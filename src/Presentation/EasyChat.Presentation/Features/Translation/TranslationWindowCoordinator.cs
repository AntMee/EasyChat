using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using EasyChat.Contracts.Platform;
using EasyChat.Contracts.SelectionTranslation;
using EasyChat.Contracts.Speech;
using EasyChat.Contracts.Translation;
using EasyChat.Presentation.Features.Settings.State;
using EasyChat.Presentation.Features.Translation.Views;
using EasyChat.Presentation.Foundation.Localization;
using EasyChat.Presentation.Foundation.Platform;
using Microsoft.Extensions.Logging;

namespace EasyChat.Presentation.Features.Translation;

public interface ITranslationWindowCoordinator
{
    ValueTask PrewarmAsync(CancellationToken cancellationToken = default);

    ValueTask ShowSentenceAsync(
        string text,
        PhysicalScreenPoint? anchor = null,
        bool showCloseButton = true,
        CancellationToken cancellationToken = default);

    ValueTask ShowSentenceAsync(
        string text,
        PhysicalScreenPoint? anchor,
        bool showCloseButton,
        SelectionTranslationConfigurationScope configurationScope,
        CancellationToken cancellationToken = default) =>
        ShowSentenceAsync(text, anchor, showCloseButton, cancellationToken);

    ValueTask ShowDictionaryAsync(
        string text,
        string sourceLanguageId,
        string targetLanguageId,
        bool centerOnScreen = false,
        PhysicalScreenPoint? anchor = null,
        CancellationToken cancellationToken = default);

    ValueTask<bool> ContainsAsync(
        PhysicalScreenPoint point,
        CancellationToken cancellationToken = default);

    ValueTask<bool> IsVisibleAsync(CancellationToken cancellationToken = default);

    ValueTask CloseAsync(CancellationToken cancellationToken = default);
}

