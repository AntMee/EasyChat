using EasyChat.Application.Selection;
using EasyChat.Application.Tests.Settings;
using EasyChat.Contracts.Platform;
using EasyChat.Contracts.Selection;
using EasyChat.Contracts.Settings;
using EasyChat.Shared.Results;
using Microsoft.Extensions.Logging.Abstractions;

namespace EasyChat.Application.Tests.Selection;

[TestClass]
public sealed class SelectionInteractionCoordinatorTests
{
    [TestMethod]
    public async Task DragSelection_PreservesThresholdDelaysContextAndToolbarOptions()
    {
        var bundle = SettingsTestData.CreateBundle() with
        {
            SelectionTranslation = new SelectionTranslationSettings(
                true,
                "AiModel",
                null,
                null,
                null,
                SelectionTriggerMode.All,
                true,
                true,
                false,
                true,
                false)
        };
        var settings = new FakeSettings(bundle);
        var pointer = new FakePointerMonitor();
        var selectedText = new FakeSelectedTextUseCases();
        var delay = new FakeDelay();
        var sink = new FakeSink();
        await using var coordinator = new SelectionInteractionCoordinator(
            settings,
            new AvailablePlatformAccess(),
            pointer,
            new FakeWindowFocus(),
            new FakeClipboardSnapshots(),
            selectedText,
            delay,
            NullLogger<SelectionInteractionCoordinator>.Instance);

        coordinator.Start(sink);
        await pointer.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var timestamp = DateTimeOffset.UtcNow;
        pointer.Publish(new GlobalPointerEvent(
            PointerAction.PrimaryPressed,
            new PhysicalScreenPoint(10, 20),
            timestamp));
        pointer.Publish(new GlobalPointerEvent(
            PointerAction.PrimaryReleased,
            new PhysicalScreenPoint(30, 40),
            timestamp.AddMilliseconds(30)));

        var capture = await sink.Captured.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual(SelectionGesture.Drag, capture.Gesture);
        Assert.AreEqual("selected", capture.SelectedText.Text);
        Assert.IsTrue(capture.Toolbar.Translation);
        Assert.IsTrue(capture.Toolbar.Correction);
        Assert.IsFalse(capture.Toolbar.Polish);
        Assert.IsTrue(capture.Toolbar.Summary);
        Assert.IsFalse(capture.Toolbar.Explanation);
        Assert.AreEqual(new PhysicalScreenPoint(30, 40), selectedText.Command!.PointerPosition);
        Assert.AreEqual("foreground", selectedText.Command.ExpectedForegroundTarget.Value);
        Assert.AreEqual("focused", selectedText.Command.ExpectedFocusedTarget.Value);
        CollectionAssert.Contains(delay.Delays, TimeSpan.FromSeconds(3));
        CollectionAssert.Contains(delay.Delays, TimeSpan.FromMilliseconds(50));
    }

