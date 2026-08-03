using Avalonia.Threading;
using EasyChat.Contracts.Platform;
using EasyChat.Presentation.Features.Settings.State;
using EasyChat.Presentation.Features.Translation;
using EasyChat.Presentation.ImageTranslation;
using EasyChat.Presentation.Features.Capture.Views;
using Microsoft.Extensions.Logging;
using SukiUI.Toasts;

namespace EasyChat.Presentation.Features.Capture;

public sealed class ScreenshotResultSession(ResultView view)
{
    private readonly ResultView _view = view;

    public bool IsClosed { get; private set; }

    internal void ObserveLifetime() => _view.Closed += (_, _) => IsClosed = true;
    public void Append(string text) => _view.AppendText(text);
    public void CloseAfterDelay(int milliseconds) => _view.CloseAfterDelay(milliseconds);
    public void Close() => _view.Close();
}

public sealed class ScreenshotResultCoordinator(
    SettingsSession settings,
    ITranslationWindowCoordinator translationWindow,
    IPointerPosition pointer,
    IClipboardText clipboard,
    ISukiToastManager toasts,
    ILoggerFactory loggerFactory)
{
    private readonly SettingsSession _settings = settings;
    private readonly ITranslationWindowCoordinator _translationWindow = translationWindow;
    private readonly IPointerPosition _pointer = pointer;
    private readonly IClipboardText _clipboard = clipboard;
    private readonly ISukiToastManager _toasts = toasts;
    private readonly ILoggerFactory _loggerFactory = loggerFactory;

    public ValueTask<ScreenshotResultSession> OpenClassicAsync(
        CancellationToken cancellationToken = default) =>
        OnUiAsync(() =>
        {
            var view = new ResultView(_settings);
            var session = new ScreenshotResultSession(view);
            session.ObserveLifetime();
            view.ShowLoading();
            view.Show();
            return session;
        }, cancellationToken);

    public ValueTask ShowDictionaryAsync(
        string text,
        CancellationToken cancellationToken = default) =>
        _translationWindow.ShowSentenceAsync(
            text,
            _pointer.GetCurrent(),
            showCloseButton: true,
            cancellationToken);

    public ValueTask ShowImageAsync(
        ImageFrame image,
        IReadOnlyList<string> warnings,
        CancellationToken cancellationToken = default) =>
        OnUiAsync(() =>
        {
            var bitmap = AvaloniaImageFrames.ToBitmap(image);
            new ImageTranslationResultWindow(
                bitmap,
                warnings,
                _loggerFactory.CreateLogger<ImageTranslationResultWindow>()).Show();
        }, cancellationToken);

    public async ValueTask CopyTextAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        var result = await _clipboard.WriteAsync(text, cancellationToken).ConfigureAwait(false);
        await OnUiAsync(() => ShowMessage(
            result.IsSuccess ? "Copied" : "Copy Error",
            result.IsSuccess ? "Text copied to clipboard." : result.Error.Message), cancellationToken);
    }

    public ValueTask ShowMessageAsync(
        string title,
        string message,
        CancellationToken cancellationToken = default) =>
        OnUiAsync(() => ShowMessage(title, message), cancellationToken);

    private void ShowMessage(string title, string message) => _toasts.CreateSimpleInfoToast()
        .WithTitle(title)
        .WithContent(message)
        .Queue();

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
}
