using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using MapFlags = Vortice.Direct3D11.MapFlags;
using DxgiResultCode = Vortice.DXGI.ResultCode;

enum CaptureBackendMode
{
    Auto,
    Dxgi,
    Gdi
}

interface ICaptureBackend : IDisposable
{
    string Name { get; }
    int Width { get; }
    int Height { get; }
    byte[] CaptureJpeg(long quality);
}

static class CaptureBackendFactory
{
    public static ICaptureBackend Create(CaptureBackendMode mode, out string diagnostics)
    {
        diagnostics = "";

        if (mode is CaptureBackendMode.Auto or CaptureBackendMode.Dxgi)
        {
            try
            {
                var dxgi = new DxgiCaptureBackend();
                diagnostics = "DXGI Desktop Duplication initialized successfully.";
                return dxgi;
            }
            catch (Exception ex)
            {
                if (mode == CaptureBackendMode.Dxgi)
                    throw new InvalidOperationException($"DXGI initialization failed: {ex.Message}", ex);

                diagnostics = $"DXGI unavailable ({ex.Message}). Fell back to GDI.";
            }
        }

        return new GdiCaptureBackend();
    }
}

sealed class GdiCaptureBackend : ICaptureBackend
{
    readonly Rectangle bounds;

    public string Name => "GDI CopyFromScreen";
    public int Width => bounds.Width;
    public int Height => bounds.Height;

    public GdiCaptureBackend()
    {
        bounds = Screen.PrimaryScreen?.Bounds
            ?? throw new InvalidOperationException("Primary screen not found.");
    }

    public byte[] CaptureJpeg(long quality)
    {
        using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(
                bounds.Location,
                Point.Empty,
                bounds.Size,
                CopyPixelOperation.SourceCopy);
        }

        return JpegHelper.Encode(bitmap, quality);
    }

    public void Dispose() { }
}

sealed class DxgiCaptureBackend : ICaptureBackend
{
    static readonly FeatureLevel[] FeatureLevels =
    {
        FeatureLevel.Level_11_1,
        FeatureLevel.Level_11_0,
        FeatureLevel.Level_10_1,
        FeatureLevel.Level_10_0
    };

    readonly IDXGIFactory1 factory;
    readonly IDXGIAdapter1 adapter;
    readonly IDXGIOutput output;
    readonly ID3D11Device device;
    readonly ID3D11DeviceContext context;
    readonly IDXGIOutputDuplication duplication;
    readonly ID3D11Texture2D stagingTexture;

    byte[]? lastJpeg;
    bool frameHeld;

    public string Name => "DXGI Desktop Duplication";
    public int Width { get; }
    public int Height { get; }

    public DxgiCaptureBackend()
    {
        DXGI.CreateDXGIFactory1(out factory!).CheckError();

        IDXGIAdapter1? selectedAdapter = null;
        IDXGIOutput? selectedOutput = null;

        for (var ai = 0; factory.EnumAdapters1(ai, out IDXGIAdapter1 currentAdapter).Success; ai++)
        {
            var keepAdapter = false;

            for (var oi = 0; currentAdapter.EnumOutputs(oi, out IDXGIOutput currentOutput).Success; oi++)
            {
                var desc = currentOutput.Description;
                var r = desc.DesktopCoordinates;

                if (r.Left == 0 && r.Top == 0)
                {
                    selectedAdapter = currentAdapter;
                    selectedOutput = currentOutput;
                    keepAdapter = true;
                    break;
                }

                currentOutput.Dispose();
            }

            if (keepAdapter)
                break;

            currentAdapter.Dispose();
        }

        adapter = selectedAdapter
            ?? throw new InvalidOperationException("DXGI could not find the primary desktop output.");

        output = selectedOutput
            ?? throw new InvalidOperationException("DXGI primary output was not found.");

        var outputDesc = output.Description;
        Width = outputDesc.DesktopCoordinates.Right - outputDesc.DesktopCoordinates.Left;
        Height = outputDesc.DesktopCoordinates.Bottom - outputDesc.DesktopCoordinates.Top;

        if (Width <= 0 || Height <= 0)
            throw new InvalidOperationException("DXGI returned an invalid desktop size.");

        D3D11.D3D11CreateDevice(
            adapter,
            DriverType.Unknown,
            DeviceCreationFlags.None,
            FeatureLevels,
            out device!).CheckError();

        context = device.ImmediateContext;

        using var output5 = output.QueryInterface<IDXGIOutput5>();
        duplication = output5.DuplicateOutput(device);

        var textureDesc = new Texture2DDescription
        {
            CPUAccessFlags = CpuAccessFlags.Read,
            BindFlags = BindFlags.None,
            Format = Format.B8G8R8A8_UNorm,
            Width = Width,
            Height = Height,
            MiscFlags = ResourceOptionFlags.None,
            MipLevels = 1,
            ArraySize = 1,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging
        };

        stagingTexture = device.CreateTexture2D(textureDesc);
    }

