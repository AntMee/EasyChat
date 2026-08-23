using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using EasyChat.Contracts.Capture;
using EasyChat.Contracts.Platform;
using EasyChat.Shared.Results;
using EasyChat.Presentation.Features.Capture.Views;
using EasyChat.Presentation.Foundation.Platform;
using EasyChat.Presentation.ImageTranslation;

namespace EasyChat.Presentation.Features.Capture;

internal sealed record CaptureOverlayOutcome(
    PhysicalScreenRegion Region,
    PhysicalScreenPoint CompletionPoint,
    CaptureOverlayAction Action,
    Bitmap? Image);

public sealed class CaptureOverlayCoordinator(
    IScreenCatalog screens,
    IScreenCapture capture,
    IPointerPosition pointer,
    IWindowFocus focus,
    IKeyboardState keyboard,
    ILongScreenshotStitcher stitcher,
    IPlatformWindowBehavior windowBehavior)
{
    private readonly IScreenCatalog _screens = screens;
    private readonly IScreenCapture _capture = capture;
    private readonly IPointerPosition _pointer = pointer;
    private readonly IWindowFocus _focus = focus;
    private readonly IKeyboardState _keyboard = keyboard;
    private readonly ILongScreenshotStitcher _stitcher = stitcher;
    private readonly IPlatformWindowBehavior _windowBehavior = windowBehavior;
    private readonly SemaphoreSlim _gate = new(1, 1);

    internal async Task<CaptureOverlayOutcome?> SelectAsync(
        bool precise,
        bool regionOnly,
        CaptureOverlayAction defaultAction = CaptureOverlayAction.Translation,
        CaptureToolbarMode toolbarMode = CaptureToolbarMode.Full,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var availableScreens = (await _screens.GetScreensAsync(cancellationToken)
                    .ConfigureAwait(false))
                .Where(screen => !screen.Bounds.IsEmpty)
                .ToArray();
            if (availableScreens.Length == 0)
                throw new InvalidOperationException("No display screen is available.");

            var target = await GetForegroundTargetAsync(cancellationToken).ConfigureAwait(false);

            var desktopBounds = Union(availableScreens.Select(screen => screen.Bounds));
            using var desktopImage = await CaptureDesktopImageAsync(
                desktopBounds,
                cancellationToken).ConfigureAwait(false);
            var initialPointer = GetInitialPointer(availableScreens);
            var session = await OnUiAsync(
                () => new CaptureOverlaySession(
                    availableScreens,
                    desktopBounds,
                    desktopImage,
                    _capture,
                    _stitcher,
                    _windowBehavior,
                    _focus,
                    _keyboard,
                    target,
                    cancellationToken,
                    precise,
                    regionOnly,
                    defaultAction,
                    regionOnly ? CaptureToolbarMode.ImageSelection : toolbarMode),
                cancellationToken);
            try
            {
                var completion = await OnUiAsync(
                    () => session.Start(initialPointer),
                    cancellationToken);
                using var cancellationRegistration = cancellationToken.Register(() =>
                    Dispatcher.UIThread.Post(() => session.Cancel(cancellationToken)));
                var escapeMonitor = MonitorEscapeAsync(session, completion);
                try
                {
                    return await completion.ConfigureAwait(false);
                }
                finally
                {
                    await escapeMonitor.ConfigureAwait(false);
                }
            }
            finally
            {
                await OnUiAsync(session.Dispose, CancellationToken.None);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Bitmap> CaptureDesktopImageAsync(
        PhysicalScreenRegion desktopBounds,
        CancellationToken cancellationToken)
    {
        var captured = await _capture.CaptureAsync(
            new ScreenCaptureRequest(ScreenCaptureTarget.Region, Region: desktopBounds),
            cancellationToken).ConfigureAwait(false);
        if (captured.IsFailure)
            throw new InvalidOperationException(captured.Error.Message);
        return AvaloniaImageFrames.ToBitmap(captured.Value);
    }

    private PhysicalScreenPoint GetInitialPointer(IReadOnlyList<ScreenDescriptor> availableScreens)
    {
        try
        {
            return _pointer.GetCurrent();
        }
        catch
        {
            var primary = availableScreens.FirstOrDefault(screen => screen.IsPrimary) ?? availableScreens[0];
            return new PhysicalScreenPoint(
                primary.Bounds.X + primary.Bounds.Width / 2,
                primary.Bounds.Y + primary.Bounds.Height / 2);
        }
    }

    private async ValueTask<ExternalTargetToken> GetForegroundTargetAsync(
        CancellationToken cancellationToken)
    {
        var result = await _focus.GetForegroundTargetAsync(cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? result.Value : ExternalTargetToken.None;
    }

    private static PhysicalScreenRegion Union(IEnumerable<PhysicalScreenRegion> regions)
    {
        var all = regions.Where(region => !region.IsEmpty).ToArray();
        if (all.Length == 0)
            throw new InvalidOperationException("No non-empty display screen is available.");
        var left = all.Min(region => region.X);
        var top = all.Min(region => region.Y);
        var right = all.Max(region => checked(region.X + region.Width));
        var bottom = all.Max(region => checked(region.Y + region.Height));
        return new PhysicalScreenRegion(left, top, right - left, bottom - top);
    }

    private static async ValueTask OnUiAsync(
        Action action,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return;
        }
        await Dispatcher.UIThread.InvokeAsync(action, DispatcherPriority.Normal, cancellationToken);
    }

    private static async ValueTask<T> OnUiAsync<T>(
        Func<T> action,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Dispatcher.UIThread.CheckAccess())
            return action();
        return await Dispatcher.UIThread.InvokeAsync(
            action,
            DispatcherPriority.Normal,
            cancellationToken);
    }

    private async Task MonitorEscapeAsync(
        CaptureOverlaySession session,
        Task<CaptureOverlayOutcome?> completion)
    {
        var wasPressed = _keyboard.IsPressed(KeyboardKey.Escape);
        while (!completion.IsCompleted)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(25)).ConfigureAwait(false);
            var isPressed = _keyboard.IsPressed(KeyboardKey.Escape);
            if (isPressed && !wasPressed)
            {
                // Finish closes Avalonia windows and must run on the UI thread.
                // The keyboard state itself is safe to sample from this poller.
                var cancelled = await OnUiAsync(
                        session.TryCancelFromEscape,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                if (cancelled)
                    return;
            }
            wasPressed = isPressed;
        }
    }
}

