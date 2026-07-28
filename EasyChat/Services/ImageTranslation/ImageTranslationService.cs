using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using EasyChat.Models.Ocr;
using EasyChat.Models.Translation;
using EasyChat.Constants;
using EasyChat.Services.Abstractions;
using EasyChat.Services.Languages;
using EasyChat.Services.Translation;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using AvaloniaRect = Avalonia.Rect;

namespace EasyChat.Services.ImageTranslation;

public sealed class ImageTranslationService : IImageTranslationService
{
    private const double MinimumFontSize = 1;
    private readonly ITranslationServiceFactory _translationFactory;
    private readonly IConfigurationService _configurationService;
    private readonly ILogger<ImageTranslationService> _logger;

    public ImageTranslationService(
        ITranslationServiceFactory translationFactory,
        IConfigurationService configurationService,
        ILogger<ImageTranslationService> logger)
    {
        _translationFactory = translationFactory;
        _configurationService = configurationService;
        _logger = logger;
    }

    public async Task<ImageTranslationResult> TranslateAsync(
        Bitmap bitmap,
        OcrRecognitionResult recognition,
        LanguageDefinition? source,
        LanguageDefinition? target,
        CancellationToken cancellationToken = default)
    {
        var blocks = CreateRegionBlocks(recognition.Regions);
        if (blocks.Count == 0)
            return new ImageTranslationResult(bitmap, ["No text detected."], 0, 0);

        var translated = new List<TranslatedBlock>();
        var warnings = new List<string>();
        if (string.Equals(
                _configurationService.General?.TransEngine,
                Constant.TransEngineType.Ai,
                StringComparison.OrdinalIgnoreCase))
        {
            await TranslateAiAsync(blocks, translated, warnings, source, target, cancellationToken);
        }
        else
        {
            await TranslateMachineBlocksAsync(blocks, translated, warnings, source, target, cancellationToken);
        }

        if (translated.Count == 0)
            return new ImageTranslationResult(bitmap, warnings, blocks.Count, 0);

        var renderable = translated
            .Where(item => CanFitText(item.Translation, item.Block, bitmap))
            .ToList();

        foreach (var item in translated.Except(renderable))
            warnings.Add($"Translation did not fit: {item.Block.SourceText}");

        if (renderable.Count == 0)
            return new ImageTranslationResult(bitmap, warnings, blocks.Count, 0);

        var output = Render(bitmap, renderable, warnings);
        return new ImageTranslationResult(output, warnings, blocks.Count, renderable.Count);
    }

    public static IReadOnlyList<TextBlock> GroupRegions(IReadOnlyList<OcrTextRegion> regions)
    {
        var ordered = regions
            .Where(region => !string.IsNullOrWhiteSpace(region.Text) && region.Polygon.Count >= 3)
            .OrderBy(region => region.Bounds.Top)
            .ThenBy(region => region.Bounds.Left)
            .ToList();
        var lines = new List<List<OcrTextRegion>>();

        foreach (var region in ordered)
        {
            var height = Math.Max(1, region.Bounds.Height);
            var line = lines.FirstOrDefault(candidate =>
            {
                var bounds = GetBounds(candidate);
                var overlap = Math.Min(bounds.Bottom, region.Bounds.Bottom) - Math.Max(bounds.Top, region.Bounds.Top);
                var horizontalGap = Math.Max(bounds.Left - region.Bounds.Right, region.Bounds.Left - bounds.Right);
                var maxGap = Math.Max(80, Math.Min(bounds.Height, height) * 12);
                var angle = Math.Abs(NormalizeAngle(GetAngle(candidate) - region.Angle));
                return overlap >= Math.Min(bounds.Height, height) * 0.25
                    && horizontalGap <= maxGap
                    && angle <= 18;
            });

            if (line == null)
                lines.Add([region]);
            else
                line.Add(region);
        }

        return lines
            .Select(line =>
            {
                var orderedLine = line.OrderBy(region => region.Bounds.Left).ToArray();
                return new TextBlock(
                    JoinRegions(orderedLine),
                    orderedLine,
                    GetBounds(orderedLine),
                    GetAngle(orderedLine),
                    GetBounds(orderedLine).Center,
                    GetBounds(orderedLine).Width,
                    GetBounds(orderedLine).Height);
            })
            .OrderBy(block => block.Bounds.Top)
            .ThenBy(block => block.Bounds.Left)
            .ToArray();
    }

