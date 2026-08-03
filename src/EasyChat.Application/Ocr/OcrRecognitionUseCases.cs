using EasyChat.Contracts.Ocr;

namespace EasyChat.Application.Ocr;

public sealed class OcrRecognitionUseCases : IOcrRecognitionUseCases
{
    private readonly IOcrRecognizer _recognizer;

    public OcrRecognitionUseCases(IOcrRecognizer recognizer)
    {
        _recognizer = recognizer ?? throw new ArgumentNullException(nameof(recognizer));
    }

    public ValueTask<OcrRecognitionResult> RecognizeAsync(
        OcrRecognitionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var resolved = request.Language is null
            ? request with { Language = OcrLanguages.ChineseSimplified }
            : request;
        return _recognizer.RecognizeAsync(resolved, cancellationToken);
    }
}