    [TestMethod]
    public async Task DisposeAsync_WaitsForQueuedPointerWorkBeforeDisposingItsGate()
    {
        var initial = SettingsTestData.CreateBundle();
        var bundle = initial with
        {
            SelectionTranslation = initial.SelectionTranslation with { Enabled = true }
        };
        var pointer = new FakePointerMonitor();
        var sink = new BlockingSink();
        var coordinator = new SelectionInteractionCoordinator(
            new FakeSettings(bundle),
            new AvailablePlatformAccess(),
            pointer,
            new FakeWindowFocus(),
            new FakeClipboardSnapshots(),
            new FakeSelectedTextUseCases(),
            new FakeDelay(),
            NullLogger<SelectionInteractionCoordinator>.Instance);

        coordinator.Start(sink);
        await pointer.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        pointer.Publish(new GlobalPointerEvent(
            PointerAction.PrimaryPressed,
            new PhysicalScreenPoint(10, 20),
            DateTimeOffset.UtcNow));
        await sink.InspectStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var disposal = coordinator.DisposeAsync().AsTask();
        Assert.IsFalse(disposal.IsCompleted);

        sink.ContinueInspection.TrySetResult();
        await disposal.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [TestMethod]
    public async Task OpenResultSurface_DismissesAndBlocksDragSelectionThenAllowsNextGesture()
    {
        var pointer = new FakePointerMonitor();
        var selectedText = new FakeSelectedTextUseCases();
        var sink = new FakeSink
        {
            SurfaceState = new SelectionSurfaceState(false, true),
            DismissOnExternalPointerPress = true
        };
        await using var coordinator = CreateEnabledCoordinator(pointer, selectedText, sink);

        coordinator.Start(sink);
        await pointer.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var timestamp = DateTimeOffset.UtcNow;
        pointer.Publish(new GlobalPointerEvent(
            PointerAction.PrimaryPressed,
            new PhysicalScreenPoint(10, 20),
            timestamp));
        pointer.Publish(new GlobalPointerEvent(
            PointerAction.PrimaryReleased,
            new PhysicalScreenPoint(30, 40),
            timestamp.AddMilliseconds(30)));
        pointer.Publish(new GlobalPointerEvent(
            PointerAction.PrimaryPressed,
            new PhysicalScreenPoint(100, 200),
            timestamp.AddMilliseconds(600)));
        pointer.Publish(new GlobalPointerEvent(
            PointerAction.PrimaryReleased,
            new PhysicalScreenPoint(130, 240),
            timestamp.AddMilliseconds(630)));

        var capture = await sink.Captured.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual(new PhysicalScreenPoint(130, 240), selectedText.Command!.PointerPosition);
        Assert.AreEqual(new PhysicalScreenPoint(130, 240), capture.SelectedText.PointerPosition);
        Assert.AreEqual(2, sink.ExternalPointerPressCount);
        Assert.AreEqual(1, sink.DismissedSurfaceCount);
    }

    [TestMethod]
    public async Task OpenResultSurface_DismissesAndBlocksDoubleClickSequenceThenAllowsNextGesture()
    {
        var pointer = new FakePointerMonitor();
        var selectedText = new FakeSelectedTextUseCases();
        var sink = new FakeSink
        {
            SurfaceState = new SelectionSurfaceState(false, true),
            DismissOnExternalPointerPress = true
        };
        await using var coordinator = CreateEnabledCoordinator(pointer, selectedText, sink);

        coordinator.Start(sink);
        await pointer.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var timestamp = DateTimeOffset.UtcNow;
        var point = new PhysicalScreenPoint(10, 20);
        pointer.Publish(new GlobalPointerEvent(PointerAction.PrimaryPressed, point, timestamp));
        pointer.Publish(new GlobalPointerEvent(PointerAction.PrimaryReleased, point, timestamp.AddMilliseconds(30)));
        pointer.Publish(new GlobalPointerEvent(PointerAction.PrimaryDoubleClick, point, timestamp.AddMilliseconds(100)));
        pointer.Publish(new GlobalPointerEvent(PointerAction.PrimaryPressed, point, timestamp.AddMilliseconds(100)));
        pointer.Publish(new GlobalPointerEvent(PointerAction.PrimaryReleased, point, timestamp.AddMilliseconds(130)));
        var nextPoint = new PhysicalScreenPoint(100, 200);
        pointer.Publish(new GlobalPointerEvent(
            PointerAction.PrimaryDoubleClick,
            nextPoint,
            timestamp.AddMilliseconds(700)));

        var capture = await sink.Captured.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual(nextPoint, selectedText.Command!.PointerPosition);
        Assert.AreEqual(nextPoint, capture.SelectedText.PointerPosition);
        Assert.AreEqual(1, sink.ExternalPointerPressCount);
        Assert.AreEqual(1, sink.DismissedSurfaceCount);
    }

    private static SelectionInteractionCoordinator CreateEnabledCoordinator(
        FakePointerMonitor pointer,
        FakeSelectedTextUseCases selectedText,
        FakeSink sink)
    {
        var initial = SettingsTestData.CreateBundle();
        var bundle = initial with
        {
            SelectionTranslation = initial.SelectionTranslation with
            {
                Enabled = true,
                TriggerMode = SelectionTriggerMode.All
            }
        };
        return new SelectionInteractionCoordinator(
            new FakeSettings(bundle),
            new AvailablePlatformAccess(),
            pointer,
            new FakeWindowFocus(),
            new FakeClipboardSnapshots(),
            selectedText,
            new FakeDelay(),
            NullLogger<SelectionInteractionCoordinator>.Instance);
    }

    private sealed class FakePointerMonitor : IGlobalPointerMonitor
    {
        private Action<GlobalPointerEvent>? _callback;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IPointerMonitorRegistration Start(Action<GlobalPointerEvent> callback)
        {
            _callback = callback;
            Started.TrySetResult();
            return new Registration();
        }

        public void Publish(GlobalPointerEvent pointerEvent) => _callback!(pointerEvent);

        private sealed class Registration : IPointerMonitorRegistration
        {
            public void Dispose()
            {
            }
        }
    }

    private sealed class FakeSelectedTextUseCases : ISelectedTextUseCases
    {
        public SelectedTextCaptureCommand? Command { get; private set; }

        public ValueTask<Result<SelectedText>> CaptureAsync(
            SelectedTextCaptureCommand command,
            CancellationToken cancellationToken = default)
        {
            Command = command;
            return ValueTask.FromResult(Result<SelectedText>.Success(new SelectedText(
                "selected",
                command.ExpectedForegroundTarget,
                "fake",
                command.PointerPosition)));
        }
    }

    private sealed class FakeWindowFocus : IWindowFocus
    {
        public ValueTask<Result<ExternalTargetToken>> GetForegroundTargetAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Result<ExternalTargetToken>.Success(new ExternalTargetToken("foreground")));

        public ValueTask<Result<ExternalTargetToken>> GetFocusedTargetAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Result<ExternalTargetToken>.Success(new ExternalTargetToken("focused")));