internal sealed class CaptureOverlaySession : IDisposable
{
    private readonly IReadOnlyList<ScreenDescriptor> _screens;
    private readonly PhysicalScreenRegion _desktopBounds;
    private readonly Bitmap _desktopImage;
    private readonly IScreenCapture _capture;
    private readonly ILongScreenshotStitcher _stitcher;
    private readonly IPlatformWindowBehavior _windowBehavior;
    private readonly IWindowFocus _focus;
    private readonly IKeyboardState _keyboard;
    private readonly ExternalTargetToken _target;
    private readonly CancellationToken _cancellationToken;
    private readonly bool _precise;
    private readonly bool _regionOnly;
    private readonly CaptureOverlayAction _defaultAction;
    private readonly PhysicalSelectionState _selection;
    private readonly List<OverlaySurface> _surfaces = [];
    private readonly TaskCompletionSource<CaptureOverlayOutcome?> _completion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private OverlayWindowView? _toolbarView;
    private OverlayWindowView? _hintView;
    private LongScreenshotProgressView? _longScreenshotProgress;
    private LongScreenshotSelectionBorderView? _longScreenshotSelectionBorder;
    private PhysicalScreenPoint? _completionPoint;
    private Screens? _screenCollection;
    private bool _finished;
    private bool _disposed;
    private bool _processing;
    private bool _completeLongScreenshotRequested;
    private bool _longScreenshotStarted;
    private TaskCompletionSource<bool>? _longScreenshotStart;
    private bool _longScreenshotResultReview;
    private Bitmap? _longScreenshotResultImage;
    private LongScreenshotResultDialog? _longScreenshotResultDialog;
    private LongScreenshotDirection _longScreenshotDirection = LongScreenshotDirection.Vertical;
    private int _longScreenshotFrameCount;
    private LongScreenshotAccumulator? _longScreenshotAccumulator;
    private PhysicalScreenRegion? _longScreenshotSelection;
    public CaptureOverlaySession(
        IReadOnlyList<ScreenDescriptor> screens,
        PhysicalScreenRegion desktopBounds,
        Bitmap desktopImage,
        IScreenCapture capture,
        ILongScreenshotStitcher stitcher,
        IPlatformWindowBehavior windowBehavior,
        IWindowFocus focus,
        IKeyboardState keyboard,
        ExternalTargetToken target,
        CancellationToken cancellationToken,
        bool precise,
        bool regionOnly,
        CaptureOverlayAction defaultAction,
        CaptureToolbarMode toolbarMode)
    {
        _screens = screens;
        _desktopBounds = desktopBounds;
        _desktopImage = desktopImage;
        _capture = capture;
        _stitcher = stitcher;
        _windowBehavior = windowBehavior;
        _focus = focus;
        _keyboard = keyboard;
        _target = target;
        _cancellationToken = cancellationToken;
        _precise = precise;
        _regionOnly = regionOnly;
        _defaultAction = defaultAction;
        _selection = new PhysicalSelectionState(ToPixelRect(desktopBounds));

        try
        {
            foreach (var screen in screens)
            {
                var crop = CaptureOverlayGeometry.GetDesktopSlice(
                    screen.Bounds,
                    desktopBounds);
                var background = new CroppedBitmap(desktopImage, crop);
                OverlayWindowView? view = null;
                try
                {
                    view = new OverlayWindowView(
                        screen,
                        background,
                        regionOnly,
                        defaultAction,
                        toolbarMode);
                    Subscribe(view);
                    _surfaces.Add(new OverlaySurface(view, background));
                }
                catch
                {
                    if (view is not null)
                    {
                        Unsubscribe(view);
                        view.PrepareForSessionClose();
                    }
                    background.Dispose();
                    throw;
                }
            }
        }
        catch
        {
            foreach (var surface in _surfaces)
            {
                Unsubscribe(surface.View);
                surface.View.PrepareForSessionClose();
                surface.Background.Dispose();
            }
            throw;
        }
    }

