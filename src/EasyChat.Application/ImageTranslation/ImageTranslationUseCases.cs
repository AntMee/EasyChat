using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EasyChat.Contracts.ImageTranslation;
using EasyChat.Contracts.Ocr;
using EasyChat.Contracts.Settings;
using EasyChat.Contracts.Translation;

namespace EasyChat.Application.ImageTranslation;

public sealed class ImageTranslationUseCases : IImageTranslationUseCases
{
    private readonly ITranslationUseCases _translation;
    private readonly ISettingsUseCases _settings;
    private readonly IImageTranslationRenderer _renderer;

    public ImageTranslationUseCases(
        ITranslationUseCases translation,
        ISettingsUseCases settings,
        IImageTranslationRenderer renderer)
    {
        _translation = translation ?? throw new ArgumentNullException(nameof(translation));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
    }

    public async Task<ImageTranslationResult> TranslateAsync(
        ImageTranslationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var blocks = CreateIndexedBlocks(request.Recognition.Regions);
        if (blocks.Count == 0)
            return new ImageTranslationResult(request.Image, ["No text detected."], 0, 0);

        var translated = await TranslateRegionsAsync(
            new ImageRegionTranslationRequest(
                request.Recognition,
                blocks.Select(block => block.Index).ToArray(),
                request.SourceLanguage,
                request.TargetLanguage),
            cancellationToken);
        var warnings = translated.Warnings.ToList();
        var overlays = translated.Translations
            .Select(item => new ImageTranslationOverlay(
                item.RenderRegion ?? request.Recognition.Regions[item.RegionIndex],
                item.Translation,
                item.EraseRegions))
            .ToArray();

        if (overlays.Length == 0)
            return new ImageTranslationResult(request.Image, warnings, blocks.Count, 0);

        var rendered = await _renderer.RenderAsync(
            request.Image,
            overlays,
            new ImageTranslationRenderOptions(_settings.Current.Screenshot.ImageTextEraseMode),
            cancellationToken);
        warnings.AddRange(rendered.Warnings);
        return new ImageTranslationResult(
            rendered.Image,
            warnings,
            blocks.Count,
            rendered.RenderedBlockCount);
    }

    public async Task<ImageRegionTranslationResult> TranslateRegionsAsync(
        ImageRegionTranslationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Recognition);
        ArgumentNullException.ThrowIfNull(request.RegionIndexes);
        ArgumentNullException.ThrowIfNull(request.TargetLanguage);
        cancellationToken.ThrowIfCancellationRequested();

        var indexes = request.RegionIndexes
            .Distinct()
            .ToHashSet();
        if (indexes.Any(index => index < 0 || index >= request.Recognition.Regions.Count))
            throw new ArgumentOutOfRangeException(nameof(request), "A selected OCR region index is invalid.");

        var allBlocks = CreateIndexedBlocks(request.Recognition.Regions);
        var selected = allBlocks.Where(block => indexes.Contains(block.Index)).ToArray();
        if (selected.Length == 0)
            return new ImageRegionTranslationResult([], ["No selected text could be translated."]);

        // Translating the complete image keeps the original per-region rendering behavior.
        // Only an explicit partial multi-selection is treated as one or more text paragraphs.
        var combineSelectedBlocks = selected.Length > 1 && selected.Length < allBlocks.Count;