        public ValueTask<Result> EnsureFocusedAsync(
            ExternalTargetToken target,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Result.Success());

        public ValueTask<Result> ConfigureNoActivateAsync(
            ExternalTargetToken target,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Result.Success());
    }

    private sealed class FakeClipboardSnapshots : IClipboardSnapshots
    {
        private sealed class Token : IClipboardChangeToken;

        public ValueTask<Result<IClipboardChangeToken>> GetChangeTokenAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Result<IClipboardChangeToken>.Success(new Token()));

        public ValueTask<Result<bool>> IsChangeTokenCurrentAsync(
            IClipboardChangeToken changeToken,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Result<bool>.Success(true));

        public ValueTask<Result<IClipboardSnapshot>> CaptureAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<Result> RestoreAsync(
            IClipboardSnapshot snapshot,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<Result> RestoreIfUnchangedAsync(
            IClipboardSnapshot snapshot,
            IClipboardChangeToken expectedChangeToken,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeSink : ISelectionInteractionSink
    {
        public TaskCompletionSource<SelectionCapture> Captured { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public SelectionSurfaceState SurfaceState { get; set; }
        public bool DismissOnExternalPointerPress { get; init; }
        public int ExternalPointerPressCount { get; private set; }
        public int DismissedSurfaceCount { get; private set; }

        public ValueTask<SelectionSurfaceState> InspectSurfaceAsync(
            PhysicalScreenPoint point,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(SurfaceState);

        public ValueTask OnMonitoringStartedAsync(
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask OnExternalPointerPressedAsync(
            PhysicalScreenPoint point,
            CancellationToken cancellationToken = default)
        {
            ExternalPointerPressCount++;
            if (DismissOnExternalPointerPress)
            {
                if (SurfaceState.BlocksSelectionCapture)
                    DismissedSurfaceCount++;
                SurfaceState = default;
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask OnSelectionCapturedAsync(
            SelectionCapture capture,
            CancellationToken cancellationToken = default)
        {
            Captured.TrySetResult(capture);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingSink : ISelectionInteractionSink
    {
        public TaskCompletionSource InspectStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ContinueInspection { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<SelectionSurfaceState> InspectSurfaceAsync(
            PhysicalScreenPoint point,
            CancellationToken cancellationToken = default)
        {
            InspectStarted.TrySetResult();
            await ContinueInspection.Task.ConfigureAwait(false);
            return new SelectionSurfaceState(false, false);
        }

        public ValueTask OnMonitoringStartedAsync(
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask OnExternalPointerPressedAsync(
            PhysicalScreenPoint point,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask OnSelectionCapturedAsync(
            SelectionCapture capture,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class FakeDelay : ISelectionDelay
    {
        public List<TimeSpan> Delays { get; } = [];

        public Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            lock (Delays)
                Delays.Add(delay);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSettings(SettingsBundle current) : ISettingsUseCases
    {
        public event EventHandler<SettingsChangedEventArgs>? SettingsChanged;
        public event EventHandler<SettingsSaveFailedEventArgs>? SaveFailed
        {
            add { }
            remove { }
        }
        public bool IsInitialized => true;
        public SettingsBundle Current { get; private set; } = current;

        public ValueTask<Result<SettingsBundle>> InitializeAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Result<SettingsBundle>.Success(Current));

        public Result Update(SettingsSection section, SettingsBundle settings)
        {
            Current = settings;
            SettingsChanged?.Invoke(this, new SettingsChangedEventArgs(section, settings));
            return Result.Success();
        }

        public ValueTask<Result> FlushAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Result.Success());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
