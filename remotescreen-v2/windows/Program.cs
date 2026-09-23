using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using NAudio.Lame;
using NAudio.Wave;

ApplicationConfiguration.Initialize();
Application.Run(new MainForm());

sealed class MainForm : Form
{
    readonly Label addressLabel = new() { AutoSize = true, Font = new Font("Segoe UI", 14, FontStyle.Bold) };
    readonly Label codeLabel = new() { AutoSize = true, Font = new Font("Consolas", 18, FontStyle.Bold) };
    readonly Label statusLabel = new() { AutoSize = true, Font = new Font("Segoe UI", 10) };
    readonly Label audioLabel = new() { AutoSize = true, Font = new Font("Segoe UI", 10) };
    readonly Button copyButton = new() { Text = "Copy address", AutoSize = true };
    readonly DesktopServer server;

    public MainForm()
    {
        Text = "Remote Screen Desktop V2";
        Width = 640;
        Height = 360;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;

        server = new DesktopServer();
        server.Start();

        var title = new Label {
            Text = "Remote Screen Desktop V2",
            AutoSize = true,
            Font = new Font("Segoe UI", 20, FontStyle.Bold)
        };
        var hint = new Label {
            Text = "Keep this window open while viewing from the phone.",
            AutoSize = true,
            Font = new Font("Segoe UI", 10)
        };

        addressLabel.Text = server.Address;
        codeLabel.Text = server.SessionCode;
        statusLabel.Text = "Screen stream: READY";
        audioLabel.Text = server.AudioReady ? "System audio: READY" : "System audio: unavailable";

        copyButton.Click += (_, __) => Clipboard.SetText(server.Address);

        var panel = new FlowLayoutPanel {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(28),
            AutoScroll = true
        };
        panel.Controls.Add(title);
        panel.Controls.Add(new Label { Text = "", Height = 5 });
        panel.Controls.Add(hint);
        panel.Controls.Add(new Label { Text = "PC address", AutoSize = true, Font = new Font("Segoe UI", 10, FontStyle.Bold) });
        panel.Controls.Add(addressLabel);
        panel.Controls.Add(copyButton);
        panel.Controls.Add(new Label { Text = "Session code", AutoSize = true, Font = new Font("Segoe UI", 10, FontStyle.Bold) });
        panel.Controls.Add(codeLabel);
        panel.Controls.Add(statusLabel);
        panel.Controls.Add(audioLabel);
        panel.Controls.Add(new Label {
            Text = "If port 5050 is busy after a restart, V2 automatically chooses the next free port.",
            AutoSize = true,
            MaximumSize = new Size(560, 0)
        });
        Controls.Add(panel);

        FormClosed += (_, __) => server.Dispose();
    }
}

sealed class DesktopServer : IDisposable
{
    readonly CancellationTokenSource cts = new();
    readonly ConcurrentDictionary<Guid, Channel<byte[]>> audioClients = new();
    readonly object mp3Lock = new();

    TcpListener? listener;
    Task? acceptLoop;
    WasapiLoopbackCapture? loopback;
    LameMP3FileWriter? mp3Writer;
    BroadcastWriteStream? broadcastStream;

    public string SessionCode { get; } = Random.Shared.Next(100000, 1000000).ToString();
    public int Port { get; private set; }
    public string Address => $"http://{GetLanIPv4()}:{Port}";
    public bool AudioReady { get; private set; }

    public void Start()
    {
        for (var p = 5050; p <= 5060; p++)
        {
            try
            {
                listener = new TcpListener(IPAddress.Any, p);
                listener.Start();
                Port = p;
                break;
            }
            catch (SocketException)
            {
                listener?.Stop();
                listener = null;
            }
        }

        if (listener == null)
            throw new InvalidOperationException("Could not bind ports 5050-5060.");

        acceptLoop = Task.Run(AcceptLoopAsync);
        TryStartAudio();
    }

    void TryStartAudio()
    {
        try
        {
            loopback = new WasapiLoopbackCapture();
            var src = loopback.WaveFormat;
            var pcm = new WaveFormat(src.SampleRate, 16, src.Channels);

            broadcastStream = new BroadcastWriteStream(bytes =>
            {
                foreach (var kv in audioClients)
                    kv.Value.Writer.TryWrite(bytes);
            });

            mp3Writer = new LameMP3FileWriter(broadcastStream, pcm, LAMEPreset.STANDARD_FAST);

            loopback.DataAvailable += (_, e) =>
            {
                var converted = ConvertToPcm16(e.Buffer, e.BytesRecorded, src);
                if (converted.Length == 0) return;
                lock (mp3Lock)
                    mp3Writer.Write(converted, 0, converted.Length);
            };

            loopback.RecordingStopped += (_, __) => AudioReady = false;
            loopback.StartRecording();
            AudioReady = true;
        }
        catch
        {
            AudioReady = false;
        }
    }

    async Task AcceptLoopAsync()
    {
        while (!cts.IsCancellationRequested)
        {
            TcpClient? client = null;
            try
            {
                client = await listener!.AcceptTcpClientAsync(cts.Token);
                _ = Task.Run(() => HandleClientAsync(client));
            }
            catch (OperationCanceledException) { break; }
            catch { client?.Dispose(); }
        }
    }

