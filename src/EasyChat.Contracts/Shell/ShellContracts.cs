using EasyChat.Shared.Results;

namespace EasyChat.Contracts.Shell;

public interface IShellLifecycle
{
    bool IsStarted { get; }

    ValueTask<Result> StartAsync(CancellationToken cancellationToken = default);

    ValueTask<Result> StopAsync(CancellationToken cancellationToken = default);
}

public interface IApplicationRestartService
{
    void Restart();
}
