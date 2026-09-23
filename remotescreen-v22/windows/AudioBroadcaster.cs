using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using NAudio.Wave;

sealed class AudioBroadcaster : IDisposable
{
    readonly ConcurrentDictionary<Guid, Channel<byte[]>> clients = new();
    WasapiLoopbackCapture? capture;

    public bool IsReady { get; private set; }
    public string? LastError { get; private set; }
    public int SampleRate { get; private set; } = 48000;
    public int Channels { get; private set; } = 2;

    public void Start()
    {
        try
        {
            capture = new WasapiLoopbackCapture();
            SampleRate = capture.WaveFormat.SampleRate;
            Channels = Math.Clamp(capture.WaveFormat.Channels, 1, 2);

            capture.DataAvailable += (_, e) =>
            {
                try
                {
                    var pcm = ConvertToPcm16(e.Buffer, e.BytesRecorded, capture.WaveFormat);
                    if (pcm.Length == 0)
                        return;

                    foreach (var entry in clients)
                        entry.Value.Writer.TryWrite(pcm);
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                }
            };

            capture.RecordingStopped += (_, e) =>
            {
                IsReady = false;
                if (e.Exception is not null)
                    LastError = e.Exception.Message;
            };

            capture.StartRecording();
            IsReady = true;
            LastError = null;
        }
        catch (Exception ex)
        {
            IsReady = false;
            LastError = ex.Message;
            Console.WriteLine($"Audio unavailable: {ex.Message}");
        }
    }

    public async Task StreamToAsync(NetworkStream stream, CancellationToken token)
    {
        if (!IsReady)
        {
            var body = Encoding.UTF8.GetBytes(LastError ?? "Audio unavailable");
            var header =
                "HTTP/1.1 503 Service Unavailable\r\n" +
                "Content-Type: text/plain; charset=utf-8\r\n" +
                $"Content-Length: {body.Length}\r\n" +
                "Connection: close\r\n\r\n";

            await stream.WriteAsync(Encoding.ASCII.GetBytes(header), token);
            await stream.WriteAsync(body, token);
            return;
        }

        var response =
            "HTTP/1.1 200 OK\r\n" +
            "Connection: close\r\n" +
            "Cache-Control: no-cache, no-store\r\n" +
            "Content-Type: application/octet-stream\r\n" +
            $"X-Sample-Rate: {SampleRate}\r\n" +
            $"X-Channels: {Channels}\r\n" +
            "X-Audio-Format: PCM_S16LE\r\n\r\n";

        await stream.WriteAsync(Encoding.ASCII.GetBytes(response), token);
        await stream.FlushAsync(token);

        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(128)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

        clients[id] = channel;

        try
        {
            await foreach (var chunk in channel.Reader.ReadAllAsync(token))
            {
                await stream.WriteAsync(chunk, token);
                await stream.FlushAsync(token);
            }
        }
        catch
        {
        }
        finally
        {
            clients.TryRemove(id, out _);
        }
    }

    static byte[] ConvertToPcm16(byte[] input, int count, WaveFormat format)
    {
        var bits = format.BitsPerSample;

        if (bits == 32)
        {
            // Windows loopback is normally 32-bit IEEE float, including
            // WAVE_FORMAT_EXTENSIBLE devices whose subtype is float.
            var output = new byte[(count / 4) * 2];
            var oi = 0;

            for (var i = 0; i + 3 < count; i += 4)
            {
                var sample = BitConverter.ToSingle(input, i);

                if (float.IsNaN(sample) || float.IsInfinity(sample))
                    sample = 0;

                sample = Math.Clamp(sample, -1f, 1f);
                var value = (short)Math.Round(sample * short.MaxValue);

                output[oi++] = (byte)(value & 0xff);
                output[oi++] = (byte)((value >> 8) & 0xff);
            }

            return output;
        }

        if (bits == 16)
        {
            var output = new byte[count];
            Buffer.BlockCopy(input, 0, output, 0, count);
            return output;
        }

        if (bits == 24)
        {
            var samples = count / 3;
            var output = new byte[samples * 2];
            var oi = 0;

            for (var i = 0; i + 2 < count; i += 3)
            {
                var value = input[i] | (input[i + 1] << 8) | (input[i + 2] << 16);
                if ((value & 0x800000) != 0)
                    value |= unchecked((int)0xff000000);

                var sample16 = (short)(value >> 8);
                output[oi++] = (byte)(sample16 & 0xff);
                output[oi++] = (byte)((sample16 >> 8) & 0xff);
            }

            return output;
        }

        throw new NotSupportedException(
            $"Unsupported loopback format: {format.Encoding}, {bits}-bit, {format.SampleRate} Hz, {format.Channels} ch");
    }

    public void Dispose()
    {
        try { capture?.StopRecording(); } catch { }
        try { capture?.Dispose(); } catch { }

        foreach (var client in clients.Values)
            client.Writer.TryComplete();
    }
}