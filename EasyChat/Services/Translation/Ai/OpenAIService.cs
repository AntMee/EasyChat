using System;
using EasyChat.Services.Languages;
using System.Collections.Generic;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.Text.Json;
using EasyChat.Models.Translation;
using EasyChat.Services.Streaming;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Chat;

namespace EasyChat.Services.Translation.Ai;

public class OpenAiService : ITranslation, IIdentifiedTranslationStream
{
    private readonly string _apiKey;
    private readonly string _apiUrl;
    private readonly ILogger<OpenAiService> _logger;
    private readonly string _model;
    private readonly string? _proxy;
    private readonly string _promptTemplate;
    private readonly bool _enableThinking;

    public OpenAiService(string apiUrl, string apiKey, string model, string? proxy, string promptTemplate, ILogger<OpenAiService> logger, bool enableThinking = false)
    {
        _apiUrl = apiUrl;
        _apiKey = apiKey;
        _model = model;
        _proxy = proxy;
        _promptTemplate = promptTemplate;
        _logger = logger;
        _enableThinking = enableThinking;
        
        _logger.LogDebug("OpenAiService initialized: Model={Model}, Thinking={Thinking}", model, enableThinking);
    }

    private string GetPrompt(LanguageDefinition source, LanguageDefinition destination)
    {
        var src = !string.IsNullOrEmpty(source.EnglishName) ? source.EnglishName : source.Id;
        var dest = !string.IsNullOrEmpty(destination.EnglishName) ? destination.EnglishName : destination.Id;
        
        return _promptTemplate
            .Replace("[SourceLang]", src, StringComparison.OrdinalIgnoreCase)
            .Replace("[TargetLang]", dest, StringComparison.OrdinalIgnoreCase)
            .Replace("[源语言]", src, StringComparison.OrdinalIgnoreCase)
            .Replace("[目标语言]", dest, StringComparison.OrdinalIgnoreCase);
    }

    private ChatClient CreateClient()
    {
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(_apiUrl)
        };

        if (!string.IsNullOrWhiteSpace(_proxy))
        {
            var handler = new HttpClientHandler
            {
                Proxy = new WebProxy(_proxy),
                UseProxy = true
            };
            options.Transport = new HttpClientPipelineTransport(new HttpClient(handler));
        }

