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

        var blocks = CreateBlocks(request.Recognition.Regions);
        if (blocks.Count == 0)
            return new ImageTranslationResult(request.Image, ["No text detected."], 0, 0);

        var warnings = new List<string>();
        var overlays = string.Equals(
                _settings.Current.General.TranslationEngine,
                TranslationEngineNames.AiModel,
                StringComparison.OrdinalIgnoreCase)
            ? await TranslateWithAiAsync(blocks, request, warnings, cancellationToken)
            : await TranslateWithMachineProviderAsync(blocks, request, warnings, cancellationToken);

        if (overlays.Count == 0)
            return new ImageTranslationResult(request.Image, warnings, blocks.Count, 0);

        var rendered = await _renderer.RenderAsync(request.Image, overlays, cancellationToken);
        warnings.AddRange(rendered.Warnings);
        return new ImageTranslationResult(
            rendered.Image,
            warnings,
            blocks.Count,
            rendered.RenderedBlockCount);
    }

    internal static IReadOnlyList<OcrTextRegion> CreateBlocks(
        IReadOnlyList<OcrTextRegion> regions) =>
        regions
            .Where(region => !string.IsNullOrWhiteSpace(region.Text) && region.Polygon.Count >= 3)
            .OrderBy(region => region.Polygon.Min(point => point.Y))
            .ThenBy(region => region.Polygon.Min(point => point.X))
            .ToArray();

    private async Task<IReadOnlyList<ImageTranslationOverlay>> TranslateWithAiAsync(
        IReadOnlyList<OcrTextRegion> blocks,
        ImageTranslationRequest request,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var items = blocks
            .Select((block, index) => new BatchTranslationItem($"block-{index}", block.Text.Trim()))
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
                items.Select(item => item.Id).ToArray()));
            var requestedIds = items.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
            await foreach (var delta in session.StreamIdentifiedAsync(
                               new TranslationRequest(
                                   payload,
                                   request.SourceLanguage,
                                   request.TargetLanguage),
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
            translations.Clear();
        }

        return CreateOverlays(blocks, items, translations, warnings);
    }

    private async Task<IReadOnlyList<ImageTranslationOverlay>> TranslateWithMachineProviderAsync(
        IReadOnlyList<OcrTextRegion> blocks,
        ImageTranslationRequest request,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var session = _translation.Prepare();
        using var disposable = session as IDisposable;
        var overlays = new List<ImageTranslationOverlay>(blocks.Count);
        foreach (var block in blocks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var response = await session.TranslateAsync(
                    new TranslationRequest(
                        block.Text.Trim(),
                        request.SourceLanguage,
                        request.TargetLanguage),
                    cancellationToken);
                if (string.IsNullOrWhiteSpace(response.Text))
                    warnings.Add($"Unable to translate: {block.Text.Trim()}");
                else
                    overlays.Add(new ImageTranslationOverlay(block, response.Text.Trim()));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                warnings.Add($"Unable to translate: {block.Text.Trim()}");
            }
        }

        return overlays;
    }

    private static IReadOnlyList<ImageTranslationOverlay> CreateOverlays(
        IReadOnlyList<OcrTextRegion> blocks,
        IReadOnlyList<BatchTranslationItem> items,
        IReadOnlyDictionary<string, StringBuilder> translations,
        List<string> warnings)
    {
        var overlays = new List<ImageTranslationOverlay>(items.Count);
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            if (translations.TryGetValue(item.Id, out var value)
                && !string.IsNullOrWhiteSpace(value.ToString()))
            {
                overlays.Add(new ImageTranslationOverlay(blocks[index], value.ToString().Trim()));
            }
            else
            {
                warnings.Add($"Unable to translate: {blocks[index].Text.Trim()}");
            }
        }

        return overlays;
    }

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
- Use the complete `items` collection as context, but do not return unrequested IDs.
- Do not merge, split, omit, or invent requested items.
- Each `translation` must contain only the target-language replacement text for that block.
- Do not translate the input JSON as prose. It is control data describing separate OCR blocks.
- Do not nest JSON inside `text` and do not emit Markdown or explanations.
""";
}
