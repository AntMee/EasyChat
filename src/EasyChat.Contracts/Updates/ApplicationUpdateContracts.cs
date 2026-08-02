using EasyChat.Shared.Results;

namespace EasyChat.Contracts.Updates;

public sealed record ApplicationUpdateStatus(
    string CurrentVersion,
    string LatestVersion,
    bool IsUpdateAvailable);

public interface IApplicationUpdateService
{
    string CurrentVersion { get; }

    ValueTask<Result<ApplicationUpdateStatus>> CheckAsync(
        CancellationToken cancellationToken = default);

    ValueTask<Result> DownloadAndRestartAsync(
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default);
}
