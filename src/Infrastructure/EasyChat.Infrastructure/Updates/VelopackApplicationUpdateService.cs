using System.Reflection;
using EasyChat.Contracts.Updates;
using EasyChat.Infrastructure.Network;
using EasyChat.Shared.Results;
using Microsoft.Extensions.Logging;
using Velopack;
using Velopack.Sources;

namespace EasyChat.Infrastructure.Updates;

public sealed class VelopackApplicationUpdateService(
    ILogger<VelopackApplicationUpdateService> logger,
    NetworkProxyHandlerFactory proxyFactory) : IApplicationUpdateService
{
    private static readonly Uri Repository = new("https://github.com/SwaggyMacro/EasyChat");
    private readonly ILogger<VelopackApplicationUpdateService> _logger = logger;
    private readonly NetworkProxyHandlerFactory _proxyFactory = proxyFactory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private UpdateInfo? _pending;

    public string CurrentVersion { get; } = FormatVersion(
        Assembly.GetEntryAssembly()?.GetName().Version);

    public async ValueTask<Result<ApplicationUpdateStatus>> CheckAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var manager = CreateManager();
            if (!manager.IsInstalled)
            {
                _pending = null;
                return Result<ApplicationUpdateStatus>.Success(
                    new ApplicationUpdateStatus(CurrentVersion, CurrentVersion, false));
            }

            _pending = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
            var latest = _pending?.TargetFullRelease.Version.ToString() ?? CurrentVersion;
            return Result<ApplicationUpdateStatus>.Success(
                new ApplicationUpdateStatus(CurrentVersion, latest, _pending is not null));
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to check for application updates.");
            return Result<ApplicationUpdateStatus>.Failure(
                new Error("updates.check-failed", exception.Message));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<Result> DownloadAndRestartAsync(
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_pending is null)
                return Result.Failure(new Error("updates.none-pending", "No application update is pending."));

            var manager = CreateManager();
            await manager.DownloadUpdatesAsync(
                _pending,
                value => progress?.Report(value),
                cancellationToken).ConfigureAwait(false);
            // Ensure the UI has rendered completion before Velopack replaces the process.
            progress?.Report(100);
            manager.ApplyUpdatesAndRestart(_pending);
            return Result.Success();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to download and apply the application update.");
            return Result.Failure(new Error("updates.apply-failed", exception.Message));
        }
        finally
        {
            _gate.Release();
        }
    }

    private UpdateManager CreateManager() =>
        new(new GithubSource(
            Repository.AbsoluteUri.TrimEnd('/'),
            null,
            false,
            new NetworkProxyFileDownloader(_proxyFactory)));

    private static string FormatVersion(Version? version) => version is null
        ? "Unknown"
        : $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
}
