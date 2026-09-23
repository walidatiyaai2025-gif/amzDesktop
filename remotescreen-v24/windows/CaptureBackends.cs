using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Lvhang.WindowsCapture;
using MapFlags = Vortice.Direct3D11.MapFlags;
using DxgiResultCode = Vortice.DXGI.ResultCode;

enum CaptureBackendMode
{
    Auto,
    Dxgi,
    WindowsGraphicsCapture,
    BitBlt,
    BitBltCaptureBlt,
    GdiCopyFromScreen,
    PrintWindow
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
    public static ICaptureBackend Create(
        CaptureBackendMode mode,
        IntPtr ownerHwnd,
        out string diagnostics)
    {
        diagnostics = "";

        if (mode == CaptureBackendMode.Auto)
        {
            try
            {
                var dxgi = new DxgiCaptureBackend();
                diagnostics = "DXGI Desktop Duplication initialized successfully.";
                return dxgi;
            }
            catch (Exception dxgiEx)
            {
                try
                {
                    var bitblt = new BitBltCaptureBackend(includeCaptureBlt: true);
                    diagnostics =
                        $"DXGI unavailable ({dxgiEx.Message}). Using GDI BitBlt + CAPTUREBLT.";
                    return bitblt;
                }
                catch (Exception bitbltEx)
                {
                    diagnostics =
                        $"DXGI unavailable ({dxgiEx.Message}). BitBlt unavailable ({bitbltEx.Message}). Using CopyFromScreen.";
                    return new GdiCopyFromScreenBackend();
                }
            }
        }

        return mode switch
        {
            CaptureBackendMode.Dxgi =>
                new DxgiCaptureBackend(),

            CaptureBackendMode.WindowsGraphicsCapture =>
                new WindowsGraphicsCaptureBackend(ownerHwnd),

            CaptureBackendMode.BitBlt =>
                new BitBltCaptureBackend(includeCaptureBlt: false),

            CaptureBackendMode.BitBltCaptureBlt =>
                new BitBltCaptureBackend(includeCaptureBlt: true),

            CaptureBackendMode.GdiCopyFromScreen =>
                new GdiCopyFromScreenBackend(),

            CaptureBackendMode.PrintWindow =>
                new PrintWindowCaptureBackend(),

            _ => new GdiCopyFromScreenBackend()
        };
    }
}

sealed class GdiCopyFromScreenBackend : ICaptureBackend
{
    readonly Rectangle bounds;

    public string Name => "GDI CopyFromScreen";
    public int Width => bounds.Width;
    public int Height => bounds.Height;

    public GdiCopyFromScreenBackend()
    {
        bounds = Screen.PrimaryScreen?.Bounds
            ?? throw new InvalidOperationException("Primary screen not found.");
    }

    public byte[] CaptureJpeg(long quality)
    {
        using var bitmap = new Bitmap(
            bounds.Width,
            bounds.Height,
            PixelFormat.Format24bppRgb);

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

sealed class BitBltCaptureBackend : ICaptureBackend
{
    const uint SRCCOPY = 0x00CC0020;
    const uint CAPTUREBLT = 0x40000000;

    readonly Rectangle bounds;
    readonly bool includeCaptureBlt;

    public string Name => includeCaptureBlt
        ? "GDI BitBlt (SRCCOPY + CAPTUREBLT)"
        : "GDI BitBlt (SRCCOPY)";

    public int Width => bounds.Width;
    public int Height => bounds.Height;

    public BitBltCaptureBackend(bool includeCaptureBlt)
    {
        this.includeCaptureBlt = includeCaptureBlt;

        bounds = Screen.PrimaryScreen?.Bounds
            ?? throw new InvalidOperationException("Primary screen not found.");
    }

    public byte[] CaptureJpeg(long quality)
    {
        using var bitmap = new Bitmap(
            bounds.Width,
            bounds.Height,
            PixelFormat.Format32bppArgb);

        using var graphics = Graphics.FromImage(bitmap);

        var screenDc = NativeMethods.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
            throw new InvalidOperationException("GetDC failed.");

        var destinationDc = graphics.GetHdc();

        try
        {
            var rop = includeCaptureBlt
                ? SRCCOPY | CAPTUREBLT
                : SRCCOPY;

            if (!NativeMethods.BitBlt(
                    destinationDc,
                    0,
                    0,
                    bounds.Width,
                    bounds.Height,
                    screenDc,
                    bounds.Left,
                    bounds.Top,
                    rop))
            {
                throw new InvalidOperationException(
                    $"BitBlt failed. Win32={Marshal.GetLastWin32Error()}");
            }
        }
        finally
        {
            graphics.ReleaseHdc(destinationDc);
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }

        return JpegHelper.Encode(bitmap, quality);
    }

    public void Dispose() { }
}

sealed class PrintWindowCaptureBackend : ICaptureBackend
{
    const uint PW_RENDERFULLCONTENT = 0x00000002;

    int width = 1280;
    int height = 720;

    public string Name => "PrintWindow (current foreground window)";
    public int Width => width;
    public int Height => height;

    public byte[] CaptureJpeg(long quality)
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
            throw new InvalidOperationException("No foreground window found.");

        if (!NativeMethods.GetWindowRect(hwnd, out var rect))
            throw new InvalidOperationException(
                $"GetWindowRect failed. Win32={Marshal.GetLastWin32Error()}");

        width = Math.Max(1, rect.Right - rect.Left);
        height = Math.Max(1, rect.Bottom - rect.Top);

        using var bitmap = new Bitmap(
            width,
            height,
            PixelFormat.Format32bppArgb);

        using var graphics = Graphics.FromImage(bitmap);
        var hdc = graphics.GetHdc();

        try
        {
            var ok = NativeMethods.PrintWindow(
                hwnd,
                hdc,
                PW_RENDERFULLCONTENT);

            if (!ok)
                ok = NativeMethods.PrintWindow(hwnd, hdc, 0);

            if (!ok)
            {
                throw new InvalidOperationException(
                    $"PrintWindow failed. Win32={Marshal.GetLastWin32Error()}");
            }
        }
        finally
        {
            graphics.ReleaseHdc(hdc);
        }

        return JpegHelper.Encode(bitmap, quality);
    }

    public void Dispose() { }
}

sealed class WindowsGraphicsCaptureBackend : ICaptureBackend
{
    readonly object sync = new();
    readonly WindowsCaptureSession session;
    readonly System.Windows.Window pickerOwner;

