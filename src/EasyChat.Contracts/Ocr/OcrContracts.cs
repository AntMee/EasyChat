using EasyChat.Contracts.Platform;

namespace EasyChat.Contracts.Ocr;

public sealed record OcrLanguage(string Id, string DisplayName, string? NativeName = null);

public static class OcrLanguages
{
    public static OcrLanguage ChineseSimplified { get; } =
        new("zh-Hans", "Chinese (Simplified)", "\u7b80\u4f53\u4e2d\u6587");

    public static OcrLanguage ChineseTraditional { get; } =
        new("zh-Hant", "Chinese (Traditional)", "\u7e41\u9ad4\u4e2d\u6587");

    public static OcrLanguage English { get; } = new("en", "English");
    public static OcrLanguage Japanese { get; } = new("ja", "Japanese", "\u65e5\u672c\u8a9e");
    public static OcrLanguage Korean { get; } = new("ko", "Korean", "\ud55c\uad6d\uc5b4");
    public static OcrLanguage Auto { get; } = new("auto", "Auto Detect", "\u81ea\u52a8\u68c0\u6d4b");
    public static OcrLanguage Arabic { get; } = new("ar", "Arabic", "\u0627\u0644\u0639\u0631\u0628\u064a\u0629");
    public static OcrLanguage Devanagari { get; } = new("hi", "Devanagari", "\u0926\u0947\u0935\u0928\u093e\u0917\u0930\u0940");
    public static OcrLanguage Tamil { get; } = new("ta", "Tamil", "\u0ba4\u0bae\u0bbf\u0bb4\u0bcd");
    public static OcrLanguage Telugu { get; } = new("te", "Telugu", "\u0c24\u0c46\u0c32\u0c41\u0c17\u0c41");
    public static OcrLanguage Kannada { get; } = new("kn", "Kannada", "\u0c95\u0ca8\u0ccd\u0ca8\u0ca1");

    public static IReadOnlyList<OcrLanguage> Supported { get; } =
    [
        ChineseSimplified,
        ChineseTraditional,
        English,
        Japanese,
        Korean,
        Arabic,
        Devanagari,
        Tamil,
        Telugu,
        Kannada
    ];
}

public readonly record struct ImagePoint(double X, double Y);

public sealed record OcrTextRegion(
    string Text,
    IReadOnlyList<ImagePoint> Polygon,
    double Angle,
    double Confidence = 1d);

public sealed record OcrRecognitionResult(IReadOnlyList<OcrTextRegion> Regions)
{
    public string Text => string.Join("\n", Regions.Select(region => region.Text));
}

public sealed record OcrRecognitionRequest(
    ImageFrame Image,
    OcrLanguage? Language = null,
    bool EnableRotation = false);

public sealed record OcrModelDownloadOptions(string? ProxyUrl, bool UseProxy);

public interface IOcrRecognizer
{
    ValueTask<OcrRecognitionResult> RecognizeAsync(
        OcrRecognitionRequest request,
        CancellationToken cancellationToken = default);
}

public interface IOcrModelStore
{
    IReadOnlyList<OcrLanguage> SupportedLanguages { get; }

    bool CanDeleteModels { get; }

    bool IsModelDownloaded(OcrLanguage language);

    Task DownloadModelAsync(
        OcrLanguage language,
        OcrModelDownloadOptions options,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);

    void DeleteModel(OcrLanguage language);
}

public interface IOcrRecognitionUseCases
{
    ValueTask<OcrRecognitionResult> RecognizeAsync(
        OcrRecognitionRequest request,
        CancellationToken cancellationToken = default);
}

public interface IOcrModelUseCases
{
    IReadOnlyList<OcrLanguage> SupportedLanguages { get; }

    bool CanDeleteModels { get; }

    bool IsModelDownloaded(OcrLanguage language);

    Task DownloadModelAsync(
        OcrLanguage language,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);

    void DeleteModel(OcrLanguage language);
}

public sealed class OcrModelNotDownloadedException : Exception
{
    public OcrModelNotDownloadedException(OcrLanguage language)
        : base($"OCR model is not downloaded for {language.Id}.")
    {
        Language = language ?? throw new ArgumentNullException(nameof(language));
    }

    public OcrLanguage Language { get; }
}
