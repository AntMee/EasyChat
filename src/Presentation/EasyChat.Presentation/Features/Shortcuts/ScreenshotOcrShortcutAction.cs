using EasyChat.Contracts.Capture;
using EasyChat.Contracts.Settings;
using EasyChat.Contracts.Shortcuts;
using EasyChat.Presentation.Features.Capture;
using EasyChat.Presentation.Features.Settings.State;
using Microsoft.Extensions.Logging;

namespace EasyChat.Presentation.Features.Shortcuts;

public sealed class ScreenshotOcrShortcutAction(
    ScreenshotCaptureCoordinator capture,
    ScreenshotShortcutAction dispatcher,
    ScreenshotResultCoordinator results,
    SettingsSession settings,
    ILogger<ScreenshotOcrShortcutAction> logger) : IShortcutAction
{
    public string ActionType => "ScreenshotOcr";
    public bool PreventConcurrentExecution => true;

    public async ValueTask ExecuteAsync(
        ShortcutParameterSettings? parameter,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var selection = await capture.CaptureAsync(
                settings.Screenshot.Mode,
                CaptureOverlayAction.OcrWorkbench,
                CaptureToolbarMode.Full,
                cancellationToken);
            if (selection is not null)
                _ = dispatcher.ProcessAsync(selection.Image, selection.Action, selection.CompletionPoint);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unable to start the screenshot OCR workbench.");
            await results.ShowMessageAsync("Screenshot OCR", exception.Message, cancellationToken);
        }
    }
}