    byte[]? latestJpeg;
    int width;
    int height;
    bool disposed;

    public string Name => "Windows Graphics Capture (system picker)";
    public int Width => width;
    public int Height => height;

    public WindowsGraphicsCaptureBackend(IntPtr ownerHwnd)
    {
        var primary = Screen.PrimaryScreen?.Bounds
            ?? new Rectangle(0, 0, 1280, 720);

        width = primary.Width;
        height = primary.Height;

        // Lvhang.WindowsCapture 1.1.0 expects a WPF Window as the picker owner.
        // Create a tiny hidden WPF owner and force its HWND to exist.
        pickerOwner = new System.Windows.Window
        {
            Width = 1,
            Height = 1,
            Left = -10000,
            Top = -10000,
            ShowInTaskbar = false,
            WindowStyle = System.Windows.WindowStyle.None,
            ResizeMode = System.Windows.ResizeMode.NoResize,
            Opacity = 0
        };

        _ = new System.Windows.Interop.WindowInteropHelper(pickerOwner)
            .EnsureHandle();

        session = new WindowsCaptureSession(
            pickerOwner,
            new WindowsCaptureSessionOptions
            {
                MinFrameInterval = 60,
                IsManual = false
            });

        session.OnFrameArrived += OnFrameArrived;

        // This is the normal Windows Graphics Capture picker.
        // Select the full display when it appears.
        session.PickAndCapture();
    }

    void OnFrameArrived(
        Windows.Graphics.Capture.Direct3D11CaptureFrame frame,
        Action nextFrame)
    {
        if (disposed)
            return;

        try
        {
            using var softwareBitmap =
                frame.ToSoftwareBitmapAsync()
                    .GetAwaiter()
                    .GetResult();

            using var tempBitmap =
                softwareBitmap
                    .ToBitmapAsync()
                    .GetAwaiter()
                    .GetResult();

            using var bitmap = new Bitmap(tempBitmap);

            width = bitmap.Width;
            height = bitmap.Height;

            var jpeg = JpegHelper.Encode(bitmap, 70);

            lock (sync)
                latestJpeg = jpeg;
        }
        catch
        {
            // Streaming loop will report no frame if WGC did not produce one.
        }
    }

    public byte[] CaptureJpeg(long quality)
    {
        for (var i = 0; i < 150; i++)
        {
            lock (sync)
            {
                if (latestJpeg is not null)
                    return latestJpeg;
            }

            Thread.Sleep(100);
        }

        throw new InvalidOperationException(
            "Windows Graphics Capture has no frame yet. Select the full display in the Windows picker.");
    }

    public void Dispose()
    {
        disposed = true;

        try { session.StopCapture(); } catch { }
        try { session.Dispose(); } catch { }
        try { pickerOwner.Close(); } catch { }
    }
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