public sealed class TranslationWindowCoordinator(
    ISelectionTranslationUseCases translation,
    ITranslationLanguageCatalog languages,
    ITtsUseCases tts,
    SettingsSession settings,
    IPlatformWindowBehavior platformWindowBehavior,
    ILoggerFactory loggerFactory) : ITranslationWindowCoordinator
{
    private TranslationDictionaryWindowView? _current;
    private TranslationWindowSession? _prewarmed;

    public ValueTask PrewarmAsync(CancellationToken cancellationToken = default) =>
        OnUiAsync(() =>
        {
            _prewarmed ??= CreateWindow();
        }, cancellationToken);

    public async ValueTask ShowSentenceAsync(
        string text,
        PhysicalScreenPoint? anchor = null,
        bool showCloseButton = true,
        CancellationToken cancellationToken = default) =>
        await ShowSentenceAsync(
            text,
            anchor,
            showCloseButton,
            SelectionTranslationConfigurationScope.Selection,
            cancellationToken);

    public async ValueTask ShowSentenceAsync(
        string text,
        PhysicalScreenPoint? anchor,
        bool showCloseButton,
        SelectionTranslationConfigurationScope configurationScope,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        var window = await ShowShellAsync(
            anchor,
            centerOnScreen: false,
            showCloseButton,
            cancellationToken);
        await window.ViewModel.InitializeAsync(text, configurationScope);
    }

    public async ValueTask ShowDictionaryAsync(
        string text,
        string sourceLanguageId,
        string targetLanguageId,
        bool centerOnScreen = false,
        PhysicalScreenPoint? anchor = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        // This remains an independent topmost, non-activating window. Native ownership
        // changes the input activation chain on Windows and can detach a TSF composition
        // client when the dictionary closes.
        var window = await ShowShellAsync(
            anchor,
            centerOnScreen,
            showCloseButton: true,
            cancellationToken);
        await window.ViewModel.InitializeDictionaryAsync(text, sourceLanguageId, targetLanguageId);

        // Dictionary content is loaded after the shell is shown. A centered
        // window can therefore grow past the working area after its initial
        // placement. Re-clamp its final, measured bounds without overriding a
        // position the user explicitly moved.
        await OnUiAsync(() => ClampToWorkingAreaIfUnadjusted(window.View), cancellationToken);
    }

    public ValueTask<bool> ContainsAsync(
        PhysicalScreenPoint point,
        CancellationToken cancellationToken = default) =>
        OnUiAsync(() =>
        {
            if (_current?.IsVisible != true)
                return false;
            var clientPoint = _current.PointToClient(new PixelPoint(point.X, point.Y));
            return new Rect(_current.Bounds.Size).Contains(clientPoint);
        }, cancellationToken);

    public ValueTask<bool> IsVisibleAsync(CancellationToken cancellationToken = default) =>
        OnUiAsync(() => _current?.IsVisible == true, cancellationToken);

    public ValueTask CloseAsync(CancellationToken cancellationToken = default) =>
        OnUiAsync(() =>
        {
            _current?.Close();
            _current = null;
        }, cancellationToken);

    private async ValueTask<TranslationWindowSession> ShowShellAsync(
        PhysicalScreenPoint? anchor,
        bool centerOnScreen,
        bool showCloseButton,
        CancellationToken cancellationToken)
    {
        return await OnUiAsync(() =>
        {
            _current?.Close();
            var prepared = _prewarmed ?? CreateWindow();
            _prewarmed = null;
            _current = prepared.View;
            prepared.ViewModel.ShowCloseButton = showCloseButton;
            prepared.View.Closed += OnCurrentClosed;
            prepared.View.SizeChanged += OnWindowSizeChanged;

            // The window is prewarmed and reused. Reset this every time because
            // CenterScreen otherwise persists from a prior dictionary lookup and
            // overrides a Position set before Show().
            prepared.View.WindowStartupLocation = centerOnScreen
                ? WindowStartupLocation.CenterScreen
                : WindowStartupLocation.Manual;

            if (!centerOnScreen && anchor is { } point)
            {
                // Avalonia finalizes the native position after Show(). Keep the
                // shell invisible until its native position can be reapplied.
                prepared.View.Opacity = 0;
                var initialPosition = GetPositionNear(prepared.View, point);
                prepared.View.Position = initialPosition;
                EventHandler? positionAfterOpened = null;
                positionAfterOpened = (_, _) =>
                {
                    prepared.View.Opened -= positionAfterOpened;
                    // Bounds can transiently reflect SizeToContent's maximum
                    // constraint here. Reapply the coordinate calculated from
                    // the shell size instead of recalculating from that value.
                    prepared.View.Position = initialPosition;
                    prepared.View.Opacity = 1;
                };
                prepared.View.Opened += positionAfterOpened;
            }
            else
            {
                prepared.View.Opacity = 1;
            }

            prepared.View.Show();
            return prepared;
        }, cancellationToken);
    }

    private TranslationWindowSession CreateWindow()
    {
        var viewModel = new TranslationDictionaryWindowViewModel(translation, languages, tts, settings);
        var view = new TranslationDictionaryWindowView(
            viewModel,
            platformWindowBehavior,
            loggerFactory.CreateLogger<TranslationDictionaryWindowView>());
        return new TranslationWindowSession(view, viewModel);
    }

    private void OnCurrentClosed(object? sender, EventArgs args)
    {
        if (sender is TranslationDictionaryWindowView window)
        {
            window.Closed -= OnCurrentClosed;
            window.SizeChanged -= OnWindowSizeChanged;
        }

        if (ReferenceEquals(_current, sender))
            _current = null;

        Dispatcher.UIThread.Post(
            () => _prewarmed ??= CreateWindow(),
            DispatcherPriority.Background);
    }

    private static PixelPoint GetPositionNear(Window window, PhysicalScreenPoint point)
    {
        var screen = window.Screens.ScreenFromPoint(new PixelPoint(point.X, point.Y)) ?? window.Screens.Primary;
        if (screen is null)
            return new PixelPoint(point.X + 20, point.Y + 20);

        var logicalWidth = window.Width > 0 ? window.Width : 450;
        var logicalHeight = window.Height > 0 ? window.Height : 350;
        return TranslationWindowPlacement.Near(
            screen.WorkingArea,
            screen.Scaling,
            point,
            logicalWidth,
            logicalHeight,
            logicalOffset: 20);
    }

    private static void OnWindowSizeChanged(object? sender, SizeChangedEventArgs args)
    {
        if (sender is TranslationDictionaryWindowView { IsVisible: true } window)
            ClampToWorkingAreaIfUnadjusted(window);
    }

    private static void ClampToWorkingAreaIfUnadjusted(TranslationDictionaryWindowView window)
    {
        if (window.HasUserAdjustedBounds)
            return;

        window.UpdateLayout();
        var point = new PixelPoint(window.Position.X + 1, window.Position.Y + 1);
        var screen = window.Screens.ScreenFromPoint(point) ?? window.Screens.Primary;
        if (screen is null)
            return;

        var logicalWidth = window.Bounds.Width > 0 ? window.Bounds.Width : window.Width;
        var logicalHeight = window.Bounds.Height > 0 ? window.Bounds.Height : window.Height;
        window.Position = TranslationWindowPlacement.ClampToArea(
            screen.WorkingArea,
            screen.Scaling,
            window.Position,
            logicalWidth,
            logicalHeight);
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
        return await Dispatcher.UIThread.InvokeAsync(action, DispatcherPriority.Normal, cancellationToken);
    }

    private sealed record TranslationWindowSession(
        TranslationDictionaryWindowView View,
        TranslationDictionaryWindowViewModel ViewModel);
}