    public byte[] CaptureJpeg(long quality)
    {
        IDXGIResource? resource = null;
        try
        {
            var result = duplication.AcquireNextFrame(
                120,
                out OutduplFrameInfo frameInfo,
                out resource);

            if (result.Failure)
            {
                if (result.Code == DxgiResultCode.WaitTimeout.Code && lastJpeg is not null)
                    return lastJpeg;

                result.CheckError();
            }

            frameHeld = true;

            if (resource is null || frameInfo.LastPresentTime == 0)
            {
                if (lastJpeg is not null)
                    return lastJpeg;

                throw new InvalidOperationException("DXGI did not provide a desktop frame.");
            }

            using var screenTexture = resource.QueryInterface<ID3D11Texture2D>();
            context.CopyResource(stagingTexture, screenTexture);

            var mapped = context.Map(stagingTexture, 0, MapMode.Read, MapFlags.None);
            try
            {
                using var bitmap = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
                var rect = new Rectangle(0, 0, Width, Height);
                var data = bitmap.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

                try
                {
                    unsafe
                    {
                        var source = mapped.AsSpan(mapped.RowPitch * Height);
                        var destination = new Span<byte>((void*)data.Scan0, Math.Abs(data.Stride) * Height);
                        var bytesPerRow = Width * 4;

                        for (var y = 0; y < Height; y++)
                        {
                            source
                                .Slice(y * mapped.RowPitch, bytesPerRow)
                                .CopyTo(destination.Slice(y * data.Stride, bytesPerRow));
                        }
                    }
                }
                finally
                {
                    bitmap.UnlockBits(data);
                }

                lastJpeg = JpegHelper.Encode(bitmap, quality);
                return lastJpeg;
            }
            finally
            {
                context.Unmap(stagingTexture, 0);
            }
        }
        catch (SharpGenException ex) when (ex.ResultCode.Code == DxgiResultCode.WaitTimeout.Code && lastJpeg is not null)
        {
            return lastJpeg;
        }
        finally
        {
            resource?.Dispose();

            if (frameHeld)
            {
                try { duplication.ReleaseFrame(); } catch { }
                frameHeld = false;
            }
        }
    }

    public void Dispose()
    {
        try
        {
            if (frameHeld)
            {
                duplication.ReleaseFrame();
                frameHeld = false;
            }
        }
        catch { }

        stagingTexture.Dispose();
        duplication.Dispose();
        context.Dispose();
        device.Dispose();
        output.Dispose();
        adapter.Dispose();
        factory.Dispose();
    }
}

static class JpegHelper
{
    public static byte[] Encode(Bitmap bitmap, long quality)
    {
        using var memory = new MemoryStream();
        var codec = ImageCodecInfo.GetImageEncoders()
            .First(x => x.FormatID == ImageFormat.Jpeg.Guid);

        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(
            System.Drawing.Imaging.Encoder.Quality,
            Math.Clamp(quality, 20L, 95L));

        bitmap.Save(memory, codec, parameters);
        return memory.ToArray();
    }
}