    public static IReadOnlyList<TextBlock> CreateRegionBlocks(IReadOnlyList<OcrTextRegion> regions)
        => regions
            .Where(region => !string.IsNullOrWhiteSpace(region.Text) && region.Polygon.Count >= 3)
            .OrderBy(region => region.Bounds.Top)
            .ThenBy(region => region.Bounds.Left)
            .Select(region => new TextBlock(
                region.Text.Trim(),
                [region],
                region.Bounds,
                region.Angle,
                region.Center,
                region.OrientedSize.Width,
                region.OrientedSize.Height))
            .ToArray();

    private static string JoinRegions(IReadOnlyList<OcrTextRegion> regions)
    {
        var builder = new StringBuilder();
        foreach (var region in regions)
        {
            var text = region.Text.Trim();
            if (text.Length == 0)
                continue;

            if (builder.Length > 0 && !IsCjk(builder[^1]) && !IsCjk(text[0]))
                builder.Append(' ');
            builder.Append(text);
        }

        return builder.ToString();
    }

    private static bool IsCjk(char character)
        => character is >= '\u3040' and <= '\u30ff'
            or >= '\u3400' and <= '\u4dbf'
            or >= '\u4e00' and <= '\u9fff'
            or >= '\uac00' and <= '\ud7af';

    private static AvaloniaRect GetBounds(IEnumerable<OcrTextRegion> regions)
    {
        var bounds = regions.Select(region => region.Bounds).ToArray();
        if (bounds.Length == 0)
            return new AvaloniaRect();

        var left = bounds.Min(rect => rect.Left);
        var top = bounds.Min(rect => rect.Top);
        var right = bounds.Max(rect => rect.Right);
        var bottom = bounds.Max(rect => rect.Bottom);
        return new AvaloniaRect(left, top, right - left, bottom - top);
    }

    private static double GetAngle(IEnumerable<OcrTextRegion> regions)
    {
        var angles = regions.Select(region => region.Angle * Math.PI / 90d).ToArray();
        if (angles.Length == 0)
            return 0;

        var sin = angles.Average(Math.Sin);
        var cos = angles.Average(Math.Cos);
        return Math.Atan2(sin, cos) * 90d / Math.PI;
    }

    private static double NormalizeAngle(double angle)
    {
        while (angle > 180) angle -= 360;
        while (angle < -180) angle += 360;
        return angle;
    }

    private async Task TranslateAiAsync(
        IReadOnlyList<TextBlock> blocks,
        List<TranslatedBlock> translated,
        List<string> warnings,
        LanguageDefinition? source,
        LanguageDefinition? target,
        CancellationToken cancellationToken)
    {
        var requests = blocks
            .Select((block, index) => new BatchTranslationRequest($"block-{index}", block.SourceText))
            .ToArray();
        IReadOnlyDictionary<string, string> translations = new Dictionary<string, string>();

        try
        {
            var translator = CreateAiBatchTranslator();
            if (translator is not IIdentifiedTranslationStream identifiedTranslator)
                throw new InvalidOperationException("The configured AI translator does not support identified streams.");

            cancellationToken.ThrowIfCancellationRequested();
            var payload = JsonSerializer.Serialize(new BatchTranslationPayload(
                requests,
                requests.Select(item => item.Id).ToArray()));
            var requestedIds = requests.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
            var translationBuilders = new Dictionary<string, StringBuilder>(StringComparer.Ordinal);
            await foreach (var item in identifiedTranslator.StreamIdentifiedTranslationsAsync(
                               payload, source, target, cancellationToken))
            {
                if (item is not IdentifiedTranslationDeltaEvent delta
                    || !requestedIds.Contains(delta.Id)
                    || string.IsNullOrEmpty(delta.Text))
                {
                    continue;
                }

                if (!translationBuilders.TryGetValue(delta.Id, out var builder))
                {
                    builder = new StringBuilder();
                    translationBuilders.Add(delta.Id, builder);
                }

                builder.Append(delta.Text);
            }

            translations = translationBuilders.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.ToString(),
                StringComparer.Ordinal);
            if (translations.Count == 0)
                throw new JsonException("AI image translation did not return identified translation events.");

            if (translations.Count < requests.Length)
            {
                _logger.LogWarning(
                    "Image AI translation returned {TranslationCount} of {BlockCount} blocks",
                    translations.Count,
                    requests.Length);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Image AI translation failed for {BlockCount} blocks",
                blocks.Count);
        }

