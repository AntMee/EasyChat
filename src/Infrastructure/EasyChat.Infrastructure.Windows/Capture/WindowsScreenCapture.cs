using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using EasyChat.Contracts.Platform;
using EasyChat.Shared.Results;

namespace EasyChat.Infrastructure.Windows.Capture;

[SupportedOSPlatform("windows")]
public sealed class WindowsScreenCapture : IScreenCapture
{
    private readonly IWindowsScreenBackend _backend;

    public WindowsScreenCapture()
        : this(new GdiWindowsScreenBackend())
    {
    }

    internal WindowsScreenCapture(IWindowsScreenBackend backend)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
    }

    public async ValueTask<Result<ImageFrame>> CaptureAsync(
        ScreenCaptureRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            return await Task.Run(() => Capture(request, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Result<ImageFrame>.Failure(new Error("capture.failed", exception.Message));
        }
    }

    private Result<ImageFrame> Capture(
        ScreenCaptureRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var region = ResolveRegion(request, cancellationToken);
        var captured = _backend.Capture(region, cancellationToken);

        return Result<ImageFrame>.Success(new ImageFrame(
            captured.Width,
            captured.Height,
            captured.Stride,
            captured.DpiX,
            captured.DpiY,
            captured.Pixels));
    }

    private ScreenRegion ResolveRegion(
        ScreenCaptureRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Target == ScreenCaptureTarget.Region)
        {
            if (request.Region is not { } region || region.IsEmpty)
                throw new ArgumentException("A non-empty region is required for region capture.", nameof(request));

            return region;
        }

        var screens = _backend.GetScreens(cancellationToken);
        if (screens.Count == 0)
            throw new InvalidOperationException("No display screen is available.");

        if (request.Target == ScreenCaptureTarget.PrimaryScreen)
            return (screens.FirstOrDefault(screen => screen.IsPrimary) ?? screens[0]).Bounds;

        if (request.Target != ScreenCaptureTarget.Screen || request.Screen is not { } screenId)
            throw new ArgumentException("A screen id is required for screen capture.", nameof(request));

        return ResolveScreen(screens, screenId.Value)?.Bounds
               ?? throw new ArgumentException($"Screen '{screenId.Value}' was not found.", nameof(request));
    }

    private static WindowsScreenSnapshot? ResolveScreen(
        IReadOnlyList<WindowsScreenSnapshot> screens,
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (TryResolveIndex(value, out var index) && index >= 0 && index < screens.Count)
            return screens[index];

        return screens.FirstOrDefault(screen =>
            screen.Id.Equals(value, StringComparison.OrdinalIgnoreCase) ||
            screen.Bounds.X.ToString() == value ||
            FormatBounds(screen.Bounds).Equals(value, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryResolveIndex(string value, out int index)
    {
        if (int.TryParse(value, out index))
            return true;

        const string prefix = "screen:";
        return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
               int.TryParse(value.AsSpan(prefix.Length), out index);
    }

    private static string FormatBounds(ScreenRegion bounds) =>
        $"{bounds.X},{bounds.Y},{bounds.Width},{bounds.Height}";
}

[SupportedOSPlatform("windows")]
public sealed class WindowsScreenCatalog : IScreenCatalog
{
    private readonly IWindowsScreenBackend _backend;

    public WindowsScreenCatalog()
        : this(new GdiWindowsScreenBackend())
    {
    }

    internal WindowsScreenCatalog(IWindowsScreenBackend backend)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
    }

    public async ValueTask<IReadOnlyList<ScreenDescriptor>> GetScreensAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var screens = await Task.Run(
                () => _backend.GetScreens(cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);

        return screens
            .Select(screen => new ScreenDescriptor(
                new ScreenId(screen.Id),
                screen.Bounds,
                screen.DpiX,
                screen.DpiY,
                screen.IsPrimary))
            .ToArray();
    }
}

internal sealed record WindowsScreenSnapshot(
    string Id,
    ScreenRegion Bounds,
    double DpiX,
    double DpiY,
    bool IsPrimary);

internal sealed record WindowsCapturedFrame(
    int Width,
    int Height,
    int Stride,
    double DpiX,
    double DpiY,
    ReadOnlyMemory<byte> Pixels);

internal interface IWindowsScreenBackend
{
    IReadOnlyList<WindowsScreenSnapshot> GetScreens(CancellationToken cancellationToken);

    WindowsCapturedFrame Capture(ScreenRegion region, CancellationToken cancellationToken);
}

[SupportedOSPlatform("windows")]
internal sealed class GdiWindowsScreenBackend : IWindowsScreenBackend
{
    private const int SourceCopy = 0x00CC0020;
    private const uint PrimaryMonitor = 0x00000001;
    private const int EffectiveDpi = 0;

    public IReadOnlyList<WindowsScreenSnapshot> GetScreens(CancellationToken cancellationToken)
    {
        var screens = new List<WindowsScreenSnapshot>();
        MonitorEnumerationProcedure callback = (
            IntPtr monitor,
            IntPtr _,
            ref NativeRect _,
            IntPtr _) =>
        {
            if (cancellationToken.IsCancellationRequested)
                return false;

            var info = new MonitorInfoEx
            {
                Size = Marshal.SizeOf<MonitorInfoEx>(),
                DeviceName = string.Empty
            };

            if (!GetMonitorInfo(monitor, ref info))
                return true;

            var (dpiX, dpiY) = GetMonitorDpi(monitor);
            var bounds = new ScreenRegion(
                info.Monitor.Left,
                info.Monitor.Top,
                info.Monitor.Right - info.Monitor.Left,
                info.Monitor.Bottom - info.Monitor.Top);

            screens.Add(new WindowsScreenSnapshot(
                string.IsNullOrWhiteSpace(info.DeviceName)
                    ? $"monitor:{monitor.ToInt64():X}"
                    : info.DeviceName,
                bounds,
                dpiX,
                dpiY,
                (info.Flags & PrimaryMonitor) != 0));
            return true;
        };

        var enumerated = EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        cancellationToken.ThrowIfCancellationRequested();
        if (!enumerated)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to enumerate Windows monitors.");

        return screens;
    }

    public WindowsCapturedFrame Capture(
        ScreenRegion region,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (region.IsEmpty)
            throw new ArgumentOutOfRangeException(nameof(region));

        var desktopWindow = GetDesktopWindow();
        var desktopDc = GetWindowDC(desktopWindow);
        if (desktopDc == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to acquire the desktop device context.");

        var compatibleDc = IntPtr.Zero;
        var bitmap = IntPtr.Zero;
        var previousBitmap = IntPtr.Zero;
        try
        {
            compatibleDc = CreateCompatibleDC(desktopDc);
            if (compatibleDc == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create a compatible device context.");

            bitmap = CreateCompatibleBitmap(desktopDc, region.Width, region.Height);
            if (bitmap == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create a compatible bitmap.");

            previousBitmap = SelectObject(compatibleDc, bitmap);
            if (previousBitmap == IntPtr.Zero || previousBitmap == new IntPtr(-1))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to select the capture bitmap.");

            if (!BitBlt(
                    compatibleDc,
                    0,
                    0,
                    region.Width,
                    region.Height,
                    desktopDc,
                    region.X,
                    region.Y,
                    SourceCopy))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "BitBlt failed.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var stride = checked(region.Width * 4);
            var pixels = new byte[checked(stride * region.Height)];
            var pinnedPixels = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                var bitmapInfo = new BitmapInfo
                {
                    Size = Marshal.SizeOf<BitmapInfo>(),
                    Width = region.Width,
                    Height = -region.Height,
                    Planes = 1,
                    BitCount = 32,
                    Compression = 0
                };

                var scanLines = GetDIBits(
                    compatibleDc,
                    bitmap,
                    0,
                    (uint)region.Height,
                    pinnedPixels.AddrOfPinnedObject(),
                    ref bitmapInfo,
                    0);

                if (scanLines == 0)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "GetDIBits failed.");
            }
            finally
            {
                pinnedPixels.Free();
            }

            return new WindowsCapturedFrame(
                region.Width,
                region.Height,
                stride,
                96d,
                96d,
                pixels);
        }
        finally
        {
            if (compatibleDc != IntPtr.Zero &&
                previousBitmap != IntPtr.Zero &&
                previousBitmap != new IntPtr(-1))
            {
                SelectObject(compatibleDc, previousBitmap);
            }

            if (bitmap != IntPtr.Zero)
                DeleteObject(bitmap);
            if (compatibleDc != IntPtr.Zero)
                DeleteDC(compatibleDc);

            ReleaseDC(desktopWindow, desktopDc);
        }
    }

    private static (double DpiX, double DpiY) GetMonitorDpi(IntPtr monitor)
    {
        try
        {
            return GetDpiForMonitor(monitor, EffectiveDpi, out var dpiX, out var dpiY) == 0
                ? (dpiX, dpiY)
                : (96d, 96d);
        }
        catch (DllNotFoundException)
        {
            return (96d, 96d);
        }
        catch (EntryPointNotFoundException)
        {
            return (96d, 96d);
        }
    }

    private delegate bool MonitorEnumerationProcedure(
        IntPtr monitor,
        IntPtr monitorDc,
        ref NativeRect monitorBounds,
        IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfoEx
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public int Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public int Compression;
        public int SizeImage;
        public int XPixelsPerMeter;
        public int YPixelsPerMeter;
        public int ColorsUsed;
        public int ColorsImportant;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(
        IntPtr deviceContext,
        IntPtr clippingRectangle,
        MonitorEnumerationProcedure callback,
        IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(
        IntPtr monitor,
        int dpiType,
        out uint dpiX,
        out uint dpiY);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetWindowDC(IntPtr window);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr deviceContext);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr deviceContext, int width, int height);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr SelectObject(IntPtr deviceContext, IntPtr objectHandle);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(
        IntPtr destination,
        int destinationX,
        int destinationY,
        int width,
        int height,
        IntPtr source,
        int sourceX,
        int sourceY,
        int rasterOperation);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr deviceContext);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr objectHandle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int ReleaseDC(IntPtr window, IntPtr deviceContext);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int GetDIBits(
        IntPtr deviceContext,
        IntPtr bitmap,
        uint firstScanLine,
        uint scanLineCount,
        IntPtr bits,
        ref BitmapInfo bitmapInfo,
        uint usage);
}
