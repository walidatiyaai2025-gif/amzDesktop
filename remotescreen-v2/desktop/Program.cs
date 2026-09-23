using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using NAudio.Wave;
using System.Windows.Forms;

namespace RemoteScreenDesktopV2;

internal static class Program
{
    private const int DefaultPort = 5050;
    private const int DiscoveryPort = 5051;
    private const int DefaultFps = 12;
    private const long DefaultJpegQuality = 70L;

    public static async Task Main()
    {
        Console.Title = "Remote Screen Desktop V2";
        Console.OutputEncoding = Encoding.UTF8;

        var port = ReadIntEnv("REMOTE_SCREEN_PORT", DefaultPort, 1024, 65535);
        var fps = ReadIntEnv("REMOTE_SCREEN_FPS", DefaultFps, 1, 30);
        var quality = ReadIntEnv("REMOTE_SCREEN_QUALITY", (int)DefaultJpegQuality, 30, 95);
        var token = SessionTokenStore.GetOrCreate();

        using var audio = new AudioBroadcaster();
        audio.TryStart();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
        builder.Logging.ClearProviders();

        var app = builder.Build();

        static bool Authorized(HttpContext context, string expected)
        {
            var supplied = context.Request.Query["token"].ToString().Trim();
            if (supplied.Length != expected.Length) return false;
            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(supplied),
                Encoding.UTF8.GetBytes(expected));
        }

        app.MapGet("/health", (HttpContext context) =>
        {
            if (!Authorized(context, token))
                return Results.Unauthorized();

            return Results.Json(new
            {
                ok = true,
                machine = Environment.MachineName,
                audio = audio.IsAvailable,
                sampleRate = audio.SampleRate,
                channels = audio.Channels,
                version = "2.0"
            });
        });

        app.MapGet("/stream", async (HttpContext context) =>
        {
            if (!Authorized(context, token))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "multipart/x-mixed-replace; boundary=frame";
            context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
            context.Response.Headers.Pragma = "no-cache";
            context.Response.Headers["X-Remote-Screen-Version"] = "2.0";

            var remote = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Video connected: {remote}");

            var frameDelay = TimeSpan.FromMilliseconds(1000.0 / fps);
            try
            {
                while (!context.RequestAborted.IsCancellationRequested)
                {
                    var started = Stopwatch.GetTimestamp();
                    byte[] jpeg;
                    try
                    {
                        jpeg = ScreenCapture.CaptureJpeg(quality);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Capture error: {ex.Message}");
                        await Task.Delay(250, context.RequestAborted);
                        continue;
                    }

                    var header = Encoding.ASCII.GetBytes(
                        $"--frame\r\nContent-Type: image/jpeg\r\nContent-Length: {jpeg.Length}\r\n\r\n");

                    await context.Response.Body.WriteAsync(header, context.RequestAborted);
                    await context.Response.Body.WriteAsync(jpeg, context.RequestAborted);
                    await context.Response.Body.WriteAsync("\r\n"u8.ToArray(), context.RequestAborted);
                    await context.Response.Body.FlushAsync(context.RequestAborted);

                    var elapsed = Stopwatch.GetElapsedTime(started);
                    var remaining = frameDelay - elapsed;
                    if (remaining > TimeSpan.Zero)
                        await Task.Delay(remaining, context.RequestAborted);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
            }
            finally
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Video disconnected: {remote}");
            }
        });

        app.MapGet("/audio", async (HttpContext context) =>
        {
            if (!Authorized(context, token))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            if (!audio.IsAvailable)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await context.Response.WriteAsync("Audio capture is not available.", context.RequestAborted);
                return;
            }

            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "application/octet-stream";
            context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
            context.Response.Headers["X-Audio-Sample-Rate"] = audio.SampleRate.ToString();
            context.Response.Headers["X-Audio-Channels"] = audio.Channels.ToString();
            context.Response.Headers["X-Audio-Encoding"] = "pcm_s16le";

            var remote = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var subscription = audio.Subscribe();
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Audio connected: {remote}");

            try
            {
                await foreach (var chunk in subscription.Reader.ReadAllAsync(context.RequestAborted))
                {
                    await context.Response.Body.WriteAsync(chunk, context.RequestAborted);
                    await context.Response.Body.FlushAsync(context.RequestAborted);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
            }
            finally
            {
                audio.Unsubscribe(subscription.Id);
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Audio disconnected: {remote}");
            }
        });