        for (var ai = 0;
             factory.EnumAdapters1(ai, out IDXGIAdapter1 currentAdapter).Success;
             ai++)
        {
            var keepAdapter = false;

            for (var oi = 0;
                 currentAdapter.EnumOutputs(
                     oi,
                     out IDXGIOutput currentOutput).Success;
                 oi++)
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
            ?? throw new InvalidOperationException(
                "DXGI could not find the primary desktop output.");

        output = selectedOutput
            ?? throw new InvalidOperationException(
                "DXGI primary output was not found.");

        var outputDesc = output.Description;

        Width =
            outputDesc.DesktopCoordinates.Right -
            outputDesc.DesktopCoordinates.Left;

        Height =
            outputDesc.DesktopCoordinates.Bottom -
            outputDesc.DesktopCoordinates.Top;

        if (Width <= 0 || Height <= 0)
        {
            throw new InvalidOperationException(
                "DXGI returned an invalid desktop size.");
        }

        D3D11.D3D11CreateDevice(
            adapter,
            DriverType.Unknown,
            DeviceCreationFlags.None,
            FeatureLevels,
            out device!).CheckError();

        context = device.ImmediateContext;

        using var output5 =
            output.QueryInterface<IDXGIOutput5>();

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

        stagingTexture =
            device.CreateTexture2D(textureDesc);
    }

    public byte[] CaptureJpeg(long quality)
    {
        IDXGIResource? resource = null;

        try
        {
            var result =
                duplication.AcquireNextFrame(
                    120,
                    out OutduplFrameInfo frameInfo,
                    out resource);

            if (result.Failure)
            {
                if (result.Code ==
                        DxgiResultCode.WaitTimeout.Code &&
                    lastJpeg is not null)
                {
                    return lastJpeg;
                }

                result.CheckError();
            }

            frameHeld = true;

            if (resource is null ||
                frameInfo.LastPresentTime == 0)
            {
                if (lastJpeg is not null)
                    return lastJpeg;

                throw new InvalidOperationException(
                    "DXGI did not provide a desktop frame.");
            }

            using var screenTexture =
                resource.QueryInterface<ID3D11Texture2D>();

            context.CopyResource(
                stagingTexture,
                screenTexture);

            var mapped =
                context.Map(
                    stagingTexture,
                    0,
                    MapMode.Read,
                    MapFlags.None);

            try
            {
                using var bitmap =
                    new Bitmap(
                        Width,
                        Height,
                        PixelFormat.Format32bppArgb);

                var rect =
                    new Rectangle(
                        0,
                        0,
                        Width,
                        Height);

                var data =
                    bitmap.LockBits(
                        rect,
                        ImageLockMode.WriteOnly,
                        PixelFormat.Format32bppArgb);

                try
                {
                    unsafe
                    {
                        var source =
                            mapped.AsSpan(
                                mapped.RowPitch * Height);

                        var destination =
                            new Span<byte>(
                                (void*)data.Scan0,
                                Math.Abs(data.Stride) * Height);

                        var bytesPerRow = Width * 4;

                        for (var y = 0; y < Height; y++)
                        {
                            source
                                .Slice(
                                    y * mapped.RowPitch,
                                    bytesPerRow)
                                .CopyTo(
                                    destination.Slice(
                                        y * data.Stride,
                                        bytesPerRow));
                        }
                    }
                }
                finally
                {
                    bitmap.UnlockBits(data);
                }

                lastJpeg =
                    JpegHelper.Encode(
                        bitmap,
                        quality);

                return lastJpeg;
            }
            finally
            {
                context.Unmap(
                    stagingTexture,
                    0);
            }
        }
        catch (SharpGenException ex)
            when (
                ex.ResultCode.Code ==
                    DxgiResultCode.WaitTimeout.Code &&
                lastJpeg is not null)
        {
            return lastJpeg;
        }
        finally
        {
            resource?.Dispose();

            if (frameHeld)
            {
                try
                {
                    duplication.ReleaseFrame();
                }
                catch { }

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
    public static byte[] Encode(
        Bitmap bitmap,
        long quality)
    {
        using var memory =
            new MemoryStream();

        var codec =
            ImageCodecInfo
                .GetImageEncoders()
                .First(
                    x =>
                        x.FormatID ==
                        ImageFormat.Jpeg.Guid);

        using var parameters =
            new EncoderParameters(1);

        parameters.Param[0] =
            new EncoderParameter(
                System.Drawing.Imaging.Encoder.Quality,
                Math.Clamp(
                    quality,
                    20L,
                    95L));

        bitmap.Save(
            memory,
            codec,
            parameters);

        return memory.ToArray();
    }
}

static class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport(
        "user32.dll",
        SetLastError = true)]
    public static extern IntPtr GetDC(
        IntPtr hwnd);

    [DllImport(
        "user32.dll")]
    public static extern int ReleaseDC(
        IntPtr hwnd,
        IntPtr hdc);

    [DllImport(
        "gdi32.dll",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool BitBlt(
        IntPtr hdcDest,
        int xDest,
        int yDest,
        int width,
        int height,
        IntPtr hdcSrc,
        int xSrc,
        int ySrc,
        uint rop);

    [DllImport(
        "user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport(
        "user32.dll",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(
        IntPtr hwnd,
        out RECT rect);

    [DllImport(
        "user32.dll",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PrintWindow(
        IntPtr hwnd,
        IntPtr hdcBlt,
        uint flags);
}
