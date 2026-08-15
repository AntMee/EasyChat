using EasyChat.Contracts.Platform;

namespace EasyChat.Presentation.Features.Speech;

public sealed class SpeechInterpretationHotkeyController(
    IAudioFeedbackCuePlayer feedback)
{
    private SpeechRecognitionViewModel? _viewModel;

    public void Attach(SpeechRecognitionViewModel viewModel) =>
        Volatile.Write(ref _viewModel, viewModel);

    public void Detach(SpeechRecognitionViewModel viewModel) =>
        Interlocked.CompareExchange(ref _viewModel, null, viewModel);

    public async ValueTask PressAsync(CancellationToken cancellationToken = default)
    {
        var viewModel = Volatile.Read(ref _viewModel);
        if (viewModel is null
            || !viewModel.IsRealtimeInterpretationAvailable
            || !viewModel.IsRealtimeInterpretationArmed
            || viewModel.IsRealtimeInterpretationRecording)
        {
            return;
        }

        await PlayQuietlyAsync(AudioFeedbackCue.RealtimeInterpretationStarted, cancellationToken);
        await viewModel.BeginRealtimeInterpretationHoldAsync(cancellationToken);
    }

    public async ValueTask ReleaseAsync(CancellationToken cancellationToken = default)
    {
        var viewModel = Volatile.Read(ref _viewModel);
        if (viewModel is null || !viewModel.IsRealtimeInterpretationRecording)
            return;

        await PlayQuietlyAsync(AudioFeedbackCue.RealtimeInterpretationReleased, cancellationToken);
        await viewModel.EndRealtimeInterpretationHoldAsync(cancellationToken);
    }

    public ValueTask PlayTranslationCompletedFeedbackAsync(
        CancellationToken cancellationToken = default) =>
        PlayQuietlyAsync(AudioFeedbackCue.RealtimeInterpretationCompleted, cancellationToken);

    private async ValueTask PlayQuietlyAsync(
        AudioFeedbackCue cue,
        CancellationToken cancellationToken)
    {
        try
        {
            await feedback.PlayAsync(cue, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            // Feedback must never interrupt a realtime interpretation session.
        }
    }
}
