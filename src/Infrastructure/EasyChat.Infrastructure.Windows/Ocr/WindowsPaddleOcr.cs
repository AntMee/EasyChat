using System.Runtime.Versioning;
using EasyChat.Contracts.Ocr;
using EasyChat.Contracts.Platform;
using Microsoft.Extensions.Logging;
using OpenCvSharp;

namespace EasyChat.Infrastructure.Windows.Ocr;

[SupportedOSPlatform("windows")]
public sealed class WindowsPaddleOcr : IOcrRecognizer, IOcrModelStore, IDisposable
{
    private readonly IWindowsOcrBackend _backend;
    private readonly ILogger<WindowsPaddleOcr>? _logger;

    public WindowsPaddleOcr(ILogger<WindowsPaddleOcr>? logger = null)
        : this(new PaddleWindowsOcrBackend(logger), logger)
    {
    }

    internal WindowsPaddleOcr(
        IWindowsOcrBackend backend,
        ILogger<WindowsPaddleOcr>? logger = null)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _logger = logger;
    }

    public IReadOnlyList<OcrLanguage> SupportedLanguages => OcrLanguages.Supported;

    public bool CanDeleteModels => _backend.CanDeleteModels;

    public bool IsModelDownloaded(OcrLanguage language)
    {
        ArgumentNullException.ThrowIfNull(language);
        return _backend.IsModelAvailable(WindowsOcrLanguageCatalog.Resolve(language));
    }

    public Task DownloadModelAsync(
        OcrLanguage language,
        OcrModelDownloadOptions options,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(language);
        ArgumentNullException.ThrowIfNull(options);
        return _backend.DownloadModelAsync(
            WindowsOcrLanguageCatalog.Resolve(language),
            options,
            progress,
            cancellationToken);
    }

    public void DeleteModel(OcrLanguage language)
    {
        ArgumentNullException.ThrowIfNull(language);
        _backend.DeleteModel(WindowsOcrLanguageCatalog.Resolve(language));
    }

    public ValueTask<OcrRecognitionResult> RecognizeAsync(
        OcrRecognitionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (request.Image.PixelFormat != ImagePixelFormat.Bgra32)
            throw new NotSupportedException($"Pixel format '{request.Image.PixelFormat}' is not supported.");

        var language = ResolveLanguage(request.Language);
        using var image = ConvertToBgr(request.Image);
        var backendRegions = _backend.Recognize(
            image,
            language,
            request.EnableRotation,
            cancellationToken);

        var regions = backendRegions
            .Where(region => !string.IsNullOrWhiteSpace(region.Text))
            .Select(MapRegion)
            .ToArray();

        _logger?.LogDebug(
            "OCR ({Language}) recognized {RegionCount} regions.",
            language.Language.DisplayName,
            regions.Length);
        return ValueTask.FromResult(new OcrRecognitionResult(regions));
    }

    public void Dispose() => _backend.Dispose();

    private WindowsOcrLanguageSelection ResolveLanguage(OcrLanguage? requestedLanguage)
    {
        var requested = requestedLanguage ?? OcrLanguages.ChineseSimplified;
        if (!string.Equals(requested.Id, OcrLanguages.Auto.Id, StringComparison.Ordinal))
            return WindowsOcrLanguageCatalog.Resolve(requested);

        if (_backend.IsModelAvailable(WindowsOcrLanguageCatalog.ChineseSimplified))
            return WindowsOcrLanguageCatalog.ChineseSimplified;
        if (_backend.IsModelAvailable(WindowsOcrLanguageCatalog.English))
            return WindowsOcrLanguageCatalog.English;

        return WindowsOcrLanguageCatalog.ChineseSimplified;
    }

    private static Mat ConvertToBgr(ImageFrame frame)
    {
        var pixels = frame.Pixels.ToArray();
        using var bgra = Mat.FromPixelData(
            frame.Height,
            frame.Width,
            MatType.CV_8UC4,
            pixels,
            frame.Stride);
        var bgr = new Mat();
        Cv2.CvtColor(bgra, bgr, ColorConversionCodes.BGRA2BGR);
        GC.KeepAlive(pixels);
        return bgr;
    }

    private static OcrTextRegion MapRegion(WindowsOcrBackendRegion region)
    {
        var polygon = region.Polygon
            .Select(point => new ImagePoint(point.X, point.Y))
            .ToArray();

        return new OcrTextRegion(
            region.Text.Trim(),
            polygon,
            CalculateTextAngle(region.Polygon, region.FallbackAngle),
            region.Confidence);
    }

    internal static double CalculateTextAngle(
        IReadOnlyList<WindowsOcrPoint> polygon,
        double fallback = 0)
    {
        if (polygon.Count < 2)
            return NormalizeAngle(fallback);

        var longestLengthSquared = 0d;
        var angle = fallback;
        for (var index = 0; index < polygon.Count; index++)
        {
            var start = polygon[index];
            var end = polygon[(index + 1) % polygon.Count];
            var dx = end.X - start.X;
            var dy = end.Y - start.Y;
            var lengthSquared = dx * dx + dy * dy;
            if (lengthSquared <= longestLengthSquared)
                continue;

            longestLengthSquared = lengthSquared;
            angle = Math.Atan2(dy, dx) * 180d / Math.PI;
        }

        angle = NormalizeAngle(angle);
        return Math.Abs(angle) < 2 ? 0 : angle;
    }

    private static double NormalizeAngle(double angle)
    {
        while (angle > 90) angle -= 180;
        while (angle <= -90) angle += 180;
        return angle;
    }
}

