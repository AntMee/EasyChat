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

public readonly record struct ScreenPoint(double X, double Y);

public readonly record struct LogicalRect(double X, double Y, double Width, double Height);

public readonly record struct PixelRect(int X, int Y, int Width, int Height);

public readonly record struct ExternalTargetToken(string Value)
{
    public static ExternalTargetToken None { get; } = new(string.Empty);

    public bool IsEmpty => string.IsNullOrEmpty(Value);
}

public sealed record EncodedImage(
    ReadOnlyMemory<byte> Content,
    string MediaType,
    int PixelWidth,
    int PixelHeight,
    double Scale);

[Flags]
public enum ShortcutModifiers
{
    None = 0,
    Control = 1,
    Alt = 2,
    Shift = 4,
    Meta = 8
}

public sealed record KeyStroke(string Key, ShortcutModifiers Modifiers);

public sealed record ShortcutGesture(IReadOnlyList<KeyStroke> Sequence)
{
    public static ShortcutGesture Single(string key, ShortcutModifiers modifiers = ShortcutModifiers.None) =>
        new([new KeyStroke(key, modifiers)]);
}

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

public sealed record ScreenCaptureRequest(
    LogicalRect? Area = null,
    bool IncludeCursor = false);

public interface IScreenCapture
{
    ValueTask<Result<EncodedImage>> CaptureAsync(
        ScreenCaptureRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record HotkeyRegistration(string Id, ShortcutGesture Gesture);

public interface IGlobalHotkeyRegistrar
{
    event EventHandler<string>? Pressed;

    ValueTask<Result<bool>> RegisterAsync(
        HotkeyRegistration registration,
        CancellationToken cancellationToken = default);

    ValueTask UnregisterAsync(string id, CancellationToken cancellationToken = default);
}

public sealed record SelectionCaptureRequest(
    ScreenPoint? PointerPosition = null,
    bool CopyOnly = false,
    ExternalTargetToken ExpectedTarget = default);

public sealed record SelectedText(
    string Text,
    ExternalTargetToken SourceTarget,
    string CaptureMethod);

public interface ISelectedTextCapture
{
    ValueTask<Result<SelectedText>> CaptureAsync(
        SelectionCaptureRequest request,
        CancellationToken cancellationToken = default);
}

public enum TextDeliveryMode
{
    Type,
    Paste,
    PasteAndSubmit
}

public sealed record TextDeliveryRequest(
    string Text,
    ExternalTargetToken Target,
    TextDeliveryMode Mode,
    TimeSpan KeyDelay);

public interface ITextDelivery
{
    ValueTask<Result<bool>> DeliverAsync(
        TextDeliveryRequest request,
        CancellationToken cancellationToken = default);
}

public interface IClipboardService
{
    ValueTask<Result<string?>> ReadTextAsync(CancellationToken cancellationToken = default);

    ValueTask<Result<bool>> WriteTextAsync(
        string text,
        CancellationToken cancellationToken = default);
}

public enum PointerAction
{
    Moved,
    PrimaryPressed,
    PrimaryReleased,
    SecondaryPressed,
    SecondaryReleased
}

public sealed record GlobalPointerEvent(PointerAction Action, ScreenPoint Position, DateTimeOffset Timestamp);

public interface IGlobalPointerMonitor
{
    IAsyncEnumerable<GlobalPointerEvent> WatchAsync(CancellationToken cancellationToken = default);
}

public interface IWindowFocus
{
    ValueTask<Result<ExternalTargetToken>> GetActiveTargetAsync(
        CancellationToken cancellationToken = default);

    ValueTask<Result<bool>> ActivateAsync(
        ExternalTargetToken target,
        CancellationToken cancellationToken = default);
}

public sealed record ProcessDescriptor(int ProcessId, string Name, string? ExecutablePath);

public interface IProcessCatalog
{
    ValueTask<IReadOnlyList<ProcessDescriptor>> GetProcessesAsync(
        CancellationToken cancellationToken = default);
}

public sealed record SpeechRecognitionOptions(
    string ModelPath,
    string Language,
    IReadOnlyList<int> ProcessIds);

public enum SpeechRecognitionEventKind
{
    Started,
    Partial,
    Final,
    Error,
    Stopped
}

public sealed record SpeechRecognitionEvent(SpeechRecognitionEventKind Kind, string? Text = null);

public interface ISpeechRecognitionEngine
{
    IAsyncEnumerable<SpeechRecognitionEvent> RecognizeAsync(
        SpeechRecognitionOptions options,
        CancellationToken cancellationToken = default);
}

public sealed record AudioTrack(ReadOnlyMemory<byte> Content, string MediaType);

public interface IAudioPlaybackQueue
{
    ValueTask EnqueueAsync(AudioTrack track, CancellationToken cancellationToken = default);

    ValueTask StopAsync(CancellationToken cancellationToken = default);
}