    public Task<CaptureOverlayOutcome?> Start(PhysicalScreenPoint initialPointer)
    {
        ThrowIfDisposed();
        var active = FindView(initialPointer)
                     ?? _surfaces.FirstOrDefault(surface => surface.View.Screen.IsPrimary)?.View
                     ?? _surfaces[0].View;
        _hintView = active;

        foreach (var surface in _surfaces)
        {
            surface.View.SetHintHost(ReferenceEquals(surface.View, active));
            surface.View.ShowActivated = ReferenceEquals(surface.View, active);
            surface.View.Show();
        }

        _screenCollection = active.Screens;
        _screenCollection.Changed += OnScreensChanged;
        if (!CaptureOverlayGeometry.MatchesTopology(
                _screens,
                _screenCollection.All.Select(screen => (screen.Bounds, screen.Scaling))))
        {
            Finish(null);
            return _completion.Task;
        }
        RenderAll();
        active.Activate();
        return _completion.Task;
    }

    public void Cancel(CancellationToken cancellationToken)
    {
        if (_finished)
            return;
        _finished = true;
        CloseLongScreenshotSelectionBorder();
        var closeFailure = CloseAll();
        if (closeFailure is null)
            _completion.TrySetCanceled(cancellationToken);
        else
            _completion.TrySetException(closeFailure);
    }

    internal bool TryCancelFromEscape()
    {
        if (_finished)
            return false;
        Finish(null);
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _longScreenshotResultImage?.Dispose();
        _longScreenshotResultImage = null;
        _longScreenshotResultReview = false;
        CloseLongScreenshotResultDialog();
        CloseLongScreenshotSelectionBorder();
        if (_longScreenshotProgress is not null)
        {
            _longScreenshotProgress.StopRequested -= OnLongScreenshotStopRequested;
            _longScreenshotProgress.CancelRequested -= OnLongScreenshotCancelRequested;
            _longScreenshotProgress.DirectionChanged -= OnLongScreenshotDirectionChanged;
            _longScreenshotProgress.CloseSessionWindow();
            _longScreenshotProgress = null;
        }
        if (_screenCollection is not null)
            _screenCollection.Changed -= OnScreensChanged;
        _ = CloseAll();
        foreach (var surface in _surfaces)
        {
            try
            {
                Unsubscribe(surface.View);
                surface.View.PrepareForSessionClose();
            }
            catch
            {
            }
            try
            {
                surface.Background.Dispose();
            }
            catch
            {
            }
        }
        _surfaces.Clear();
    }

    private void Subscribe(OverlayWindowView view)
    {
        view.InteractionStarted += OnInteractionStarted;
        view.InteractionMoved += OnInteractionMoved;
        view.InteractionEnded += OnInteractionEnded;
        view.ActionRequested += OnActionRequested;
        view.ResetRequested += OnResetRequested;
        view.CancelRequested += OnCancelRequested;
        view.ClosedUnexpectedly += OnClosedUnexpectedly;
    }

    private void Unsubscribe(OverlayWindowView view)
    {
        view.InteractionStarted -= OnInteractionStarted;
        view.InteractionMoved -= OnInteractionMoved;
        view.InteractionEnded -= OnInteractionEnded;
        view.ActionRequested -= OnActionRequested;
        view.ResetRequested -= OnResetRequested;
        view.CancelRequested -= OnCancelRequested;
        view.ClosedUnexpectedly -= OnClosedUnexpectedly;
    }

    private void OnInteractionStarted(
        OverlayWindowView view,
        PhysicalScreenPoint point,
        CaptureResizeHandle handle,
        bool insideSelection)
    {
        if (_finished)
            return;

        _hintView = view;
        var pixel = ToPixelPoint(point);
        if (_selection.Mode == CaptureSelectionMode.Done &&
            handle != CaptureResizeHandle.None &&
            _selection.BeginResize(handle, pixel))
        {
            _toolbarView = null;
            _completionPoint = null;
            RenderAll();
            return;
        }
        if (_selection.Mode == CaptureSelectionMode.Done &&
            insideSelection &&
            _selection.BeginMove(pixel))
        {
            _toolbarView = null;
            _completionPoint = null;
            RenderAll();
            return;
        }

        _selection.BeginSelection(pixel);
        _toolbarView = null;
        _completionPoint = null;
        foreach (var surface in _surfaces)
            surface.View.SetHintHost(false);
        RenderAll();
    }

    private void OnInteractionMoved(OverlayWindowView view, PhysicalScreenPoint point)
    {
        if (_finished || _selection.Mode is CaptureSelectionMode.Idle or CaptureSelectionMode.Done)
            return;
        _selection.Update(ToPixelPoint(point));
        RenderAll();
    }