internal enum WindowsOcrModel
{
    ChineseSimplified,
    ChineseTraditional,
    English,
    Japanese,
    Korean,
    Arabic,
    Devanagari,
    Tamil,
    Telugu,
    Kannada,
    ChineseV4Fallback
}

internal sealed record WindowsOcrLanguageSelection(OcrLanguage Language, WindowsOcrModel Model);

internal static class WindowsOcrLanguageCatalog
{
    internal static readonly WindowsOcrLanguageSelection ChineseSimplified =
        new(OcrLanguages.ChineseSimplified, WindowsOcrModel.ChineseSimplified);

    internal static readonly WindowsOcrLanguageSelection English =
        new(OcrLanguages.English, WindowsOcrModel.English);

    private static readonly IReadOnlyDictionary<string, WindowsOcrModel> ModelsByLanguageId =
        new Dictionary<string, WindowsOcrModel>(StringComparer.Ordinal)
        {
            [OcrLanguages.ChineseSimplified.Id] = WindowsOcrModel.ChineseSimplified,
            [OcrLanguages.ChineseTraditional.Id] = WindowsOcrModel.ChineseTraditional,
            [OcrLanguages.English.Id] = WindowsOcrModel.English,
            [OcrLanguages.Japanese.Id] = WindowsOcrModel.Japanese,
            [OcrLanguages.Korean.Id] = WindowsOcrModel.Korean,
            [OcrLanguages.Arabic.Id] = WindowsOcrModel.Arabic,
            [OcrLanguages.Devanagari.Id] = WindowsOcrModel.Devanagari,
            [OcrLanguages.Tamil.Id] = WindowsOcrModel.Tamil,
            [OcrLanguages.Telugu.Id] = WindowsOcrModel.Telugu,
            [OcrLanguages.Kannada.Id] = WindowsOcrModel.Kannada
        };

    internal static WindowsOcrLanguageSelection Resolve(OcrLanguage language)
    {
        ArgumentNullException.ThrowIfNull(language);
        return new WindowsOcrLanguageSelection(
            language,
            ModelsByLanguageId.TryGetValue(language.Id, out var model)
                ? model
                : WindowsOcrModel.ChineseV4Fallback);
    }
}

internal sealed record WindowsOcrPoint(double X, double Y);

internal sealed record WindowsOcrBackendRegion(
    string Text,
    IReadOnlyList<WindowsOcrPoint> Polygon,
    double FallbackAngle,
    double Confidence = 1d);

internal interface IWindowsOcrBackend : IDisposable
{
    bool CanDeleteModels { get; }

    bool IsModelAvailable(WindowsOcrLanguageSelection language);

    Task DownloadModelAsync(
        WindowsOcrLanguageSelection language,
        OcrModelDownloadOptions options,
        IProgress<double>? progress,
        CancellationToken cancellationToken);

    void DeleteModel(WindowsOcrLanguageSelection language);

    IReadOnlyList<WindowsOcrBackendRegion> Recognize(
        Mat image,
        WindowsOcrLanguageSelection language,
        bool enableRotation,
        CancellationToken cancellationToken);
}
