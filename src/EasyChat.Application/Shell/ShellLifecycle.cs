using EasyChat.Contracts.Settings;
using EasyChat.Contracts.Shell;
using EasyChat.Shared.Results;

namespace EasyChat.Application.Shell;

public sealed class ShellLifecycle : IShellLifecycle
{
    private readonly ISettingsUseCases _settings;
    private readonly SemaphoreSlim _transition = new(1, 1);
    private volatile bool _started;

    public ShellLifecycle(ISettingsUseCases settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public bool IsStarted => _started;

    public async ValueTask<Result> StartAsync(CancellationToken cancellationToken = default)
    {
        await _transition.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_started)
                return Result.Success();

            var initialization = await _settings.InitializeAsync(cancellationToken)
                .ConfigureAwait(false);
            if (initialization.IsFailure)
                return Result.Failure(initialization.Error);

            _started = true;
            return Result.Success();
        }
        finally
        {
            _transition.Release();
        }
    }

    public async ValueTask<Result> StopAsync(CancellationToken cancellationToken = default)
    {
        await _transition.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_started)
                return Result.Success();

            var flush = await _settings.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (flush.IsFailure)
                return flush;

            _started = false;
            return Result.Success();
        }
        finally
        {
            _transition.Release();
        }
    }
}