    private void OnInteractionEnded(OverlayWindowView view, PhysicalScreenPoint point)
    {
        if (_finished || _selection.Mode is CaptureSelectionMode.Idle or CaptureSelectionMode.Done)
            return;
        _selection.Update(ToPixelPoint(point));
        if (!_selection.Complete(ToPixelPoint(point)))
        {
            Reset();
            return;
        }

        _toolbarView = FindToolbarView(point, view);
        _hintView = _toolbarView;
        _completionPoint = point;
        if (_precise)
        {
            RenderAll();
            _toolbarView.Activate();
        }
        else
            Complete(_defaultAction);
    }

    private void OnActionRequested(OverlayWindowView view, CaptureOverlayAction action)
    {
        if (_finished || _processing || _selection.Mode != CaptureSelectionMode.Done)
            return;
        _toolbarView = view;
        _completionPoint = ScreenCenter(view.Screen.Bounds);
        if (_longScreenshotResultReview)
        {
            CompleteLongScreenshotResult(action);
            return;
        }
        if (action == CaptureOverlayAction.CopyLongScreenshot)
        {
            _processing = true;
            _completeLongScreenshotRequested = false;
            _longScreenshotDirection = LongScreenshotDirection.Vertical;
            _longScreenshotFrameCount = 0;
            _ = CompleteLongScreenshotAsync();
            return;
        }
        Complete(action);
    }

    private void OnResetRequested() => Reset();
    private void OnCancelRequested() => Finish(null);
    private void OnClosedUnexpectedly() => Finish(null);
    private void OnScreensChanged(object? sender, EventArgs e) => Finish(null);

    private void Reset()
    {
        if (_finished)
            return;
        if (_longScreenshotResultReview)
        {
            _longScreenshotResultReview = false;
            CloseLongScreenshotResultDialog();
            _longScreenshotResultImage?.Dispose();
            _longScreenshotResultImage = null;
            foreach (var surface in _surfaces)
                surface.View.ShowSessionWindow();
        }
        _selection.Reset();
        _toolbarView = null;
        _completionPoint = null;
        _hintView ??= _surfaces.FirstOrDefault(surface => surface.View.Screen.IsPrimary)?.View
                      ?? _surfaces[0].View;
        foreach (var surface in _surfaces)
            surface.View.SetHintHost(ReferenceEquals(surface.View, _hintView));
        RenderAll();
    }

    private void Complete(CaptureOverlayAction action)
    {
        if (_selection.Region is not { } selected ||
            _completionPoint is not { } completionPoint)
        {
            Finish(null);
            return;
        }

        Bitmap? image = null;
        try
        {
            if (!_regionOnly)
            {
                var crop = _selection.ToUnionBitmapRect(ToPixelRect(_desktopBounds));
                if (crop is not { Width: > 0, Height: > 0 } cropRect)
                {
                    Finish(null);
                    return;
                }

                image = RenderCrop(_desktopImage, cropRect);
            }

            var outcome = new CaptureOverlayOutcome(
                ToPhysicalRegion(selected),
                completionPoint,
                action,
                image);
            Finish(outcome);
            image = null;
        }
        catch (Exception exception)
        {
            Fail(exception);
        }
        finally
        {
            image?.Dispose();
        }
    }

