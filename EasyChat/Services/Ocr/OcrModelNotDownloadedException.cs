using System;

namespace EasyChat.Services.Ocr;

public sealed class OcrModelNotDownloadedException : Exception
{
    public OcrModelNotDownloadedException(OcrLanguage language)
        : base($"OCR model is not downloaded for {language.Id}.")
    {
        Language = language;
    }

    public OcrLanguage Language { get; }
}
