using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Sockets;
using System.Text;

sealed class RemoteServer : IDisposable
{
    readonly CancellationTokenSource cts = new();
    TcpListener? listener;
    Task? acceptLoop;
    readonly AudioBroadcaster audio = new();

    public string SessionCode { get; } = Random.Shared.Next(100000, 1000000).ToString();
    public int Port { get; private set; }
    public string Address => $"http://{NetworkInfo.GetLanIPv4()}:{Port}";
    public bool AudioReady => audio.IsReady;

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

        if (listener is null)
            throw new InvalidOperationException("No free TCP port found from 5050 to 5060.");

        audio.Start();
        acceptLoop = Task.Run(AcceptLoopAsync);
    }

    async Task AcceptLoopAsync()
    {
        while (!cts.IsCancellationRequested)
        {
            try
            {
                var client = await listener!.AcceptTcpClientAsync(cts.Token);
                _ = Task.Run(() => HandleClientAsync(client));
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Server error: {ex.Message}");
            }
        }
    }

    async Task HandleClientAsync(TcpClient client)
    {
        using (client)
        {
            client.NoDelay = true;
            var stream = client.GetStream();

            string? requestLine;
            try
            {
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true);
                requestLine = await reader.ReadLineAsync();

                string? line;
                do
                {
                    line = await reader.ReadLineAsync();
                }
                while (!string.IsNullOrEmpty(line));
            }
            catch
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(requestLine))
                return;

            var parts = requestLine.Split(' ');
            if (parts.Length < 2)
                return;

            var uri = new Uri("http://localhost" + parts[1]);
            var path = uri.AbsolutePath.ToLowerInvariant();

            if (path == "/health")
            {
                await WriteTextAsync(stream, "200 OK", "OK");
                return;
            }

            var token = GetQueryValue(uri.Query, "token");
            if (!string.Equals(token, SessionCode, StringComparison.Ordinal))
            {
                await WriteTextAsync(stream, "401 Unauthorized", "Wrong session code");
                return;
            }

            if (path == "/stream")
            {
                await StreamScreenAsync(stream);
                return;
            }

            if (path == "/audio.mp3")
            {
                await audio.StreamToAsync(stream, cts.Token);
                return;
            }

            await WriteTextAsync(stream, "404 Not Found", "Not found");
        }
    }

    async Task StreamScreenAsync(NetworkStream stream)
    {
        var response =
            "HTTP/1.1 200 OK\r\n" +
            "Connection: close\r\n" +
            "Cache-Control: no-cache, no-store\r\n" +
            "Pragma: no-cache\r\n" +
            "Content-Type: multipart/x-mixed-replace; boundary=frame\r\n\r\n";

        await stream.WriteAsync(Encoding.ASCII.GetBytes(response), cts.Token);

        var screen = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1280, 720);
        var jpegCodec = ImageCodecInfo.GetImageEncoders().First(x => x.FormatID == ImageFormat.Jpeg.Guid);
        using var encoderParams = new EncoderParameters(1);
        encoderParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 65L);

        while (!cts.IsCancellationRequested)
        {
            try
            {
                using var bitmap = new Bitmap(screen.Width, screen.Height, PixelFormat.Format24bppRgb);
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    // V2.1 fix:
                    // CopyFromScreen validates CopyPixelOperation values. Combining
                    // SourceCopy with CaptureBlt produced 0x40CC0020 and threw every frame.
                    graphics.CopyFromScreen(
                        screen.Location,
                        Point.Empty,
                        screen.Size,
                        CopyPixelOperation.SourceCopy);
                }

                using var memory = new MemoryStream();
                bitmap.Save(memory, jpegCodec, encoderParams);
                var jpeg = memory.ToArray();

                var partHeader =
                    $"--frame\r\nContent-Type: image/jpeg\r\nContent-Length: {jpeg.Length}\r\n\r\n";

                await stream.WriteAsync(Encoding.ASCII.GetBytes(partHeader), cts.Token);
                await stream.WriteAsync(jpeg, cts.Token);
                await stream.WriteAsync(Encoding.ASCII.GetBytes("\r\n"), cts.Token);
                await stream.FlushAsync(cts.Token);

                await Task.Delay(67, cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException)
            {
                break;
            }
            catch (SocketException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Capture error: {ex.Message}");
                await Task.Delay(1000, cts.Token);
            }
        }
    }

    static string? GetQueryValue(string query, string key)
    {
        if (string.IsNullOrWhiteSpace(query))
            return null;

        var raw = query.TrimStart('?');
        foreach (var pair in raw.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var bits = pair.Split('=', 2);
            if (bits.Length == 2 &&
                string.Equals(Uri.UnescapeDataString(bits[0]), key, StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(bits[1]);
            }
        }

        return null;
    }

    static async Task WriteTextAsync(NetworkStream stream, string status, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var header =
            $"HTTP/1.1 {status}\r\n" +
            "Content-Type: text/plain; charset=utf-8\r\n" +
            $"Content-Length: {bytes.Length}\r\n" +
            "Connection: close\r\n\r\n";

        await stream.WriteAsync(Encoding.ASCII.GetBytes(header));
        await stream.WriteAsync(bytes);
    }

    public void Dispose()
    {
        try { cts.Cancel(); } catch { }
        try { listener?.Stop(); } catch { }
        try { audio.Dispose(); } catch { }
        try { acceptLoop?.Wait(TimeSpan.FromSeconds(1)); } catch { }
        cts.Dispose();
    }
}