    private async Task CompleteLongScreenshotAsync()
    {
        ImageFrame? combined = null;
        LongScreenshotAccumulator? accumulator = null;
        try
        {
            if (_regionOnly || _selection.Region is not { } selected)
            {
                Finish(null);
                return;
            }

            var selectedPhysical = ToPhysicalRegion(selected);
            var crop = _selection.ToUnionBitmapRect(ToPixelRect(_desktopBounds));
            if (crop is not { Width: > 0, Height: > 0 } cropRect)
            {
                Finish(null);
                return;
            }

            using (var firstBitmap = RenderCrop(_desktopImage, cropRect))
                combined = LongScreenshotComposer.ToImageFrame(firstBitmap);

            accumulator = LongScreenshotComposer.CreateAccumulator(
                combined,
                _longScreenshotDirection,
                _stitcher);
            _longScreenshotAccumulator = accumulator;
            _longScreenshotSelection = selectedPhysical;
            _longScreenshotFrameCount = accumulator.Count;
            _longScreenshotStarted = false;
            combined = null;

            _longScreenshotProgress = new LongScreenshotProgressView();
            _longScreenshotProgress.ConfigureCaptureExclusion(_windowBehavior);
            _longScreenshotStart = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _longScreenshotProgress.StartRequested += OnLongScreenshotStartRequested;
            _longScreenshotProgress.StopRequested += OnLongScreenshotStopRequested;
            _longScreenshotProgress.CancelRequested += OnLongScreenshotCancelRequested;
            _longScreenshotProgress.DirectionChanged += OnLongScreenshotDirectionChanged;
            ShowLongScreenshotPreview(accumulator, selectedPhysical);

            HideAll();
            ShowLongScreenshotSelectionBorder(selectedPhysical);

            var startRequested = await _longScreenshotStart.Task
                .WaitAsync(_cancellationToken)
                .ConfigureAwait(true);
            if (!startRequested || _finished)
                return;

            // Direction can be changed while the preview is waiting for the
            // user to press Start. The direction handler replaces the shared
            // accumulator, so refresh this local reference before sampling;
            // otherwise the loop would keep appending to the initial
            // (vertical) accumulator even after Horizontal was selected.
            accumulator = _longScreenshotAccumulator ?? accumulator;

            if (!_target.IsEmpty)
            {
                var focused = await _focus.EnsureFocusedAsync(_target, _cancellationToken)
                    .ConfigureAwait(true);
                if (focused.IsFailure)
                    throw new InvalidOperationException(focused.Error.Message);
            }

            await Task.Delay(LongScreenshotComposer.InitialSettleDelay, _cancellationToken)
                .ConfigureAwait(true);
            while (true)
            {
                ShowLongScreenshotSelectionBorder(selectedPhysical);
                // A capture that cannot be matched is not an end-of-session
                // signal. Dynamic pages and a scroll that starts inside an
                // animation can produce a changed frame that is rejected by
                // the stitcher; keep waiting for the next user scroll. The
                // frame budget is therefore based on accepted frames, not
                // polling attempts.
                while (accumulator.Count < LongScreenshotComposer.MaximumFrames)
                {
                    if (_keyboard.IsPressed(KeyboardKey.Escape))
                        _completeLongScreenshotRequested = true;
                    if (_completeLongScreenshotRequested || _finished)
                        break;

                    var captured = await WaitForUserViewportChangeAsync(selectedPhysical, accumulator.Last)
                        .ConfigureAwait(true);
                    if (captured.IsFailure)
                        throw new InvalidOperationException(captured.Error.Message);
                    if (captured.Value is null)
                        break;

                    var appended = accumulator.Append(captured.Value);
                    if (!appended)
                        continue;
                    _longScreenshotFrameCount = accumulator.Count;
                    ShowLongScreenshotPreview(accumulator, selectedPhysical);
                }

                if (_finished)
                    return;

                CloseLongScreenshotSelectionBorder();
                break;
            }

            var image = accumulator.CreateBitmap();
            ShowLongScreenshotResultReview(selectedPhysical, image);
            image = null;
        }
        catch (Exception exception)
        {
            Fail(exception);
        }
        finally
        {
            combined = null;
            _processing = false;
            if (_longScreenshotProgress is not null)
            {
                _longScreenshotProgress.StartRequested -= OnLongScreenshotStartRequested;
                _longScreenshotProgress.StopRequested -= OnLongScreenshotStopRequested;
                _longScreenshotProgress.CancelRequested -= OnLongScreenshotCancelRequested;
                _longScreenshotProgress.DirectionChanged -= OnLongScreenshotDirectionChanged;
                _longScreenshotProgress.CloseSessionWindow();
                _longScreenshotProgress = null;
            }
            _longScreenshotAccumulator = null;
            _longScreenshotStart = null;
            _longScreenshotStarted = false;
            CloseLongScreenshotSelectionBorder();
        }
    }

    private void ShowLongScreenshotPreview(
        LongScreenshotAccumulator accumulator,
        PhysicalScreenRegion selectedPhysical)
    {
        if (_longScreenshotProgress is null)
            return;
        var preview = accumulator.CreatePreviewBitmap();
        var previewScreen = GetLongScreenshotPreviewScreen(selectedPhysical);
        _longScreenshotProgress.ShowCapturePreview(
            GetLongScreenshotPreviewPosition(selectedPhysical, previewScreen),
            previewScreen.Bounds,
            previewScreen.ScaleX,
            previewScreen.ScaleY,
            preview,
            accumulator.Count,
            accumulator.Dimension,
            _longScreenshotDirection,
            _longScreenshotFrameCount <= 1,
            _longScreenshotStarted);
    }

    private void ShowLongScreenshotResultReview(
        PhysicalScreenRegion selection,
        Bitmap image)
    {
        _longScreenshotResultImage?.Dispose();
        _longScreenshotResultImage = image;
        _longScreenshotResultReview = true;
        _longScreenshotResultDialog = new LongScreenshotResultDialog();
        _longScreenshotResultDialog.ActionRequested += OnLongScreenshotResultActionRequested;
        _longScreenshotResultDialog.ResetRequested += OnLongScreenshotResultResetRequested;
        _longScreenshotResultDialog.CancelRequested += OnLongScreenshotResultCancelRequested;
        _longScreenshotResultDialog.SetImage(image, _defaultAction);
        _longScreenshotResultDialog.Show();
        _longScreenshotResultDialog.Activate();

        // The result is now reviewed in its own modeless dialog. Close the
        // full-desktop capture surfaces so the user's desktop is interactive
        // again while the dialog remains available.
        foreach (var surface in _surfaces)
            surface.View.HideSessionWindow();
    }

    private void OnLongScreenshotResultActionRequested(CaptureOverlayAction action)
    {
        if (action == CaptureOverlayAction.CopyLongScreenshot)
            RestartLongScreenshot();
        else
            CompleteLongScreenshotResult(action);
    }

    private void OnLongScreenshotResultResetRequested() => Reset();

    private void OnLongScreenshotResultCancelRequested() => Finish(null);

