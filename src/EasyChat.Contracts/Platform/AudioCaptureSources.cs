namespace EasyChat.Contracts.Platform;

public enum AudioCaptureSourceKind
{
    SystemOutput,
    Application,
    Microphone
}

/// <summary>
/// Platform-owned identifier for an audio capture source. Consumers must treat the value as opaque,
/// must not persist it, and must return it unchanged to the speech port that owns it.
/// </summary>
public readonly record struct AudioCaptureSourceToken(string Value)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Value);
}

/// <summary>Display metadata for a source discovered during the current platform session.</summary>
public sealed record AudioCaptureSourceDescriptor(
    AudioCaptureSourceToken Token,
    AudioCaptureSourceKind Kind,
    string Name,
    string DisplayName,
    string? Description,
    ReadOnlyMemory<byte> IconPng,
    bool IsVirtualCable = false,
    bool IsDefault = false);

/// <summary>
/// Minimal source reference accepted by Application. Kind is portable business metadata used for
/// permission selection; only the platform adapter may interpret <see cref="Token"/>.
/// </summary>
public sealed record AudioCaptureSourceReference(
    AudioCaptureSourceToken Token,
    AudioCaptureSourceKind Kind);

public interface IAudioCaptureSourceCatalog
{
    ValueTask<IReadOnlyList<AudioCaptureSourceDescriptor>> GetSourcesAsync(
        CancellationToken cancellationToken = default);
}
