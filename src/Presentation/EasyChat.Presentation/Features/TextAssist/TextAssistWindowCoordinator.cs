using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using EasyChat.Contracts.Platform;
using EasyChat.Contracts.Speech;
using EasyChat.Contracts.TextAssist;
using EasyChat.Presentation.Features.Settings.State;
using EasyChat.Presentation.Features.Translation;
using EasyChat.Presentation.Features.TextAssist.Views;
using EasyChat.Presentation.Foundation.Localization;
using EasyChat.Presentation.Foundation.Platform;
using Microsoft.Extensions.Logging;

namespace EasyChat.Presentation.Features.TextAssist;

public interface ITextAssistWindowCoordinator
{
    ValueTask ShowEditorAsync(
        string text,
        bool correction,
        CancellationToken cancellationToken = default);

    ValueTask<bool> CloseEditorIfOpenAsync(CancellationToken cancellationToken = default);

    ValueTask ShowResultAsync(
        string text,
        TextAssistOperation operation,
        PhysicalScreenPoint anchor,
        CancellationToken cancellationToken = default);

    ValueTask<bool> ContainsResultAsync(
        PhysicalScreenPoint point,
        CancellationToken cancellationToken = default);

    ValueTask<bool> IsResultVisibleAsync(CancellationToken cancellationToken = default);

    ValueTask CloseResultAsync(CancellationToken cancellationToken = default);
}

public sealed class TextAssistWindowCoordinator(
    SettingsSession settings,
    TranslationLanguageOptions languages,
    ITextAssistUseCases textAssist,
    ITranslationWindowCoordinator dictionary,
    ITtsUseCases tts,
    IPlatformWindowBehavior platformWindowBehavior,
    ILoggerFactory loggerFactory) : ITextAssistWindowCoordinator
{
    private TextAssistWindowView? _editor;
    private TextAssistResultWindowView? _result;

    public async ValueTask ShowEditorAsync(
        string text,
        bool correction,
        CancellationToken cancellationToken = default)
    {
        var initialization = await OnUiAsync(() =>
        {
            _editor?.Close();
            var viewModel = new TextAssistViewModel(settings, languages, textAssist, dictionary, tts, loggerFactory);
            _editor = new TextAssistWindowView(viewModel);
            _editor.Closed += (_, _) => _editor = null;
            _editor.Show();
            return _editor.InitializeAsync(text, correction);
        }, cancellationToken);
        await initialization;
    }

    public ValueTask<bool> CloseEditorIfOpenAsync(CancellationToken cancellationToken = default) =>
        OnUiAsync(() =>
        {
            if (_editor is null)
                return false;
            var window = _editor;
            _editor = null;
            window.Close();
            return true;
        }, cancellationToken);

    public async ValueTask ShowResultAsync(
        string text,
        TextAssistOperation operation,
        PhysicalScreenPoint anchor,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var window = await OnUiAsync(() =>
        {
            _result?.Close();
            var viewModel = new TextAssistResultWindowViewModel(
                settings,
                languages,
                textAssist);
            _result = new TextAssistResultWindowView(
                viewModel,
                platformWindowBehavior,
                loggerFactory.CreateLogger<TextAssistResultWindowView>());
            _result.Closed += (_, _) => _result = null;
            PositionNear(_result, anchor);
            _result.Show();
            return _result;
        }, cancellationToken);
        await window.InitializeAsync(text, operation);
    }

    public ValueTask<bool> ContainsResultAsync(
        PhysicalScreenPoint point,
        CancellationToken cancellationToken = default) =>
        OnUiAsync(() =>
        {
            if (_result?.IsVisible != true) return false;
            var client = _result.PointToClient(new PixelPoint(point.X, point.Y));
            return new Rect(_result.Bounds.Size).Contains(client);
        }, cancellationToken);

    public ValueTask<bool> IsResultVisibleAsync(CancellationToken cancellationToken = default) =>
        OnUiAsync(() => _result?.IsVisible == true, cancellationToken);

    public ValueTask CloseResultAsync(CancellationToken cancellationToken = default) =>
        OnUiAsync(() =>
        {
            _result?.Close();
            _result = null;
        }, cancellationToken);

    private static void PositionNear(Window window, PhysicalScreenPoint point)
    {
        var screen = window.Screens.ScreenFromPoint(new PixelPoint(point.X, point.Y)) ?? window.Screens.Primary;
        if (screen is null)
        {
            window.Position = new PixelPoint(point.X + 16, point.Y + 16);
            return;
        }
        var area = screen.WorkingArea;
        var scale = screen.Scaling;
        var width = Math.Max(1, (int)Math.Ceiling(window.Width * scale));
        var height = Math.Max(1, (int)Math.Ceiling(window.Height * scale));
        var offset = Math.Max(8, (int)Math.Ceiling(16 * scale));
        var left = point.X + offset;
        var top = point.Y + offset;
        if (left + width > area.Right) left = point.X - width - offset;
        if (top + height > area.Bottom) top = point.Y - height - offset;
        left = Math.Clamp(left, area.X, Math.Max(area.X, area.Right - width));
        top = Math.Clamp(top, area.Y, Math.Max(area.Y, area.Bottom - height));
        window.Position = new PixelPoint(left, top);
    }

    private static async ValueTask OnUiAsync(Action action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return;
        }
        await Dispatcher.UIThread.InvokeAsync(action, DispatcherPriority.Normal, cancellationToken);
    }

    private static async ValueTask<T> OnUiAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Dispatcher.UIThread.CheckAccess()) return action();
        return await Dispatcher.UIThread.InvokeAsync(action, DispatcherPriority.Normal, cancellationToken);
    }
}