    private void RestartLongScreenshot()
    {
        if (_finished || !_longScreenshotResultReview || _selection.Region is null)
            return;

        _longScreenshotResultReview = false;
        CloseLongScreenshotResultDialog();
        _longScreenshotResultImage?.Dispose();
        _longScreenshotResultImage = null;
        foreach (var surface in _surfaces)
            surface.View.ShowSessionWindow();

        _processing = true;
        _completeLongScreenshotRequested = false;
        _longScreenshotFrameCount = 0;
        _ = CompleteLongScreenshotAsync();
    }

    private void CloseLongScreenshotResultDialog()
    {
        if (_longScreenshotResultDialog is null)
            return;
        _longScreenshotResultDialog.ActionRequested -= OnLongScreenshotResultActionRequested;
        _longScreenshotResultDialog.ResetRequested -= OnLongScreenshotResultResetRequested;
        _longScreenshotResultDialog.CancelRequested -= OnLongScreenshotResultCancelRequested;
        _longScreenshotResultDialog.CloseSessionWindow();
        _longScreenshotResultDialog = null;
    }

    private void ShowLongScreenshotSelectionBorder(PhysicalScreenRegion selection)
    {
        if (_longScreenshotSelectionBorder is not null)
            return;
        var screen = GetLongScreenshotPreviewScreen(selection);
        _longScreenshotSelectionBorder = new LongScreenshotSelectionBorderView(
            screen,
            selection,
            _windowBehavior);
        _longScreenshotSelectionBorder.ShowSessionWindow();
    }

    private void CloseLongScreenshotSelectionBorder()
    {
        if (_longScreenshotSelectionBorder is null)
            return;
        _longScreenshotSelectionBorder.CloseSessionWindow();
        _longScreenshotSelectionBorder = null;
    }

    private void CompleteLongScreenshotResult(CaptureOverlayAction action)
    {
        if (_longScreenshotResultImage is null ||
            _selection.Region is not { } selected ||
            _completionPoint is not { } completionPoint)
        {
            Finish(null);
            return;
        }

        var image = _longScreenshotResultImage;
        _longScreenshotResultImage = null;
        _longScreenshotResultReview = false;
        CloseLongScreenshotResultDialog();
        var outcome = new CaptureOverlayOutcome(
            ToPhysicalRegion(selected),
            completionPoint,
            action,
            image);
        Finish(outcome);
    }

    private PhysicalScreenPoint GetLongScreenshotPreviewPosition(
        PhysicalScreenRegion selection,
        ScreenDescriptor screen)
    {
        const int previewLogicalWidth = 260;
        const int previewLogicalHeight = 320;
        const int edgeMargin = 12;
        const int selectionGap = 8;
        var safeScaleX = screen.ScaleX > 0 ? screen.ScaleX : 1d;
        var safeScaleY = screen.ScaleY > 0 ? screen.ScaleY : 1d;
        var previewWidth = Math.Max(1, (int)Math.Ceiling(previewLogicalWidth * safeScaleX));
        var previewHeight = Math.Max(1, (int)Math.Ceiling(previewLogicalHeight * safeScaleY));
        var desktopLeft = screen.Bounds.X + edgeMargin;
        var desktopTop = screen.Bounds.Y + edgeMargin;
        var desktopRight = checked(screen.Bounds.X + screen.Bounds.Width) - edgeMargin;
        var desktopBottom = checked(screen.Bounds.Y + screen.Bounds.Height) - edgeMargin;
        var maxLeft = Math.Max(desktopLeft, desktopRight - previewWidth);
        var maxTop = Math.Max(desktopTop, desktopBottom - previewHeight);
        var top = Math.Clamp(selection.Y, desktopTop, maxTop);
        var right = checked(selection.X + selection.Width) + selectionGap;
        if (right + previewWidth <= desktopRight)
            return new PhysicalScreenPoint(right, top);

        var left = selection.X - previewWidth - selectionGap;
        if (left >= desktopLeft)
            return new PhysicalScreenPoint(left, top);

        var horizontal = Math.Clamp(selection.X, desktopLeft, maxLeft);
        var bottom = checked(selection.Y + selection.Height) + selectionGap;
        if (bottom + previewHeight <= desktopBottom)
            return new PhysicalScreenPoint(horizontal, bottom);

        var above = selection.Y - previewHeight - selectionGap;
        if (above >= desktopTop)
            return new PhysicalScreenPoint(horizontal, above);

        return new PhysicalScreenPoint(maxLeft, desktopTop);
    }

    private ScreenDescriptor GetLongScreenshotPreviewScreen(PhysicalScreenRegion selection)
    {
        var center = new PhysicalScreenPoint(
            selection.X + selection.Width / 2,
            selection.Y + selection.Height / 2);
        var containing = _screens.FirstOrDefault(screen =>
            center.X >= screen.Bounds.X &&
            center.X < checked(screen.Bounds.X + screen.Bounds.Width) &&
            center.Y >= screen.Bounds.Y &&
            center.Y < checked(screen.Bounds.Y + screen.Bounds.Height));
        if (containing is not null)
            return containing;

        // A selection can straddle displays. Prefer the display containing the
        // largest part of the selection, then fall back to the primary screen.
        return _screens
            .OrderByDescending(screen => IntersectionArea(selection, screen.Bounds))
            .ThenByDescending(screen => screen.IsPrimary)
            .FirstOrDefault()
            ?? _screens[0];
    }