        for (var index = 0; index < requests.Length; index++)
        {
            var request = requests[index];
            if (translations.TryGetValue(request.Id, out var text) && !string.IsNullOrWhiteSpace(text))
                translated.Add(new TranslatedBlock(blocks[index], text.Trim()));
            else
                warnings.Add($"Unable to translate: {blocks[index].SourceText}");
        }
    }

    private ITranslation CreateAiBatchTranslator()
    {
        var general = _configurationService.General
            ?? throw new InvalidOperationException("General translation configuration is unavailable.");
        return !string.IsNullOrWhiteSpace(general.UsingAiModelId)
            ? _translationFactory.CreateAiServiceById(general.UsingAiModelId, ImageBatchPrompt)
            : _translationFactory.CreateAiService(general.UsingAiModel ?? "OpenAI", ImageBatchPrompt);
    }

    private async Task TranslateMachineBlocksAsync(
        IReadOnlyList<TextBlock> blocks,
        List<TranslatedBlock> translated,
        List<string> warnings,
        LanguageDefinition? source,
        LanguageDefinition? target,
        CancellationToken cancellationToken)
    {
        var translator = _translationFactory.CreateCurrentService();
        foreach (var block in blocks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var text = await translator.TranslateAsync(
                    block.SourceText,
                    source,
                    target,
                    cancellationToken: cancellationToken);
                if (string.IsNullOrWhiteSpace(text))
                    warnings.Add($"Unable to translate: {block.SourceText}");
                else
                    translated.Add(new TranslatedBlock(block, text.Trim()));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Image text block translation failed: {Text}", block.SourceText);
                warnings.Add($"Unable to translate: {block.SourceText}");
            }
        }
    }

    public static IReadOnlyDictionary<string, string> ParseBatchTranslations(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
            return new Dictionary<string, string>();

        var value = response.Trim();
        var translations = new Dictionary<string, string>(StringComparer.Ordinal);
        var runtimeText = new StringBuilder();

        var arrayStart = value.IndexOf('[');
        var arrayEnd = value.LastIndexOf(']');
        if (arrayStart >= 0 && arrayEnd > arrayStart)
        {
            TryReadTranslationJson(value[arrayStart..(arrayEnd + 1)], translations, runtimeText);
            if (translations.Count > 0)
                return translations;
        }

        TryReadTranslationJson(value, translations, runtimeText);
        if (translations.Count > 0)
            return translations;

        foreach (var line in value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = line.Trim().TrimStart(',').TrimEnd(',');
            if (candidate.StartsWith('[')) candidate = candidate[1..].TrimStart();
            if (candidate.EndsWith(']')) candidate = candidate[..^1].TrimEnd();
            TryReadTranslationJson(candidate, translations, runtimeText);
        }

        if (translations.Count > 0)
            return translations;

        if (runtimeText.Length > 0 && !string.Equals(runtimeText.ToString(), value, StringComparison.Ordinal))
            return ParseBatchTranslations(runtimeText.ToString());

        throw new JsonException("AI image translation did not return recognizable structured results.");
    }

    private static void TryReadTranslationJson(
        string json,
        Dictionary<string, string> translations,
        StringBuilder runtimeText)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            ReadTranslationElement(document.RootElement, translations, runtimeText);
        }
        catch (JsonException)
        {
            // The caller tries additional supported shapes and individual JSON lines.
        }
    }

    private static void ReadTranslationElement(
        JsonElement element,
        Dictionary<string, string> translations,
        StringBuilder runtimeText)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                ReadTranslationElement(item, translations, runtimeText);
            return;
        }

        if (element.ValueKind != JsonValueKind.Object)
            return;

        var id = GetStringProperty(element, "id");
        var translation = GetStringProperty(element, "translation");
        if (!string.IsNullOrWhiteSpace(id) && translation != null)
            translations.TryAdd(id, translation);

        foreach (var propertyName in new[] { "translations", "results", "items" })
        {
            if (TryGetProperty(element, propertyName, out var nested))
                ReadTranslationElement(nested, translations, runtimeText);
        }

        var eventName = GetStringProperty(element, "event");
        if (string.Equals(eventName, "translation_delta", StringComparison.OrdinalIgnoreCase))
        {
            var text = GetStringProperty(element, "text");
            if (!string.IsNullOrEmpty(text))
                runtimeText.Append(text);
        }
    }

    private static string? GetStringProperty(JsonElement element, string name)
        => TryGetProperty(element, name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool CanFitText(string text, TextBlock block, Bitmap bitmap)
    {
        var scaleX = bitmap.Size.Width / Math.Max(1, bitmap.PixelSize.Width);
        var scaleY = bitmap.Size.Height / Math.Max(1, bitmap.PixelSize.Height);
        var boxWidth = block.BoxWidth * scaleX;
        var boxHeight = block.BoxHeight * scaleY;
        var originalBounds = new AvaloniaRect(0, 0, boxWidth, boxHeight);
        var preferredFontSize = CalculatePreferredFontSize(originalBounds, block.Angle);
        return CreateLayout(
            text,
            boxWidth,
            boxHeight,
            preferredFontSize,
            Brushes.Black) != null;
    }

    private static Bitmap Render(Bitmap source, IReadOnlyList<TranslatedBlock> blocks, List<string> warnings)
    {
        using var mat = BitmapToMat(source);
        using var mask = new Mat(mat.Size(), MatType.CV_8UC1, Scalar.All(0));
        // OCR and OpenCV work in physical pixels, while Avalonia drawing uses DIPs.
        // Convert pixel coordinates to DIPs before placing the translated text.
        var pixelToDip = PixelToDipScale(source.PixelSize, source.Size);

        foreach (var block in blocks)
        {
            foreach (var region in block.Block.Regions)
            {
                var polygon = region.Polygon
                    .Select(point => new OpenCvSharp.Point(
                        (int)Math.Round(point.X),
                        (int)Math.Round(point.Y)))
                    .ToArray();
                if (polygon.Length >= 3)
                    Cv2.FillPoly(mask, [polygon], Scalar.All(255));
            }
        }

        var medianHeight = blocks.Select(block => block.Block.Bounds.Height).OrderBy(value => value).ElementAt(blocks.Count / 2);
        var kernelSize = Math.Max(3, (int)Math.Round(medianHeight / 12) * 2 + 1);
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new OpenCvSharp.Size(kernelSize, kernelSize));
        Cv2.Dilate(mask, mask, kernel);
        using var inpainted = new Mat();
        Cv2.Inpaint(mat, mask, inpainted, Math.Max(3, medianHeight / 10), InpaintMethod.Telea);
        using var background = MatToBitmap(inpainted);

        var output = new RenderTargetBitmap(source.PixelSize, source.Dpi);
        using (var context = output.CreateDrawingContext())
        {
            context.DrawImage(background, new AvaloniaRect(source.Size));
            foreach (var block in blocks)
            {
                var boxWidth = block.Block.BoxWidth * pixelToDip.X;
                var boxHeight = block.Block.BoxHeight * pixelToDip.Y;
                var originalBounds = new AvaloniaRect(0, 0, boxWidth, boxHeight);
                var brightness = SampleBrightness(inpainted, block.Block.Bounds);
                var brush = brightness < 135 ? Brushes.White : Brushes.Black;
                var preferredFontSize = CalculatePreferredFontSize(originalBounds, block.Block.Angle);
                var layout = CreateLayout(
                    block.Translation,
                    boxWidth,
                    boxHeight,
                    preferredFontSize,
                    brush);
                if (layout == null)
                {
                    warnings.Add($"Translation did not fit: {block.Block.SourceText}");
                    continue;
                }

                var center = new Avalonia.Point(
                    block.Block.Center.X * pixelToDip.X,
                    block.Block.Center.Y * pixelToDip.Y);
                var matrix = Matrix.CreateRotation(block.Block.Angle * Math.PI / 180d)
                    * Matrix.CreateTranslation(center.X, center.Y);
                using (context.PushTransform(matrix))
                {
                    var y = -layout.Height / 2;
                    foreach (var line in layout.Lines)
                    {
                        context.DrawText(line, new Avalonia.Point(-boxWidth / 2, y));
                        y += line.Height;
                    }
                }
            }
        }

        return output;
    }

    public static Vector PixelToDipScale(PixelSize pixelSize, Avalonia.Size dipSize)
        => new(
            dipSize.Width / Math.Max(1, pixelSize.Width),
            dipSize.Height / Math.Max(1, pixelSize.Height));

    public static double CalculatePreferredFontSize(AvaloniaRect originalBounds, double angle)
    {
        var normalizedAngle = Math.Abs(NormalizeAngle(angle));
        var textHeight = normalizedAngle > 45 ? originalBounds.Width : originalBounds.Height;
        return Math.Max(MinimumFontSize, textHeight * 0.72);
    }

    public static bool IsLayoutWithinBox(double layoutWidth, double layoutHeight, double boxWidth, double boxHeight)
        => layoutWidth <= boxWidth && layoutHeight <= boxHeight;

    private static TextLayout? CreateLayout(
        string text,
        double width,
        double height,
        double preferredFontSize,
        IBrush brush)
    {
        if (width <= 1 || height <= 1)
            return null;

        var fontSize = Math.Max(MinimumFontSize, preferredFontSize);
        while (fontSize >= MinimumFontSize)
        {
            var lines = WrapText(text, width, fontSize, brush);
            var totalHeight = lines.Sum(line => line.Height);
            var totalWidth = lines.Count > 0 ? lines.Max(line => line.Width) : 0;
            if (lines.Count > 0 && IsLayoutWithinBox(totalWidth, totalHeight, width, height))
                return new TextLayout(lines, totalWidth, totalHeight);

            fontSize -= Math.Max(1, fontSize * 0.08);
        }

        return null;
    }

    private static IReadOnlyList<FormattedText> WrapText(string text, double maxWidth, double fontSize, IBrush brush)
    {
        var lines = new List<FormattedText>();
        foreach (var sourceLine in text.Replace("\r", string.Empty).Split('\n'))
        {
            var current = new StringBuilder();
            foreach (var character in sourceLine)
            {
                var candidate = current.ToString() + character;
                var measured = Measure(candidate, fontSize, brush);
                if (current.Length > 0 && measured.Width > maxWidth)
                {
                    lines.Add(Measure(current.ToString(), fontSize, brush));
                    current.Clear();
                }
                current.Append(character);
            }

            if (current.Length > 0)
                lines.Add(Measure(current.ToString(), fontSize, brush));
        }

        return lines;
    }

    private static FormattedText Measure(string text, double fontSize, IBrush brush)
        => new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), fontSize, brush);

    private static double SampleBrightness(Mat mat, AvaloniaRect bounds)
    {
        var rect = new OpenCvSharp.Rect(
            Math.Max(0, (int)bounds.Left),
            Math.Max(0, (int)bounds.Top),
            Math.Max(1, Math.Min(mat.Width - Math.Max(0, (int)bounds.Left), (int)bounds.Width)),
            Math.Max(1, Math.Min(mat.Height - Math.Max(0, (int)bounds.Top), (int)bounds.Height)));
        if (rect.Width <= 0 || rect.Height <= 0)
            return 128;

        using var roi = new Mat(mat, rect);
        var mean = Cv2.Mean(roi);
        return 0.299 * mean.Val2 + 0.587 * mean.Val1 + 0.114 * mean.Val0;
    }

    private static Mat BitmapToMat(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        stream.Position = 0;
        return Mat.FromStream(stream, ImreadModes.Color);
    }

    private static Bitmap MatToBitmap(Mat mat)
    {
        using var stream = new MemoryStream();
        mat.WriteToStream(stream, ".png");
        stream.Position = 0;
        return new Bitmap(stream);
    }

    public sealed record TextBlock(
        string SourceText,
        IReadOnlyList<OcrTextRegion> Regions,
        AvaloniaRect Bounds,
        double Angle,
        Avalonia.Point Center,
        double BoxWidth,
        double BoxHeight);
    private sealed record TranslatedBlock(TextBlock Block, string Translation);
    private sealed record TextLayout(IReadOnlyList<FormattedText> Lines, double Width, double Height);
    private sealed record BatchTranslationRequest(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("text")] string Text);

    private sealed record BatchTranslationPayload(
        [property: JsonPropertyName("items")] IReadOnlyList<BatchTranslationRequest> Items,
        [property: JsonPropertyName("translate_ids")] IReadOnlyList<string> TranslateIds);

    private const string ImageBatchPrompt = """
You translate all OCR text blocks from one image together so that shared visual context,
terminology, labels, and sentence fragments remain consistent.

The user input is a JSON object with this exact shape:
{"items":[{"id":"block-0","text":"source text"}],"translate_ids":["block-0"]}

Use every object in `items` as shared image context. Translate only the objects whose IDs
appear in `translate_ids` from [SourceLang] to [TargetLang]. The runtime's identified JSONL
contract defines the response format. Emit one outer `translation_delta` event for every
requested block, with the original ID in `id` and its translated replacement text in `text`.

Rules:
- Preserve every requested `id` exactly and return exactly one result for every `translate_ids` entry.
- Keep the output items in the same order as `translate_ids`.
- Translate only the `text` values. Never translate or alter an `id`.
- Use the complete `items` collection as context, but do not return unrequested IDs.
- Do not merge, split, omit, or invent requested items.
- Each `translation` must contain only the target-language replacement text for that block.
- Do not translate the input JSON as prose. It is control data describing separate OCR blocks.
- Do not nest JSON inside `text` and do not emit Markdown or explanations.
""";
}
