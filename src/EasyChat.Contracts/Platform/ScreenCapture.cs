using EasyChat.Shared.Results;

namespace EasyChat.Contracts.Platform;

public enum ScreenCaptureTarget
{
    PrimaryScreen,
    Screen,
    Region
}

public sealed record ScreenCaptureRequest(
    ScreenCaptureTarget Target,
    ScreenId? Screen = null,
    ScreenRegion? Region = null);

public interface IScreenCapture
{
    ValueTask<Result<ImageFrame>> CaptureAsync(
        ScreenCaptureRequest request,
        CancellationToken cancellationToken = default);
}

public interface IScreenCatalog
{
    ValueTask<IReadOnlyList<ScreenDescriptor>> GetScreensAsync(
        CancellationToken cancellationToken = default);
}
