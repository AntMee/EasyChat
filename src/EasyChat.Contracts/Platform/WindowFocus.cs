using EasyChat.Shared.Results;

namespace EasyChat.Contracts.Platform;

public interface IWindowFocus
{
    ValueTask<Result<ExternalTargetToken>> GetForegroundTargetAsync(
        CancellationToken cancellationToken = default);

    ValueTask<Result<ExternalTargetToken>> GetFocusedTargetAsync(
        CancellationToken cancellationToken = default);

    ValueTask<Result> EnsureFocusedAsync(
        ExternalTargetToken target,
        CancellationToken cancellationToken = default);

    ValueTask<Result> ConfigureNoActivateAsync(
        ExternalTargetToken target,
        CancellationToken cancellationToken = default);
}

public interface IWindowInputTransparency
{
    ValueTask<Result> SetClickThroughAsync(
        ExternalTargetToken target,
        bool enabled,
        CancellationToken cancellationToken = default);
}