    async Task HandleClientAsync(TcpClient client)
    {
        using (client)
        {
            client.NoDelay = true;
            var stream = client.GetStream();
            string? firstLine = null;
            try
            {
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true);
                firstLine = await reader.ReadLineAsync();
                string? line;
                do { line = await reader.ReadLineAsync(); } while (!string.IsNullOrEmpty(line));
            }
            catch { return; }

            if (string.IsNullOrWhiteSpace(firstLine))
                return;

            var parts = firstLine.Split(' ');
            if (parts.Length < 2)
                return;

            var target = parts[1];
            var uri = new Uri("http://localhost" + target);
            var path = uri.AbsolutePath.ToLowerInvariant();

            if (path == "/health")
            {
                await WriteSimpleAsync(stream, "200 OK", "text/plain", "OK");
                return;
            }

            var token = System.Web.HttpUtility.ParseQueryString(uri.Query)["token"];
            if (token != SessionCode)
            {
                await WriteSimpleAsync(stream, "401 Unauthorized", "text/plain", "Wrong session code");
                return;
            }

            if (path == "/stream")
            {
                await StreamScreenAsync(stream);
                return;
            }

            if (path == "/audio.mp3")
            {
                await StreamAudioAsync(stream);
                return;
            }

            await WriteSimpleAsync(stream, "404 Not Found", "text/plain", "Not found");
        }
    }

    async Task StreamScreenAsync(NetworkStream stream)
    {
        var header = "HTTP/1.1 200 OK\r\n" +
                     "Connection: close\r\n" +
                     "Cache-Control: no-cache\r\n" +
                     "Pragma: no-cache\r\n" +
                     "Content-Type: multipart/x-mixed-replace; boundary=frame\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), cts.Token);

        var screen = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1280, 720);
        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

        while (!cts.IsCancellationRequested)
        {
            try
            {
                using var bmp = new Bitmap(screen.Width, screen.Height, PixelFormat.Format24bppRgb);
                using (var g = Graphics.FromImage(bmp))
                    g.CopyFromScreen(screen.Left, screen.Top, 0, 0, screen.Size, CopyPixelOperation.SourceCopy);

                using var ms = new MemoryStream();
                using var ep = new EncoderParameters(1);
                ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 65L);
                bmp.Save(ms, codec, ep);
                var jpg = ms.ToArray();

                var part = $"--frame\r\nContent-Type: image/jpeg\r\nContent-Length: {jpg.Length}\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(part), cts.Token);
                await stream.WriteAsync(jpg, cts.Token);
                await stream.WriteAsync(Encoding.ASCII.GetBytes("\r\n"), cts.Token);
                await stream.FlushAsync(cts.Token);
                await Task.Delay(67, cts.Token);
            }
            catch { break; }
        }
    }

    async Task StreamAudioAsync(NetworkStream stream)
    {
        if (!AudioReady)
        {
            await WriteSimpleAsync(stream, "503 Service Unavailable", "text/plain", "Audio unavailable");
            return;
        }

        var header = "HTTP/1.1 200 OK\r\n" +
                     "Connection: close\r\n" +
                     "Cache-Control: no-cache\r\n" +
                     "Content-Type: audio/mpeg\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), cts.Token);
        await stream.FlushAsync(cts.Token);

        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64) {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
        audioClients[id] = channel;

        try
        {
            await foreach (var chunk in channel.Reader.ReadAllAsync(cts.Token))
            {
                await stream.WriteAsync(chunk, cts.Token);
                await stream.FlushAsync(cts.Token);
            }
        }
        catch { }
        finally
        {
            audioClients.TryRemove(id, out _);
        }
    }

    static async Task WriteSimpleAsync(NetworkStream stream, string status, string contentType, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var header = $"HTTP/1.1 {status}\r\nContent-Type: {contentType}; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header));
        await stream.WriteAsync(bytes);
    }

    static string GetLanIPv4()
    {
        var candidates = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                        n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(a.Address))
            .Select(a => a.Address.ToString())
            .ToList();

        return candidates.FirstOrDefault(ip => ip.StartsWith("192.168.") || ip.StartsWith("10.") || ip.StartsWith("172."))
               ?? candidates.FirstOrDefault()
               ?? "127.0.0.1";
    }

    static byte[] ConvertToPcm16(byte[] input, int count, WaveFormat fmt)
    {
        if (fmt.Encoding == WaveFormatEncoding.IeeeFloat && fmt.BitsPerSample == 32)
        {
            var output = new byte[(count / 4) * 2];
            var oi = 0;
            for (var i = 0; i + 3 < count; i += 4)
            {
                var f = BitConverter.ToSingle(input, i);
                f = Math.Clamp(f, -1f, 1f);
                var s = (short)(f * short.MaxValue);
                output[oi++] = (byte)(s & 0xff);
                output[oi++] = (byte)((s >> 8) & 0xff);
            }
            return output;
        }

        if (fmt.Encoding == WaveFormatEncoding.Pcm && fmt.BitsPerSample == 16)
        {
            var output = new byte[count];
            Buffer.BlockCopy(input, 0, output, 0, count);
            return output;
        }

        return Array.Empty<byte>();
    }

    public void Dispose()
    {
        try { cts.Cancel(); } catch { }
        try { listener?.Stop(); } catch { }
        try { loopback?.StopRecording(); } catch { }
        try { loopback?.Dispose(); } catch { }
        try { lock (mp3Lock) mp3Writer?.Dispose(); } catch { }
        try { broadcastStream?.Dispose(); } catch { }
        foreach (var c in audioClients.Values) c.Writer.TryComplete();
        cts.Dispose();
    }
}

sealed class BroadcastWriteStream : Stream
{
    readonly Action<byte[]> onWrite;
    public BroadcastWriteStream(Action<byte[]> onWrite) => this.onWrite = onWrite;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => 0;
    public override long Position { get => 0; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override void Write(byte[] buffer, int offset, int count)
    {
        if (count <= 0) return;
        var copy = new byte[count];
        Buffer.BlockCopy(buffer, offset, copy, 0, count);
        onWrite(copy);
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!buffer.IsEmpty) onWrite(buffer.ToArray());
        return ValueTask.CompletedTask;
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}