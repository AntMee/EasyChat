using EasyChat.Application.Shell;
using EasyChat.Application.Tests.Settings;
using EasyChat.Contracts.Settings;
using EasyChat.Shared.Results;

namespace EasyChat.Application.Tests.Shell;

[TestClass]
public sealed class ShellLifecycleTests
{
    [TestMethod]
    public async Task StartAndStop_AreIdempotentAndDelegateOnce()
    {
        var settings = new FakeSettingsUseCases();
        var lifecycle = new ShellLifecycle(settings);

        Assert.IsTrue((await lifecycle.StartAsync()).IsSuccess);
        Assert.IsTrue((await lifecycle.StartAsync()).IsSuccess);
        Assert.IsTrue(lifecycle.IsStarted);
        Assert.AreEqual(1, settings.InitializeCount);

        Assert.IsTrue((await lifecycle.StopAsync()).IsSuccess);
        Assert.IsTrue((await lifecycle.StopAsync()).IsSuccess);
        Assert.IsFalse(lifecycle.IsStarted);
        Assert.AreEqual(1, settings.FlushCount);
    }

    [TestMethod]
    public async Task FailedTransitionsRemainRetryable()
    {
        var settings = new FakeSettingsUseCases
        {
            InitializeResult = Result<SettingsBundle>.Failure(
                new Error("settings.read-failed", "read failed"))
        };
        var lifecycle = new ShellLifecycle(settings);

        var failedStart = await lifecycle.StartAsync();

        Assert.IsTrue(failedStart.IsFailure);
        Assert.IsFalse(lifecycle.IsStarted);
        settings.InitializeResult = Result<SettingsBundle>.Success(settings.Current);
        Assert.IsTrue((await lifecycle.StartAsync()).IsSuccess);

        settings.FlushResult = Result.Failure(
            new Error("settings.write-failed", "write failed"));
        var failedStop = await lifecycle.StopAsync();

        Assert.IsTrue(failedStop.IsFailure);
        Assert.IsTrue(lifecycle.IsStarted);
        settings.FlushResult = Result.Success();
        Assert.IsTrue((await lifecycle.StopAsync()).IsSuccess);
        Assert.IsFalse(lifecycle.IsStarted);
    }

    private sealed class FakeSettingsUseCases : ISettingsUseCases
    {
        public event EventHandler<SettingsChangedEventArgs>? SettingsChanged
        {
            add { }
            remove { }
        }

        public event EventHandler<SettingsSaveFailedEventArgs>? SaveFailed
        {
            add { }
            remove { }
        }

        public int InitializeCount { get; private set; }
        public int FlushCount { get; private set; }
        public bool IsInitialized => InitializeResult.IsSuccess;
        public SettingsBundle Current { get; } = SettingsTestData.CreateBundle();
        public Result<SettingsBundle> InitializeResult { get; set; }
        public Result FlushResult { get; set; } = Result.Success();

        public FakeSettingsUseCases()
        {
            InitializeResult = Result<SettingsBundle>.Success(Current);
        }

        public ValueTask<Result<SettingsBundle>> InitializeAsync(
            CancellationToken cancellationToken = default)
        {
            InitializeCount++;
            return ValueTask.FromResult(InitializeResult);
        }

        public Result Update(SettingsSection section, SettingsBundle settings) => Result.Success();

        public ValueTask<Result> FlushAsync(CancellationToken cancellationToken = default)
        {
            FlushCount++;
            return ValueTask.FromResult(FlushResult);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
