using System.Runtime.Versioning;
using EasyChat.Contracts.Platform;
using EasyChat.Infrastructure.Windows.Capture;

namespace EasyChat.Infrastructure.Windows.Tests.Capture;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class WindowsScreenCaptureTests
{
    private static readonly WindowsScreenSnapshot[] Screens =
    [
        new("display-left", new ScreenRegion(-1920, 0, 1920, 1080), 96, 96, false),
        new("display-primary", new ScreenRegion(0, 0, 2560, 1440), 120, 120, true)
    ];

    [TestMethod]
    public async Task CaptureAsync_ResolvesPrimaryStableIdAndPhysicalRegion()
    {
        byte[] pixels = [1, 2, 3, 4, 5, 6, 7, 8];
        var backend = new FakeScreenBackend(Screens, pixels);
        var capture = new WindowsScreenCapture(backend);

        var primary = await capture.CaptureAsync(
            new ScreenCaptureRequest(ScreenCaptureTarget.PrimaryScreen));
        Assert.IsTrue(primary.IsSuccess);
        Assert.AreEqual(Screens[1].Bounds, backend.CapturedRegion);
        Assert.AreEqual(96d, primary.Value.DpiX);
        CollectionAssert.AreEqual(pixels, primary.Value.Pixels.ToArray());

        await capture.CaptureAsync(new ScreenCaptureRequest(
            ScreenCaptureTarget.Screen,
            new ScreenId("display-left")));
        Assert.AreEqual(Screens[0].Bounds, backend.CapturedRegion);

        await capture.CaptureAsync(new ScreenCaptureRequest(
            ScreenCaptureTarget.Screen,
            new ScreenId("-1920")));
        Assert.AreEqual(Screens[0].Bounds, backend.CapturedRegion);

        var region = new ScreenRegion(-25, 31, 4, 3);
        await capture.CaptureAsync(new ScreenCaptureRequest(
            ScreenCaptureTarget.Region,
            Region: region));
        Assert.AreEqual(region, backend.CapturedRegion);
    }

    [TestMethod]
    public async Task ScreenCatalog_PreservesBoundsDpiAndPrimaryFlag()
    {
        var catalog = new WindowsScreenCatalog(new FakeScreenBackend(Screens, new byte[8]));

        var result = await catalog.GetScreensAsync();

        Assert.HasCount(2, result);
        Assert.AreEqual(new ScreenId("display-left"), result[0].Id);
        Assert.AreEqual(120d, result[1].DpiX);
        Assert.IsTrue(result[1].IsPrimary);
    }

    private sealed class FakeScreenBackend(
        IReadOnlyList<WindowsScreenSnapshot> screens,
        byte[] pixels) : IWindowsScreenBackend
    {
        public ScreenRegion? CapturedRegion { get; private set; }

        public IReadOnlyList<WindowsScreenSnapshot> GetScreens(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return screens;
        }

        public WindowsCapturedFrame Capture(
            ScreenRegion region,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CapturedRegion = region;
            return new WindowsCapturedFrame(2, 1, 8, 96, 96, pixels);
        }
    }
}
