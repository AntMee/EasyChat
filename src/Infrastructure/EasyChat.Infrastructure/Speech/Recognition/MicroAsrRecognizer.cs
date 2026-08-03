using MicroASR;

namespace EasyChat.Infrastructure.Speech.Recognition;

internal enum MicroAsrResultKind
{
    Partial,
    Final,
    Error
}

internal sealed record MicroAsrResult(
    MicroAsrResultKind Kind,
    string Text,
    Exception? Exception = null);

internal interface IMicroAsrRecognizer : IAsyncDisposable
{
    event Action<MicroAsrResult>? ResultAvailable;

    ValueTask WriteAsync(
        ReadOnlyMemory<byte> pcm16,
        CancellationToken cancellationToken = default);

    Task CompleteAsync(CancellationToken cancellationToken = default);
}

internal interface IMicroAsrRecognizerFactory
{
    IMicroAsrRecognizer Create(string modelDirectory);
}

internal sealed class MicroAsrRecognizerFactory : IMicroAsrRecognizerFactory
{
    public IMicroAsrRecognizer Create(string modelDirectory) =>
        new MicroAsrRecognizerAdapter(modelDirectory);
}

internal sealed class MicroAsrRecognizerAdapter : IMicroAsrRecognizer
{
    private readonly StreamingRecognizer _recognizer;

    public MicroAsrRecognizerAdapter(string modelDirectory)
    {
        _recognizer = new StreamingRecognizer(modelDirectory, RnntRecognitionMode.Balanced);
        _recognizer.ResultAvailable += OnResultAvailable;
    }

    public event Action<MicroAsrResult>? ResultAvailable;

    public ValueTask WriteAsync(
        ReadOnlyMemory<byte> pcm16,
        CancellationToken cancellationToken = default) =>
        _recognizer.WriteAsync(pcm16, cancellationToken);

    public Task CompleteAsync(CancellationToken cancellationToken = default) =>
        _recognizer.CompleteAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        _recognizer.ResultAvailable -= OnResultAvailable;
        await _recognizer.DisposeAsync().ConfigureAwait(false);
    }

    private void OnResultAvailable(RecognitionEvent result)
    {
        var mapped = result.Type switch
        {
            RecognitionEventType.Partial => new MicroAsrResult(
                MicroAsrResultKind.Partial,
                result.Text),
            RecognitionEventType.Final => new MicroAsrResult(
                MicroAsrResultKind.Final,
                result.Text),
            RecognitionEventType.Error => new MicroAsrResult(
                MicroAsrResultKind.Error,
                result.Text,
                result.Exception),
            _ => null
        };
        if (mapped is not null)
            ResultAvailable?.Invoke(mapped);
    }
}
