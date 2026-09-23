using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading.Channels;
using NAudio.Lame;
using NAudio.Wave;

sealed class AudioBroadcaster : IDisposable
{
    readonly ConcurrentDictionary<Guid, Channel<byte[]>> clients = new();
    readonly object writerLock = new();

    WasapiLoopbackCapture? capture;
    LameMP3FileWriter? mp3Writer;
    FanoutStream? fanout;

    public bool IsReady { get; private set; }

    public void Start()
    {
        try
        {
            capture = new WasapiLoopbackCapture();
            var sourceFormat = capture.WaveFormat;
            var pcm16 = new WaveFormat(sourceFormat.SampleRate, 16, sourceFormat.Channels);

            fanout = new FanoutStream(Publish);
            mp3Writer = new LameMP3FileWriter(fanout, pcm16, LAMEPreset.STANDARD_FAST);

            capture.DataAvailable += (_, e) =>
            {
                var pcm = ConvertToPcm16(e.Buffer, e.BytesRecorded, sourceFormat);
                if (pcm.Length == 0)
                    return;

                lock (writerLock)
                {
                    mp3Writer.Write(pcm, 0, pcm.Length);
                    mp3Writer.Flush();
                }
            };

            capture.RecordingStopped += (_, __) => IsReady = false;
            capture.StartRecording();
            IsReady = true;
        }
        catch (Exception ex)
        {
            IsReady = false;
            Console.WriteLine($"Audio unavailable: {ex.Message}");
        }
    }

    void Publish(byte[] chunk)
    {
        foreach (var entry in clients)
            entry.Value.Writer.TryWrite(chunk);
    }

    public async Task StreamToAsync(NetworkStream stream, CancellationToken token)
    {
        if (!IsReady)
        {
            var text = System.Text.Encoding.UTF8.GetBytes("Audio unavailable");
            var header =
                "HTTP/1.1 503 Service Unavailable\r\n" +
                "Content-Type: text/plain\r\n" +
                $"Content-Length: {text.Length}\r\n" +
                "Connection: close\r\n\r\n";
            await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(header), token);
            await stream.WriteAsync(text, token);
            return;
        }

        var response =
            "HTTP/1.1 200 OK\r\n" +
            "Connection: close\r\n" +
            "Cache-Control: no-cache, no-store\r\n" +
            "Content-Type: audio/mpeg\r\n\r\n";

        await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(response), token);
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
        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            var output = new byte[(count / 4) * 2];
            var outputIndex = 0;

            for (var i = 0; i + 3 < count; i += 4)
            {
                var sample = BitConverter.ToSingle(input, i);
                sample = Math.Clamp(sample, -1f, 1f);
                var value = (short)(sample * short.MaxValue);

                output[outputIndex++] = (byte)(value & 0xff);
                output[outputIndex++] = (byte)((value >> 8) & 0xff);
            }

            return output;
        }

        if (format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 16)
        {
            var output = new byte[count];
            Buffer.BlockCopy(input, 0, output, 0, count);
            return output;
        }

        return Array.Empty<byte>();
    }

    public void Dispose()
    {
        try { capture?.StopRecording(); } catch { }
        try { capture?.Dispose(); } catch { }
        try
        {
            lock (writerLock)
                mp3Writer?.Dispose();
        }
        catch { }

        foreach (var client in clients.Values)
            client.Writer.TryComplete();

        try { fanout?.Dispose(); } catch { }
    }

    sealed class FanoutStream : Stream
    {
        readonly Action<byte[]> onWrite;

        public FanoutStream(Action<byte[]> onWrite) => this.onWrite = onWrite;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position
        {
            get => 0;
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (count <= 0)
                return;

            var copy = new byte[count];
            Buffer.BlockCopy(buffer, offset, copy, 0, count);
            onWrite(copy);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!buffer.IsEmpty)
                onWrite(buffer.ToArray());

            return ValueTask.CompletedTask;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}