    private static long IntersectionArea(
        PhysicalScreenRegion first,
        PhysicalScreenRegion second)
    {
        var left = Math.Max(first.X, second.X);
        var top = Math.Max(first.Y, second.Y);
        var right = Math.Min(checked(first.X + first.Width), checked(second.X + second.Width));
        var bottom = Math.Min(checked(first.Y + first.Height), checked(second.Y + second.Height));
        return right > left && bottom > top
            ? (long)(right - left) * (bottom - top)
            : 0L;
    }

    private int GetLongScreenshotMaximumDimension() =>
        _longScreenshotDirection == LongScreenshotDirection.Vertical
            ? LongScreenshotComposer.MaximumHeight
            : LongScreenshotComposer.MaximumWidth;

    private void OnLongScreenshotDirectionChanged(LongScreenshotDirection direction)
    {
        if (_finished || _longScreenshotStarted || _longScreenshotFrameCount > 1)
            return;
        _longScreenshotDirection = direction;
        if (_longScreenshotAccumulator is not null && _longScreenshotSelection is { } selection)
        {
            var first = _longScreenshotAccumulator.First;
            _longScreenshotAccumulator = LongScreenshotComposer.CreateAccumulator(
                first,
                direction,
                _stitcher);
            ShowLongScreenshotPreview(_longScreenshotAccumulator, selection);
        }
    }

    private void OnLongScreenshotStopRequested()
    {
        if (!_longScreenshotStarted)
            return;
        _completeLongScreenshotRequested = true;
    }

    private void OnLongScreenshotStartRequested(LongScreenshotDirection direction)
    {
        if (_finished || _longScreenshotStarted)
            return;
        _longScreenshotDirection = direction;
        _longScreenshotStarted = true;
        if (_longScreenshotAccumulator is not null &&
            _longScreenshotFrameCount <= 1 &&
            _longScreenshotSelection is { } selection &&
            _longScreenshotAccumulator.Count == 1)
        {
            var first = _longScreenshotAccumulator.First;
            _longScreenshotAccumulator = LongScreenshotComposer.CreateAccumulator(
                first,
                direction,
                _stitcher);
            ShowLongScreenshotPreview(_longScreenshotAccumulator, selection);
        }
        _longScreenshotProgress?.SetCaptureStarted();
        _longScreenshotStart?.TrySetResult(true);
    }

    private void OnLongScreenshotCancelRequested()
    {
        _completeLongScreenshotRequested = true;
        _longScreenshotStart?.TrySetResult(false);
        _longScreenshotProgress?.CloseSessionWindow();
        Finish(null);
    }

    private async ValueTask<Result<ImageFrame?>> WaitForUserViewportChangeAsync(
        PhysicalScreenRegion region,
        ImageFrame previous)
    {
        while (!_completeLongScreenshotRequested)
        {
            await Task.Delay(LongScreenshotComposer.SettleDelay, _cancellationToken)
                .ConfigureAwait(true);
            if (_completeLongScreenshotRequested)
                break;

            var latest = await CaptureLongScreenshotFrameAsync(region).ConfigureAwait(true);
            if (latest.IsFailure)
                return Result<ImageFrame?>.Failure(latest.Error);
            if (LongScreenshotComposer.IsSameViewport(
                    previous,
                    latest.Value,
                    _longScreenshotDirection))
                continue;

            var changed = latest.Value;
            var stableSamples = 1;
            for (var attempt = 0; attempt < LongScreenshotComposer.MaximumSettleSamples; attempt++)
            {
                await Task.Delay(LongScreenshotComposer.SettleDelay, _cancellationToken)
                    .ConfigureAwait(true);
                if (_completeLongScreenshotRequested)
                    break;

                latest = await CaptureLongScreenshotFrameAsync(region).ConfigureAwait(true);
                if (latest.IsFailure)
                    return Result<ImageFrame?>.Failure(latest.Error);
                if (LongScreenshotComposer.IsSameViewport(
                        changed,
                        latest.Value,
                        _longScreenshotDirection))
                {
                    stableSamples++;
                    if (stableSamples >= LongScreenshotComposer.StableViewportSamples)
                        return Result<ImageFrame?>.Success(latest.Value);
                }
                else
                {
                    // The user is still scrolling. Deliver the last complete
                    // viewport now so the outer loop can sample the next one
                    // instead of waiting for the gesture to stop.
                    return Result<ImageFrame?>.Success(changed);
                }
            }

            // Do not append a frame that was still changing when the guard
            // expired. The next loop iteration will retry after the viewport
            // has had another settle interval.
            continue;
        }

        return Result<ImageFrame?>.Success(null);
    }

