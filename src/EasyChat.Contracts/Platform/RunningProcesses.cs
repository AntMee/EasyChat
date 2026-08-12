using EasyChat.Shared.Results;

namespace EasyChat.Contracts.Platform;

/// <summary>
/// A running application that can be targeted by selection workflows. <see cref="Identifier"/>
/// is the stable persisted identity used by selection blacklists/whitelists; the remaining
/// properties are display metadata captured from the current platform session.
/// </summary>
public sealed record RunningProcessDescriptor(
    string Identifier,
    string Name,
    string? Description,
    string? WindowTitle,
    ReadOnlyMemory<byte> IconPng);

/// <summary>
/// Platform-owned discovery of running applications. Consumers treat <see cref="Identifier"/>
/// as an opaque string: they persist and compare it only, while the platform adapter owns its
/// encoding (for example the executable file name on Windows).
/// </summary>
public interface IRunningProcessCatalog
{
    /// <summary>Enumerates interactive applications currently running with a visible window.</summary>
    ValueTask<IReadOnlyList<RunningProcessDescriptor>> GetRunningProcessesAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the process identifier that owns <paramref name="target"/>. Fails when the target
    /// is not owned by a process visible to the current session (for example it already exited).
    /// </summary>
    ValueTask<Result<string>> ResolveProcessIdentifierAsync(
        ExternalTargetToken target,
        CancellationToken cancellationToken = default);
}