        var cts = new CancellationTokenSource();
        _ = Discovery.RunAsync(port, DiscoveryPort, cts.Token);

        try
        {
            await app.StartAsync();

            PrintStartup(port, token, fps, quality, audio);

            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
                _ = app.StopAsync();
            };

            await app.WaitForShutdownAsync();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine();
            Console.WriteLine("Remote Screen could not start.");
            Console.WriteLine(ex.Message);
            Console.ResetColor();
            Console.WriteLine();
            Console.WriteLine("Press ENTER to close.");
            Console.ReadLine();
        }
        finally
        {
            cts.Cancel();
        }
    }

    private static void PrintStartup(int port, string token, int fps, int quality, AudioBroadcaster audio)
    {
        var ips = NetworkInfo.GetLanIPv4Addresses();

        Console.Clear();
        Console.WriteLine("============================================================");
        Console.WriteLine(" Remote Screen Desktop V2 - Video + Windows Audio");
        Console.WriteLine("============================================================");

        if (ips.Count == 0)
        {
            Console.WriteLine(" PC Address  : No LAN IPv4 address detected");
        }
        else
        {
            Console.WriteLine($" PC Address  : http://{ips[0]}:{port}");
            for (var i = 1; i < ips.Count; i++)
                Console.WriteLine($" Alternative : http://{ips[i]}:{port}");
        }

        Console.WriteLine($" Session Code: {token}  (kept after restart)");
        Console.WriteLine($" Video       : {fps} fps / JPEG quality {quality}");
        Console.WriteLine($" Audio       : {audio.Status}");
        Console.WriteLine("------------------------------------------------------------");
        Console.WriteLine("The phone can use Find PC to rediscover this computer.");
        Console.WriteLine("If Windows asks about Firewall, allow Private networks.");
        Console.WriteLine("Keep this window open. Press Ctrl+C to stop.");
        Console.WriteLine("============================================================");
    }

    private static int ReadIntEnv(string name, int fallback, int min, int max)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return int.TryParse(value, out var parsed) && parsed >= min && parsed <= max
            ? parsed
            : fallback;
    }
}

internal static class SessionTokenStore
{
    public static string GetOrCreate()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RemoteScreenDesktopV2");
        Directory.CreateDirectory(dir);

        var path = Path.Combine(dir, "session-code.txt");

        try
        {
            if (File.Exists(path))
            {
                var existing = File.ReadAllText(path).Trim();
                if (Regex.IsMatch(existing, "^\\d{6}$"))
                    return existing;
            }
        }
        catch
        {
        }

        var n = RandomNumberGenerator.GetInt32(0, 1_000_000);
        var token = n.ToString("D6");

        try
        {
            File.WriteAllText(path, token);
        }
        catch
        {
        }

        return token;
    }
}

internal static class NetworkInfo
{
    public static List<string> GetLanIPv4Addresses()
    {
        var addresses = new List<(string Address, int Rank)>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
                continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;

            var rank = nic.NetworkInterfaceType switch
            {
                NetworkInterfaceType.Wireless80211 => 0,
                NetworkInterfaceType.Ethernet => 1,
                _ => 2
            };

            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork)
                    continue;

                var ip = ua.Address;
                if (IPAddress.IsLoopback(ip))
                    continue;

                var bytes = ip.GetAddressBytes();
                if (bytes[0] == 169 && bytes[1] == 254)
                    continue;

                addresses.Add((ip.ToString(), rank));
            }
        }

        return addresses
            .OrderBy(x => x.Rank)
            .ThenBy(x => x.Address)
            .Select(x => x.Address)
            .Distinct()
            .ToList();
    }
}

internal static class ScreenCapture
{
    private static readonly ImageCodecInfo JpegCodec =
        ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    public static byte[] CaptureJpeg(long quality)
    {
        var bounds = Screen.PrimaryScreen?.Bounds ?? SystemInformation.VirtualScreen;
        if (bounds.Width <= 0 || bounds.Height <= 0)
            throw new InvalidOperationException("No active screen was found.");

        using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(
                bounds.Left,
                bounds.Top,
                0,
                0,
                bounds.Size,
                CopyPixelOperation.SourceCopy | CopyPixelOperation.CaptureBlt);
        }

        using var memory = new MemoryStream();
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
        bitmap.Save(memory, JpegCodec, parameters);
        return memory.ToArray();
    }
}