    private async ValueTask<Result<ImageFrame>> CaptureLongScreenshotFrameAsync(
        PhysicalScreenRegion region)
    {
        var previewHidden = _longScreenshotProgress?.HideForCapture(region) == true;
        var borderHidden = _longScreenshotSelectionBorder?.HideForCapture() == true;
        try
        {
            return await _capture.CaptureAsync(
                new ScreenCaptureRequest(ScreenCaptureTarget.Region, Region: region),
                _cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            _longScreenshotSelectionBorder?.RestoreAfterCapture(borderHidden);
            _longScreenshotProgress?.RestoreAfterCapture(previewHidden);
        }
    }

    private void Finish(CaptureOverlayOutcome? outcome)
    {
        if (_finished)
        {
            outcome?.Image?.Dispose();
            return;
        }
        _finished = true;
        if (outcome?.Image is not null && ReferenceEquals(outcome.Image, _longScreenshotResultImage))
            _longScreenshotResultImage = null;
        else
        {
            _longScreenshotResultImage?.Dispose();
            _longScreenshotResultImage = null;
        }
        _longScreenshotResultReview = false;
        CloseLongScreenshotResultDialog();
        CloseLongScreenshotSelectionBorder();
        var closeFailure = CloseAll();
        if (closeFailure is null)
        {
            _completion.TrySetResult(outcome);
        }
        else
        {
            outcome?.Image?.Dispose();
            _completion.TrySetException(closeFailure);
        }
    }

    private void Fail(Exception exception)
    {
        if (_finished)
            return;
        _finished = true;
        _longScreenshotResultImage?.Dispose();
        _longScreenshotResultImage = null;
        _longScreenshotResultReview = false;
        CloseLongScreenshotResultDialog();
        CloseLongScreenshotSelectionBorder();
        var closeFailure = CloseAll();
        _completion.TrySetException(closeFailure is null
            ? exception
            : new AggregateException(exception, closeFailure));
    }

    private void RenderAll()
    {
        PhysicalScreenRegion? region = _selection.Region is { } value
            ? ToPhysicalRegion(value)
            : null;
        foreach (var surface in _surfaces)
        {
            surface.View.RenderSelection(
                region,
                _selection.Mode,
                showToolbar: ReferenceEquals(surface.View, _toolbarView));
        }
    }

    private OverlayWindowView? FindView(PhysicalScreenPoint point) =>
        _surfaces.Select(surface => surface.View).FirstOrDefault(view =>
            Contains(view.Screen.Bounds, point));

    private OverlayWindowView FindToolbarView(
        PhysicalScreenPoint point,
        OverlayWindowView fallback)
    {
        if (_selection.Region is not { } selection)
            return fallback;
        var candidates = _surfaces
            .Select(surface => surface.View)
            .Where(view => PhysicalPixelGeometry.Intersect(
                selection,
                ToPixelRect(view.Screen.Bounds)) is not null)
            .ToArray();
        return candidates.FirstOrDefault(view => Contains(view.Screen.Bounds, point))
               ?? candidates.FirstOrDefault(view => ContainsInclusive(view.Screen.Bounds, point))
               ?? candidates.FirstOrDefault(view => ReferenceEquals(view, fallback))
               ?? candidates.FirstOrDefault()
               ?? fallback;
    }

    private Exception? CloseAll()
    {
        List<Exception>? failures = null;
        foreach (var surface in _surfaces)
        {
            try
            {
                surface.View.CloseSessionWindow();
            }
            catch (Exception exception)
            {
                failures ??= [];
                failures.Add(exception);
            }
        }
        return failures?.Count switch
        {
            null or 0 => null,
            1 => failures[0],
            _ => new AggregateException(failures)
        };
    }

    private void HideAll()
    {
        foreach (var surface in _surfaces)
            surface.View.HideSessionWindow();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static PixelRect ToPixelRect(PhysicalScreenRegion region) =>
        new(region.X, region.Y, region.Width, region.Height);

    private static PixelPoint ToPixelPoint(PhysicalScreenPoint point) => new(point.X, point.Y);

    private static PhysicalScreenPoint ToPhysicalPoint(PixelPoint point) => new(point.X, point.Y);

    private static PhysicalScreenRegion ToPhysicalRegion(PixelRect region) =>
        new(region.X, region.Y, region.Width, region.Height);

    private static RenderTargetBitmap RenderCrop(Bitmap sourceBitmap, PixelRect crop)
    {
        using var source = new CroppedBitmap(sourceBitmap, crop);
        var result = new RenderTargetBitmap(crop.Size, new Vector(96, 96));
        try
        {
            using (var context = result.CreateDrawingContext())
                context.DrawImage(source, new Rect(0, 0, crop.Width, crop.Height));
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    private static bool Contains(PhysicalScreenRegion region, PhysicalScreenPoint point) =>
        point.X >= region.X && point.X < checked(region.X + region.Width) &&
        point.Y >= region.Y && point.Y < checked(region.Y + region.Height);

    private static bool ContainsInclusive(PhysicalScreenRegion region, PhysicalScreenPoint point) =>
        point.X >= region.X && point.X <= checked(region.X + region.Width) &&
        point.Y >= region.Y && point.Y <= checked(region.Y + region.Height);

    private static PhysicalScreenPoint ScreenCenter(PhysicalScreenRegion region) => new(
        region.X + region.Width / 2,
        region.Y + region.Height / 2);

    private sealed record OverlaySurface(OverlayWindowView View, CroppedBitmap Background);
}
