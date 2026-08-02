using EasyChat.Shared.Results;

namespace EasyChat.Contracts.Platform;

public enum PlatformCapability
{
    ScreenCapture,
    GlobalHotkeys,
    SelectedTextCapture,
    TextDelivery,
    Clipboard,
    GlobalPointerMonitoring,
    WindowActivation,
    ProcessEnumeration,
    SpeechRecognition,
    AudioPlayback
}

public enum CapabilityState
{
    Available,
    PermissionRequired,
    Unsupported
}

public enum PlatformPermission
{
    Accessibility,
    ScreenRecording,
    InputMonitoring,
    Microphone,
    SystemAudioCapture
}

public sealed record CapabilityStatus(
    PlatformCapability Capability,
    CapabilityState State,
    PlatformPermission? RequiredPermission = null,
    string? Reason = null);

public interface IPlatformCapabilities
{
    ValueTask<CapabilityStatus> GetStatusAsync(
        PlatformCapability capability,
        CancellationToken cancellationToken = default);
}

public interface IPlatformPermissionRequester
{
    ValueTask<Result<CapabilityStatus>> RequestAsync(
        PlatformPermission permission,
        CancellationToken cancellationToken = default);
}