        var warnings = new List<string>();
        var translations = string.Equals(
                _settings.Current.General.TranslationEngine,
                TranslationEngineNames.AiModel,
                StringComparison.OrdinalIgnoreCase)
            ? await TranslateWithAiAsync(
                selected,
                combineSelectedBlocks,
                request.SourceLanguage,
                request.TargetLanguage,
                warnings,
                cancellationToken)
            : await TranslateWithMachineProviderAsync(
                selected,
                combineSelectedBlocks,
                request.SourceLanguage,
                request.TargetLanguage,
                warnings,
                cancellationToken);
        return new ImageRegionTranslationResult(translations, warnings);
    }

    internal static IReadOnlyList<OcrTextRegion> CreateBlocks(
        IReadOnlyList<OcrTextRegion> regions) =>
        CreateIndexedBlocks(regions)
            .Select(block => block.Region)
            .ToArray();

    private static IReadOnlyList<IndexedBlock> CreateIndexedBlocks(
        IReadOnlyList<OcrTextRegion> regions) =>
        regions
            .Select((region, index) => new IndexedBlock(index, region))
            .Where(block => !string.IsNullOrWhiteSpace(block.Region.Text)
                            && block.Region.Polygon.Count >= 3)
            .OrderBy(block => block.Region.Polygon.Min(point => point.Y))
            .ThenBy(block => block.Region.Polygon.Min(point => point.X))
            .ToArray();

    private async Task<IReadOnlyList<ImageRegionTranslation>> TranslateWithAiAsync(
        IReadOnlyList<IndexedBlock> selectedBlocks,
        bool combineSelectedBlocks,
        TranslationLanguage? sourceLanguage,
        TranslationLanguage targetLanguage,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var groups = CreateTranslationGroups(selectedBlocks, combineSelectedBlocks);
        var items = groups
            .Select(group => new BatchTranslationItem(
                group.Id,
                JoinSourceText(group.Blocks)))
            .ToArray();
        var settings = _settings.Current.General;
        var provider = !string.IsNullOrWhiteSpace(settings.AiModelId)
            ? new TranslationProviderSelection(
                TranslationEngineNames.AiModel,
                AiModelId: settings.AiModelId,
                PromptOverride: ImageBatchPrompt)
            : new TranslationProviderSelection(
                TranslationEngineNames.AiModel,
                AiModelName: settings.AiModel ?? "OpenAI",
                PromptOverride: ImageBatchPrompt);

        var translations = new Dictionary<string, StringBuilder>(StringComparer.Ordinal);
        try
        {
            var session = _translation.Prepare(provider);
            using var disposable = session as IDisposable;
            if (!session.SupportsIdentifiedStreaming)
                throw new InvalidOperationException(
                    "The configured AI translator does not support identified streams.");

            var payload = JsonSerializer.Serialize(new BatchTranslationPayload(
                items,
                groups.Select(group => group.Id).ToArray()));
            var requestedIds = groups
                .Select(group => group.Id)
                .ToHashSet(StringComparer.Ordinal);
            await foreach (var delta in session.StreamIdentifiedAsync(
                               new TranslationRequest(
                                   payload,
                                   sourceLanguage,
                                   targetLanguage),
                               cancellationToken))
            {
                if (!requestedIds.Contains(delta.Id) || string.IsNullOrEmpty(delta.Text))
                    continue;

                if (!translations.TryGetValue(delta.Id, out var text))
                {
                    text = new StringBuilder();
                    translations.Add(delta.Id, text);
                }

                text.Append(delta.Text);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
        }

        var missing = groups
            .Where(group => !translations.TryGetValue(group.Id, out var value)
                            || string.IsNullOrWhiteSpace(value.ToString()))
            .ToArray();
        if (missing.Length > 0)
        {
            var fallback = _translation.Prepare();
            using var fallbackDisposable = fallback as IDisposable;
            foreach (var group in missing)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var response = await fallback.TranslateAsync(
                        new TranslationRequest(
                            JoinSourceText(group.Blocks),
                            sourceLanguage,
                            targetLanguage),
                        cancellationToken).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(response.Text))
                    {
                        translations[group.Id] =
                            new StringBuilder(response.Text.Trim());
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                }
            }
        }

        var results = new List<ImageRegionTranslation>();
        var unmappedGroups = new List<TranslationGroup>();
        foreach (var group in groups)
        {
            if (translations.TryGetValue(group.Id, out var value)
                && ExpandGroupTranslation(group, value.ToString()) is { Count: > 0 } expanded)
            {
                results.AddRange(expanded);
            }
            else
            {
                unmappedGroups.Add(group);
            }
        }

        if (unmappedGroups.Count == 0)
            return results;

        var individualFallback = _translation.Prepare();
        using var individualFallbackDisposable = individualFallback as IDisposable;
        foreach (var block in unmappedGroups
                     .SelectMany(group => group.Blocks))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var response = await individualFallback.TranslateAsync(
                    new TranslationRequest(block.Region.Text.Trim(), sourceLanguage, targetLanguage),
                    cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(response.Text))
                    warnings.Add($"Unable to translate: {block.Region.Text.Trim()}");
                else
                    results.Add(new ImageRegionTranslation(block.Index, response.Text.Trim()));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                warnings.Add($"Unable to translate: {block.Region.Text.Trim()}");
            }
        }

        return results;
    }

    private async Task<IReadOnlyList<ImageRegionTranslation>> TranslateWithMachineProviderAsync(
        IReadOnlyList<IndexedBlock> blocks,
        bool combineSelectedBlocks,
        TranslationLanguage? sourceLanguage,
        TranslationLanguage targetLanguage,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var session = _translation.Prepare();
        using var disposable = session as IDisposable;
        var groups = CreateTranslationGroups(blocks, combineSelectedBlocks);
        var translations = new List<ImageRegionTranslation>(blocks.Count);
        foreach (var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? groupTranslation = null;
            try
            {
                var response = await session.TranslateAsync(
                    new TranslationRequest(
                        JoinSourceText(group.Blocks),
                        sourceLanguage,
                        targetLanguage),
                    cancellationToken);
                groupTranslation = response.Text;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
            }

            var expanded = ExpandGroupTranslation(group, groupTranslation);
            if (expanded.Count > 0)
            {
                translations.AddRange(expanded);
                continue;
            }

            foreach (var block in group.Blocks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var individual = await session.TranslateAsync(
                        new TranslationRequest(block.Region.Text.Trim(), sourceLanguage, targetLanguage),
                        cancellationToken);
                    if (string.IsNullOrWhiteSpace(individual.Text))
                        warnings.Add($"Unable to translate: {block.Region.Text.Trim()}");
                    else
                        translations.Add(new ImageRegionTranslation(block.Index, individual.Text.Trim()));
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    warnings.Add($"Unable to translate: {block.Region.Text.Trim()}");
                }
            }
        }

        return translations;
    }

    private static IReadOnlyList<TranslationGroup> CreateTranslationGroups(
        IReadOnlyList<IndexedBlock> blocks,
        bool combineAdjacent)
    {
        if (!combineAdjacent)
        {
            return blocks
                .Select(block => new TranslationGroup($"block-{block.Index}", [block]))
                .ToArray();
        }

        var groups = new List<TranslationGroup>();
        foreach (var block in blocks)
        {
            var groupIndex = groups.FindLastIndex(group =>
                CanJoin(group.Blocks[^1].Region, block.Region));
            if (groupIndex < 0)
            {
                groups.Add(new TranslationGroup($"block-{block.Index}", [block]));
                continue;
            }

            groups[groupIndex] = groups[groupIndex] with
            {
                Blocks = [.. groups[groupIndex].Blocks, block]
            };
        }

        return groups;
    }

    private static bool CanJoin(OcrTextRegion previous, OcrTextRegion next)
    {
        var previousBounds = GetBounds(previous);
        var nextBounds = GetBounds(next);
        var previousHeight = Math.Max(1, previousBounds.Height);
        var nextHeight = Math.Max(1, nextBounds.Height);
        var height = Math.Min(previousHeight, nextHeight);
        var verticalGap = nextBounds.Top - previousBounds.Bottom;
        if (verticalGap < -height * 0.25 || verticalGap > height * 0.6)
            return false;

        var horizontalOverlap = Math.Min(previousBounds.Right, nextBounds.Right)
                              - Math.Max(previousBounds.Left, nextBounds.Left);
        var minimumWidth = Math.Min(previousBounds.Width, nextBounds.Width);
        var heightRatio = Math.Max(previousBounds.Height, nextBounds.Height)
                        / Math.Max(1, Math.Min(previousBounds.Height, nextBounds.Height));
        if (heightRatio > 1.6)
            return false;

        var overlapRatio = horizontalOverlap / Math.Max(1, minimumWidth);
        var sameAngle = Math.Abs(NormalizeAngle(previous.Angle - next.Angle)) <= 8;
        return sameAngle && overlapRatio >= 0.5;
    }

    private static string JoinSourceText(IReadOnlyList<IndexedBlock> blocks) =>
        string.Join("\n", blocks.Select(block => block.Region.Text.Trim()));

    private static IReadOnlyList<ImageRegionTranslation> ExpandGroupTranslation(
        TranslationGroup group,
        string? translation)
    {
        if (string.IsNullOrWhiteSpace(translation))
            return [];

        return [new ImageRegionTranslation(
            group.Blocks[0].Index,
            NormalizeGroupedTranslation(translation, group.Blocks.Count > 1),
            group.Blocks.Count > 1 ? MergeRegion(group) : null,
            group.Blocks.Count > 1
                ? group.Blocks.Select(block => block.Region).ToArray()
                : null)];
    }

    private static string NormalizeGroupedTranslation(string translation, bool isParagraph)
    {
        var normalized = translation.Trim();
        if (!isParagraph)
            return normalized;

        var lines = normalized
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(" ", lines);
    }

    private static RegionBounds GetBounds(OcrTextRegion region) => new(
        region.Polygon.Min(point => point.X),
        region.Polygon.Min(point => point.Y),
        region.Polygon.Max(point => point.X),
        region.Polygon.Max(point => point.Y));

    private static OcrTextRegion MergeRegion(TranslationGroup group)
    {
        var points = group.Blocks
            .SelectMany(block => block.Region.Polygon)
            .ToArray();
        var bounds = GetBounds(new OcrTextRegion(string.Empty, points, 0));
        var angle = group.Blocks.Average(block => block.Region.Angle);
        return new OcrTextRegion(
            JoinSourceText(group.Blocks),
            [
                new ImagePoint(bounds.Left, bounds.Top),
                new ImagePoint(bounds.Right, bounds.Top),
                new ImagePoint(bounds.Right, bounds.Bottom),
                new ImagePoint(bounds.Left, bounds.Bottom)
            ],
            angle,
            group.Blocks.Min(block => block.Region.Confidence));
    }

    private static double NormalizeAngle(double angle)
    {
        while (angle > 180)
            angle -= 360;
        while (angle < -180)
            angle += 360;
        return angle;
    }

    private sealed record TranslationGroup(string Id, IReadOnlyList<IndexedBlock> Blocks);
    private readonly record struct RegionBounds(double Left, double Top, double Right, double Bottom)
    {
        public double Width => Right - Left;
        public double Height => Bottom - Top;
    }

    private sealed record IndexedBlock(int Index, OcrTextRegion Region);

    private sealed record BatchTranslationItem(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("text")] string Text);

    private sealed record BatchTranslationPayload(
        [property: JsonPropertyName("items")] IReadOnlyList<BatchTranslationItem> Items,
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
- When a requested `text` contains line breaks, treat them as visual layout hints rather
  than sentence boundaries. Translate the complete text as one coherent paragraph and
  return one continuous target-language replacement.
- Use the complete `items` collection as context, but do not return unrequested IDs.
- Do not merge, split, omit, or invent requested items. A requested item may contain
  multiple visual lines, but it still has exactly one replacement text.
- Each `translation` must contain only the target-language replacement text for that block.
- Do not translate the input JSON as prose. It is control data describing separate OCR blocks.
- Do not nest JSON inside `text` and do not emit Markdown or explanations.
""";
}
