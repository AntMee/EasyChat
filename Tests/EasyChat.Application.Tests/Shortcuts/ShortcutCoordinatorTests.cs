using EasyChat.Application.Shortcuts;
using EasyChat.Application.Tests.Settings;
using EasyChat.Contracts.Platform;
using EasyChat.Contracts.Settings;
using EasyChat.Contracts.Shortcuts;
using EasyChat.Shared.Results;

namespace EasyChat.Application.Tests.Shortcuts;

[TestClass]
public sealed class ShortcutCoordinatorTests
{
    [TestMethod]
    public async Task StartAsync_RegistersEnabledKnownActionsAndOwnsTheirLifetime()
    {
        var bundle = SettingsTestData.CreateBundle() with
        {
            Shortcut = new ShortcutSettings(
            [
                new ShortcutEntrySettings("Screenshot", null, "Ctrl + Oem3", true),
                new ShortcutEntrySettings("Screenshot", null, "Alt + F8", false),
                new ShortcutEntrySettings("Unknown", null, "Ctrl + U", true)
            ])
        };
        var hotkeys = new FakeGlobalHotkeys();
        var action = new FakeShortcutAction();
        await using var coordinator = new ShortcutCoordinator(
            new FakeSettingsUseCases(bundle),
            hotkeys,
            [action]);

        var report = await coordinator.StartAsync();

        Assert.AreEqual(2, report.RequestedCount);
        Assert.AreEqual(1, report.RegisteredCount);
        Assert.HasCount(1, report.Issues);
        Assert.AreEqual("shortcut.action-unavailable", report.Issues[0].Error.Code);
        Assert.AreEqual(
            new ShortcutGesture("Oem3", ShortcutModifiers.Control),
            hotkeys.Gesture);

        await hotkeys.Callback!(CancellationToken.None);
        Assert.AreEqual(1, action.ExecutionCount);
        await coordinator.DisposeAsync();
        Assert.AreEqual(1, hotkeys.Registration.DisposeCount);
    }

    private sealed class FakeShortcutAction : IShortcutAction
    {
        public string ActionType => "Screenshot";
        public bool PreventConcurrentExecution => true;
        public int ExecutionCount { get; private set; }

        public ValueTask ExecuteAsync(
            ShortcutParameterSettings? parameter,
            CancellationToken cancellationToken = default)
        {
            ExecutionCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeGlobalHotkeys : IGlobalHotkeys
    {
        public ShortcutGesture? Gesture { get; private set; }
        public Func<CancellationToken, ValueTask>? Callback { get; private set; }
        public FakeRegistration Registration { get; } = new();

        public ValueTask<Result<IHotkeyRegistration>> RegisterAsync(
            ShortcutGesture gesture,
            Func<CancellationToken, ValueTask> callback,
            CancellationToken cancellationToken = default)
        {
            Gesture = gesture;
            Callback = callback;
            return ValueTask.FromResult(Result<IHotkeyRegistration>.Success(Registration));
        }

        public ValueTask<Result> ProbeAsync(
            ShortcutGesture gesture,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Result.Success());
    }

    private sealed class FakeRegistration : IHotkeyRegistration
    {
        public int DisposeCount { get; private set; }
        public void Dispose() => DisposeCount++;
    }

    private sealed class FakeSettingsUseCases(SettingsBundle current) : ISettingsUseCases
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

        public bool IsInitialized => true;
        public SettingsBundle Current { get; } = current;

        public ValueTask<Result<SettingsBundle>> InitializeAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Result<SettingsBundle>.Success(Current));

        public Result Update(SettingsSection section, SettingsBundle settings) => Result.Success();
        public ValueTask<Result> FlushAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Result.Success());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