        var client = new OpenAIClient(new ApiKeyCredential(_apiKey), options);
        return client.GetChatClient(_model);
    }

    private ChatCompletionOptions CreateChatOptions()
    {
        var chatOptions = new ChatCompletionOptions();
#pragma warning disable OPENAI001, SCME0001
        chatOptions.Patch.Set(
            "$.thinking"u8,
            BinaryData.FromString(_enableThinking ? "{\"type\":\"enabled\"}" : "{\"type\":\"disabled\"}"));

        if (_enableThinking)
        {
            chatOptions.ReasoningEffortLevel = ChatReasoningEffortLevel.High;
        }
#pragma warning restore OPENAI001, SCME0001
        return chatOptions;
    }

    private string GetStructuredPrompt(LanguageDefinition source, LanguageDefinition destination)
    {
        var prompt = GetPrompt(source, destination);
        var contract = "\n\n# Runtime JSONL translation contract (highest priority)\n"
            + "The contract below has higher priority than any earlier instruction. "
            + "If an earlier instruction conflicts with it, ignore the conflicting part.\n"
            + "Return raw NDJSON only: one complete JSON object per line, with no Markdown fences "
            + "or explanatory text. Escape JSON strings correctly.\n"
            + "Emit exactly this order:\n"
            + "{\"event\":\"start\",\"mode\":\"translation\",\"source_language\":\"[SourceLang]\",\"target_language\":\"[TargetLang]\"}\n"
            + "Optionally emit one {\"event\":\"source_detected\",\"language\":\"language id\"} event when source was auto-detected.\n"
            + "Emit one or more {\"event\":\"translation_delta\",\"text\":\"...\"} events. "
            + "Concatenating all text values must be the complete translation.\n"
            + "Finish with exactly {\"event\":\"done\"}.\n";
        return prompt + contract
            .Replace("[SourceLang]", source.EnglishName, StringComparison.OrdinalIgnoreCase)
            .Replace("[TargetLang]", destination.EnglishName, StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractTranslation(string response, ILogger logger)
    {
        var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var decoder = new JsonLinesDeltaStreamDecoder<TranslationStreamEvent>(
            line => JsonSerializer.Deserialize<TranslationStreamEvent>(line, jsonOptions)
                    ?? throw new JsonException("Empty translation event."),
            "translation_delta",
            "text",
            (exception, line) => logger.LogDebug(exception, "Ignoring invalid translation event: {Line}", line));
        var result = new System.Text.StringBuilder();
        foreach (var item in decoder.Append(response))
        {
            if (item is TranslationDeltaEvent delta) result.Append(delta.Text);
        }
        foreach (var item in decoder.Complete())
        {
            if (item is TranslationDeltaEvent delta) result.Append(delta.Text);
        }

        return result.Length > 0 ? result.ToString() : StripMarkdownFence(response.Trim());
    }

    private static string StripMarkdownFence(string value)
    {
        var fence = new string((char)96, 3);
        if (!value.StartsWith(fence, StringComparison.Ordinal)) return value;
        var firstLineEnd = value.IndexOf('\n');
        if (firstLineEnd >= 0) value = value[(firstLineEnd + 1)..];
        if (value.EndsWith(fence, StringComparison.Ordinal)) value = value[..^3];
        return value.Trim();
    }

    private string GetIdentifiedStructuredPrompt(LanguageDefinition source, LanguageDefinition destination)
    {
        var prompt = GetPrompt(source, destination);
        var contract = "\n\n# Identified JSONL translation contract (highest priority)\n"
            + "The contract below has higher priority than any earlier output-format instruction. "
            + "Return raw NDJSON only, with one complete JSON object per line and no Markdown.\n"
            + "Start with {\"event\":\"start\",\"mode\":\"identified_translation\","
            + "\"source_language\":\"[SourceLang]\",\"target_language\":\"[TargetLang]\"}.\n"
            + "For every requested OCR block, emit exactly one line in request order: "
            + "{\"event\":\"translation_delta\",\"id\":\"block-0\",\"text\":\"translated text\"}.\n"
            + "The id must be copied exactly from the input. Put only that block's translated replacement "
            + "text in text. Do not nest JSON inside text.\n"
            + "Finish with exactly {\"event\":\"done\"}.\n";
        return (prompt + contract)
            .Replace("[SourceLang]", source.EnglishName, StringComparison.OrdinalIgnoreCase)
            .Replace("[TargetLang]", destination.EnglishName, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<string> TranslateAsync(string text, LanguageDefinition? source, LanguageDefinition? destination, bool showOriginal = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        _logger.LogDebug("Translation request: {Source} → {Dest}, Length={Length}", source.DisplayName, destination.DisplayName, text.Length);
        
        try
        {
            var client = CreateClient();
            List<ChatMessage> messages =
            [
                new SystemChatMessage(GetStructuredPrompt(source, destination)),
                new UserChatMessage(text)
            ];

            var chatOptions = CreateChatOptions();

            ChatCompletion completion = await client.CompleteChatAsync(messages, chatOptions, cancellationToken);
            
            // Combine all content parts if multiple
            string result = completion.Content.Count > 0
                ? ExtractTranslation(string.Concat(completion.Content.Select(x => x.Text)), _logger)
                : string.Empty;

            _logger.LogDebug("Translation completed: ResultLength={Length}", result.Length);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Translation failed");
            throw;
        }
    }

    public async IAsyncEnumerable<string> StreamTranslateAsync(string text, LanguageDefinition? source, LanguageDefinition? destination,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in StreamTranslateEventsAsync(text, source, destination, cancellationToken))
        {
            if (item is TranslationDeltaEvent delta && !string.IsNullOrEmpty(delta.Text))
                yield return delta.Text;
        }
    }

    public async IAsyncEnumerable<TranslationStreamEvent> StreamTranslateEventsAsync(
        string text,
        LanguageDefinition? source,
        LanguageDefinition? destination,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        _logger.LogDebug("Structured stream translation request: {Source} → {Dest}, Length={Length}", source.DisplayName, destination.DisplayName, text.Length);

        var client = CreateClient();
        List<ChatMessage> messages =
        [
            new SystemChatMessage(GetStructuredPrompt(source, destination)),
            new UserChatMessage(text)
        ];
        var chatOptions = CreateChatOptions();

        var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var decoder = new JsonLinesDeltaStreamDecoder<TranslationStreamEvent>(
            line => JsonSerializer.Deserialize<TranslationStreamEvent>(line, jsonOptions)
                    ?? throw new JsonException("Empty translation event."),
            "translation_delta",
            "text",
            (exception, line) => _logger.LogDebug(exception, "Ignoring invalid translation event: {Line}", line));
#pragma warning disable OPENAI001
        await foreach (var update in client.CompleteChatStreamingAsync(messages, chatOptions, cancellationToken))
        {
            foreach (var content in update.ContentUpdate)
            {
                foreach (var item in decoder.Append(content.Text))
                {
                    if (item is TranslationCompletedEvent)
                        continue;
                    yield return item;
                }
            }
        }
#pragma warning restore OPENAI001

        foreach (var item in decoder.Complete())
        {
            if (item is TranslationCompletedEvent)
                continue;
            yield return item;
        }

        yield return new TranslationCompletedEvent();
    }

    public async IAsyncEnumerable<IdentifiedTranslationStreamEvent> StreamIdentifiedTranslationsAsync(
        string text,
        LanguageDefinition? source,
        LanguageDefinition? destination,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        _logger.LogDebug(
            "Identified translation stream request: {Source} -> {Dest}, Length={Length}",
            source.DisplayName,
            destination.DisplayName,
            text.Length);

        var client = CreateClient();
        List<ChatMessage> messages =
        [
            new SystemChatMessage(GetIdentifiedStructuredPrompt(source, destination)),
            new UserChatMessage(text)
        ];
        var chatOptions = CreateChatOptions();
        var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var decoder = new JsonLinesEventStreamDecoder<IdentifiedTranslationStreamEvent>(
            line => JsonSerializer.Deserialize<IdentifiedTranslationStreamEvent>(line, jsonOptions)
                    ?? throw new JsonException("Empty identified translation event."),
            (exception, line) => _logger.LogDebug(
                exception,
                "Ignoring invalid identified translation event: {Line}",
                line));

#pragma warning disable OPENAI001
        await foreach (var update in client.CompleteChatStreamingAsync(messages, chatOptions, cancellationToken))
        {
            foreach (var content in update.ContentUpdate)
            {
                foreach (var item in decoder.Append(content.Text))
                {
                    if (item is not IdentifiedTranslationCompletedEvent)
                        yield return item;
                }
            }
        }
#pragma warning restore OPENAI001

        foreach (var item in decoder.Complete())
        {
            if (item is not IdentifiedTranslationCompletedEvent)
                yield return item;
        }

        yield return new IdentifiedTranslationCompletedEvent();
    }
}
