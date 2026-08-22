using System.Runtime.CompilerServices;
using EasyChat.Contracts.Capture;
using EasyChat.Contracts.ImageTranslation;
using EasyChat.Contracts.Ocr;
using EasyChat.Contracts.Platform;
using EasyChat.Contracts.Settings;
using EasyChat.Contracts.Translation;

namespace EasyChat.Application.Capture;

public sealed class ScreenshotUseCases(
    ISettingsUseCases settings,
    ITranslationLanguageCatalog languages,
    IOcrRecognitionUseCases ocr,
    ITranslationUseCases translation,
    IImageTranslationUseCases imageTranslation) : IScreenshotUseCases
{
    private readonly ISettingsUseCases _settings = settings;
    private readonly ITranslationLanguageCatalog _languages = languages;
    private readonly IOcrRecognitionUseCases _ocr = ocr;
    private readonly ITranslationUseCases _translation = translation;
    private readonly IImageTranslationUseCases _imageTranslation = imageTranslation;

    public OcrLanguage ResolveOcrLanguage(OcrLanguage? requestedLanguage = null)
    {
        if (requestedLanguage is not null
            && !string.Equals(requestedLanguage.Id, OcrLanguages.Auto.Id, StringComparison.OrdinalIgnoreCase)
            && OcrLanguages.TryGet(requestedLanguage.Id, out var requested))
        {
            return requested;
        }

        var global = ResolveOcrLanguage(_settings.Current.General.SourceLanguage.Id);
        return global is not null
               && !string.Equals(global.Id, OcrLanguages.Auto.Id, StringComparison.OrdinalIgnoreCase)
            ? global
            : OcrLanguages.ChineseSimplified;
    }

    public async ValueTask<OcrRecognitionResult> RecognizeAsync(
        ImageFrame image,
        bool enableRotation,
        OcrLanguage? language = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        var settings = _settings.Current;
        var request = new OcrRecognitionRequest(
            image,
            ResolveOcrLanguage(language),
            enableRotation,
            settings.Screenshot.OcrMode,
            settings.Screenshot.OcrIdleTimeoutSeconds);

        if (image.Width <= 3000 && image.Height <= 3000)
            return await _ocr.RecognizeAsync(request, cancellationToken).ConfigureAwait(false);

        var regions = new List<OcrTextRegion>();
        var splitVertically = image.Height >= image.Width;
        var dimension = splitVertically ? image.Height : image.Width;
        const int tileSize = 2048;
        const int overlap = 192;
        var step = tileSize - overlap;
        for (var offset = 0; offset < dimension; offset += step)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var length = Math.Min(tileSize, dimension - offset);
            var tile = Crop(image, splitVertically ? 0 : offset, splitVertically ? offset : 0,
                splitVertically ? image.Width : length, splitVertically ? length : image.Height);
            var tileResult = await _ocr.RecognizeAsync(request with { Image = tile }, cancellationToken)
                .ConfigureAwait(false);
            foreach (var region in tileResult.Regions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var translated = TranslateRegion(
                    region,
                    splitVertically ? 0 : offset,
                    splitVertically ? offset : 0);
                AddRegionDeduplicated(regions, translated);
            }

            if (offset + length >= dimension)
                break;
        }

        return new OcrRecognitionResult(
            regions
                .OrderBy(region => region.Polygon.Count == 0 ? double.MaxValue : region.Polygon.Min(point => point.Y))
                .ThenBy(region => region.Polygon.Count == 0 ? double.MaxValue : region.Polygon.Min(point => point.X))
                .ToArray());
    }

    public async IAsyncEnumerable<TranslationEvent> TranslateTextAsync(
        string text,
        OcrLanguage? sourceLanguage = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var general = _settings.Current.General;
        var source = sourceLanguage is null
            ? _languages.Get(general.SourceLanguage.Id)
            : TryGetTranslationLanguage(sourceLanguage.Id)
              ?? _languages.Get(general.SourceLanguage.Id);
        var request = new TranslationRequest(
            NormalizeOcrTextForTranslation(text),
            source,
            _languages.Get(general.TargetLanguage.Id));
        await foreach (var item in _translation.StreamAsync(request, cancellationToken)
                           .ConfigureAwait(false))
        {
            yield return item;
        }
    }

    public Task<ImageTranslationResult> TranslateImageAsync(
        ImageFrame image,
        OcrRecognitionResult recognition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(recognition);
        var general = _settings.Current.General;
        return _imageTranslation.TranslateAsync(
            new ImageTranslationRequest(
                image,
                recognition,
                _languages.Get(general.SourceLanguage.Id),
                _languages.Get(general.TargetLanguage.Id)),
            cancellationToken);
    }

    internal static OcrLanguage? ResolveOcrLanguage(string languageId)
    {
        return OcrLanguages.TryGet(languageId, out var language)
            ? language
            : null;
    }

    private TranslationLanguage? TryGetTranslationLanguage(string languageId)
    {
        try
        {
            return _languages.Get(languageId);
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeOcrTextForTranslation(string text)
    {
        var lines = text
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(" ", lines);
    }

    private static ImageFrame Crop(ImageFrame source, int x, int y, int width, int height)
    {
        var stride = checked(width * 4);
        var pixels = new byte[checked(stride * height)];
        var rowBytes = stride;
        for (var row = 0; row < height; row++)
        {
            source.Pixels.Span
                .Slice((y + row) * source.Stride + x * 4, rowBytes)
                .CopyTo(pixels.AsSpan(row * stride, rowBytes));
        }

        return new ImageFrame(width, height, stride, source.DpiX, source.DpiY, pixels);
    }

    private static OcrTextRegion TranslateRegion(OcrTextRegion region, int offsetX, int offsetY) =>
        region with
        {
            Polygon = region.Polygon
                .Select(point => new ImagePoint(point.X + offsetX, point.Y + offsetY))
                .ToArray()
        };

    private static void AddRegionDeduplicated(List<OcrTextRegion> regions, OcrTextRegion candidate)
    {
        if (candidate.Polygon.Count < 3 || string.IsNullOrWhiteSpace(candidate.Text))
        {
            regions.Add(candidate);
            return;
        }

        var duplicateIndex = -1;
        for (var index = 0; index < regions.Count; index++)
        {
            var existing = regions[index];
            if (existing.Polygon.Count < 3)
                continue;
            var intersection = IntersectionOverUnion(existing, candidate);
            if (intersection <= 0)
                continue;
            var similarity = TextSimilarity(existing.Text, candidate.Text);
            if (similarity >= 0.65d || intersection >= 0.45d)
            {
                duplicateIndex = index;
                break;
            }
        }

        if (duplicateIndex < 0)
        {
            regions.Add(candidate);
            return;
        }

        if (candidate.Confidence > regions[duplicateIndex].Confidence)
            regions[duplicateIndex] = candidate;
    }

    private static double IntersectionOverUnion(OcrTextRegion first, OcrTextRegion second)
    {
        var firstBounds = Bounds(first);
        var secondBounds = Bounds(second);
        var left = Math.Max(firstBounds.Left, secondBounds.Left);
        var top = Math.Max(firstBounds.Top, secondBounds.Top);
        var right = Math.Min(firstBounds.Right, secondBounds.Right);
        var bottom = Math.Min(firstBounds.Bottom, secondBounds.Bottom);
        var intersection = Math.Max(0d, right - left) * Math.Max(0d, bottom - top);
        if (intersection <= 0)
            return 0;
        var union = firstBounds.Width * firstBounds.Height + secondBounds.Width * secondBounds.Height - intersection;
        return union <= 0 ? 0 : intersection / union;
    }

    private static (double Left, double Top, double Right, double Bottom, double Width, double Height) Bounds(
        OcrTextRegion region)
    {
        var left = region.Polygon.Min(point => point.X);
        var top = region.Polygon.Min(point => point.Y);
        var right = region.Polygon.Max(point => point.X);
        var bottom = region.Polygon.Max(point => point.Y);
        return (left, top, right, bottom, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    private static double TextSimilarity(string first, string second)
    {
        var left = first.Trim();
        var right = second.Trim();
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
            return 1d;
        if (left.Length == 0 || right.Length == 0)
            return 0d;
        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (var index = 0; index <= right.Length; index++)
            previous[index] = index;
        for (var row = 1; row <= left.Length; row++)
        {
            current[0] = row;
            for (var column = 1; column <= right.Length; column++)
            {
                var cost = char.ToUpperInvariant(left[row - 1]) == char.ToUpperInvariant(right[column - 1]) ? 0 : 1;
                current[column] = Math.Min(Math.Min(current[column - 1] + 1, previous[column] + 1), previous[column - 1] + cost);
            }
            (previous, current) = (current, previous);
        }
        return 1d - previous[right.Length] / (double)Math.Max(left.Length, right.Length);
    }
}