internal sealed class AudioBroadcaster : IDisposable
{
    private readonly ConcurrentDictionary<Guid, Channel<byte[]>> _clients = new();
    private WasapiLoopbackCapture? _capture;

    public bool IsAvailable => _capture != null;
    public int SampleRate { get; private set; } = 48000;
    public int Channels { get; private set; } = 2;
    public string Status { get; private set; } = "Unavailable";

    public void TryStart()
    {
        try
        {
            var capture = new WasapiLoopbackCapture();
            SampleRate = capture.WaveFormat.SampleRate;
            Channels = Math.Max(1, capture.WaveFormat.Channels);

            capture.DataAvailable += (_, e) =>
            {
                var pcm16 = ConvertToPcm16(e.Buffer, e.BytesRecorded, capture.WaveFormat);
                if (pcm16.Length == 0)
                    return;

                foreach (var client in _clients.Values)
                    client.Writer.TryWrite(pcm16);
            };

            capture.RecordingStopped += (_, e) =>
            {
                if (e.Exception != null)
                    Console.WriteLine($"Audio stopped: {e.Exception.Message}");
            };

            capture.StartRecording();
            _capture = capture;
            Status = $"WASAPI Loopback {SampleRate} Hz / {Channels} ch";
        }
        catch (Exception ex)
        {
            Status = $"Unavailable ({ex.Message})";
            _capture = null;
        }
    }

    public (Guid Id, ChannelReader<byte[]> Reader) Subscribe()
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(24)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });
        _clients[id] = channel;
        return (id, channel.Reader);
    }

    public void Unsubscribe(Guid id)
    {
        if (_clients.TryRemove(id, out var channel))
            channel.Writer.TryComplete();
    }

    private static byte[] ConvertToPcm16(byte[] input, int bytesRecorded, WaveFormat format)
    {
        if (bytesRecorded <= 0)
            return Array.Empty<byte>();

        if (format.BitsPerSample == 16)
        {
            var copy = new byte[bytesRecorded];
            Buffer.BlockCopy(input, 0, copy, 0, bytesRecorded);
            return copy;
        }

        if (format.BitsPerSample == 32)
        {
            var samples = bytesRecorded / 4;
            var output = new byte[samples * 2];

            for (var i = 0; i < samples; i++)
            {
                var value = BitConverter.ToSingle(input, i * 4);
                if (float.IsNaN(value) || float.IsInfinity(value))
                    value = 0;
                value = Math.Clamp(value, -1f, 1f);
                var sample = (short)Math.Round(value * short.MaxValue);
                BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(i * 2, 2), sample);
            }

            return output;
        }

        if (format.BitsPerSample == 24)
        {
            var samples = bytesRecorded / 3;
            var output = new byte[samples * 2];

            for (var i = 0; i < samples; i++)
            {
                var p = i * 3;
                var value = input[p] | (input[p + 1] << 8) | (input[p + 2] << 16);
                if ((value & 0x800000) != 0)
                    value |= unchecked((int)0xFF000000);

                var sample = (short)(value >> 8);
                BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(i * 2, 2), sample);
            }

            return output;
        }

        return Array.Empty<byte>();
    }

    public void Dispose()
    {
        foreach (var item in _clients)
            item.Value.Writer.TryComplete();
        _clients.Clear();

        if (_capture != null)
        {
            try { _capture.StopRecording(); } catch { }
            _capture.Dispose();
            _capture = null;
        }
    }
}

internal static class Discovery
{
    public static async Task RunAsync(int httpPort, int discoveryPort, CancellationToken cancellationToken)
    {
        UdpClient? udp = null;
        try
        {
            udp = new UdpClient(new IPEndPoint(IPAddress.Any, discoveryPort));
            var expected = Encoding.UTF8.GetBytes("REMOTE_SCREEN_DISCOVER_V2");
            var response = Encoding.UTF8.GetBytes($"REMOTE_SCREEN_V2|{httpPort}|{Environment.MachineName}");

            while (!cancellationToken.IsCancellationRequested)
            {
                var receiveTask = udp.ReceiveAsync(cancellationToken).AsTask();
                var result = await receiveTask;

                if (result.Buffer.AsSpan().SequenceEqual(expected))
                    await udp.SendAsync(response, result.RemoteEndPoint, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Console.WriteLine($"LAN discovery unavailable: {ex.Message}");
        }
        finally
        {
            udp?.Dispose();
        }
    }
}
