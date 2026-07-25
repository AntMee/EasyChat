using System;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;
using EasyChat.Models.Configuration;
using EasyChat.Models.Translation.Selection;
using EasyChat.Services.Abstractions;
using EasyChat.Services.Streaming;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Chat;

namespace EasyChat.Services.Translation.Selection;

public class AiSelectionTranslationProvider : ISelectionTranslationProvider
{
    private readonly IConfigurationService _configurationService;
    private readonly ILogger<AiSelectionTranslationProvider> _logger;

    private const string SystemPromptTemplate = """
# Role
You are a professional translator and lexicographer proficient in [SourceLang] and [TargetLang].

# Task
Source Language: [SourceLang]
Target Language: [TargetLang]
If Source Language is "Auto" or "Auto Detect", detect the input language.
All translations, meanings, tips, and grammatical labels MUST be in [TargetLang].

# Mode Selection (strict)
First trim whitespace and trailing punctuation.
- For space-based languages: no internal spaces means word mode; one or more spaces means sentence mode.
- For Chinese, Japanese, and other no-space languages: 4 characters or fewer means word mode; otherwise sentence mode.
Do not override these rules based on meaning or grammar.

# Output Protocol: JSON Lines
Return raw NDJSON only: one complete JSON object per line, no Markdown or explanatory text.
Every line must contain the `event` property shown below. Escape all JSON strings correctly.
Emit events in exactly the documented order and always finish with `{"event":"done"}`.

## Sentence mode
1. `{"event":"start","mode":"sentence"}`
2. `{"event":"source_detected","language":"en"}`
3. One or more `{"event":"translation_delta","text":"..."}` events. Split the complete translation into natural short phrases so it can be rendered while you generate it. Concatenating `text` values must be the complete fluent translation.
4. Zero to three `{"event":"keyword","word":"original term","meaning":"meaning in [TargetLang]"}` events.
5. `{"event":"done"}`

## Word mode
1. `{"event":"start","mode":"word"}`
2. `{"event":"source_detected","language":"en"}`
3. `{"event":"word_header","word":"lemma or original word","phonetic":"IPA or pronunciation"}`
4. One or more `{"event":"definition","pos":"n.","meaning":"meaning in [TargetLang]"}` events.
5. Zero or more `{"event":"form","label":"form name in [TargetLang]","word":"word form"}` events.
6. Optionally one `{"event":"tips","text":"usage tips in [TargetLang]"}` event.
7. Exactly three `{"event":"example","origin":"original sentence","translation":"translation in [TargetLang]"}` events.
8. `{"event":"done"}`
""";

    public AiSelectionTranslationProvider(
        IConfigurationService configurationService,
        ILogger<AiSelectionTranslationProvider> logger)
    {
        _configurationService = configurationService;
        _logger = logger;
    }

    private (ChatClient Client, string SourceLang, string TargetLang) CreateClientAndConfig(string sourceLangOverride, string targetLangOverride)
    {
        // 1. Resolve AI Model
        var generalConf = _configurationService.General;
        var selectionConf = _configurationService.SelectionTranslation;
        var aiConf = _configurationService.AiModel;

        if (generalConf == null || aiConf == null || selectionConf == null)
        {
            throw new InvalidOperationException("Configuration not available");
        }

        CustomAiModel? model = null;
        
        // Priority 1: Selection Translation Specific Model
        if (!string.IsNullOrEmpty(selectionConf.AiModelId))
        {
            model = aiConf.ConfiguredModels.FirstOrDefault(m => m.Id == selectionConf.AiModelId);
        }
        
        // Priority 2: General Config Model ID
        if (model == null && !string.IsNullOrEmpty(generalConf.UsingAiModelId))
        {
            model = aiConf.ConfiguredModels.FirstOrDefault(m => m.Id == generalConf.UsingAiModelId);
        }
        
        // Priority 3: General Config Legacy Name
        if (model == null && !string.IsNullOrEmpty(generalConf.UsingAiModel))
        {
            model = aiConf.ConfiguredModels.FirstOrDefault(m => m.Name == generalConf.UsingAiModel);
        }

        if (model == null)
        {
           throw new InvalidOperationException("No active AI model configured");
        }

        // 2. Create Client
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(model.ApiUrl)
        };

        // Proxy
        if (model.UseProxy && _configurationService.Proxy?.ProxyUrl is { } proxyUrl && !string.IsNullOrWhiteSpace(proxyUrl))
        {
             var handler = new HttpClientHandler
            {
                Proxy = new WebProxy(proxyUrl),
                UseProxy = true
            };
            options.Transport = new HttpClientPipelineTransport(new HttpClient(handler));
        }

        var client = new OpenAIClient(new ApiKeyCredential(model.ApiKey), options);
        var chatClient = client.GetChatClient(model.Model);
        
        return (chatClient, sourceLangOverride, targetLangOverride);
    }

    public async Task<SelectionTranslationResult> TranslateAsync(string text, string sourceLang, string targetLang, CancellationToken cancellationToken = default)
    {
        var accumulator = new SelectionTranslationResultAccumulator(text);
        await foreach (var translationEvent in StreamTranslateAsync(text, sourceLang, targetLang, cancellationToken))
        {
            accumulator.Apply(translationEvent);
        }

        return accumulator.Build();
    }

    public async IAsyncEnumerable<SelectionTranslationStreamEvent> StreamTranslateAsync(
        string text,
        string sourceLang,
        string targetLang,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Streaming selection translation: {Source} -> {Target}, Length={Length}", sourceLang, targetLang, text.Length);

        var (client, src, tgt) = CreateClientAndConfig(sourceLang, targetLang);
        var prompt = SystemPromptTemplate
            .Replace("[SourceLang]", src)
            .Replace("[TargetLang]", tgt);

        List<ChatMessage> messages =
        [
            new SystemChatMessage(prompt),
            new UserChatMessage(text)
        ];

        var completionOptions = new ChatCompletionOptions
        {
            Temperature = 0.3f,
            MaxOutputTokenCount = 4000,
#pragma warning disable OPENAI001
            ReasoningEffortLevel = ChatReasoningEffortLevel.Low,
#pragma warning restore OPENAI001
        };

        var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        jsonOptions.Converters.Add(new JsonStringEnumConverter());
        var reader = new JsonLinesDeltaStreamDecoder<SelectionTranslationStreamEvent>(line =>
            JsonSerializer.Deserialize<SelectionTranslationStreamEvent>(line, jsonOptions)
            ?? throw new JsonException("Empty structured translation event."),
            "translation_delta",
            "text");

        // Streaming iterators cannot yield from a try/catch block. Let transport and
        // protocol failures flow to the caller, which already owns the UI error state.
#pragma warning disable OPENAI001
        await foreach (var update in client.CompleteChatStreamingAsync(messages, completionOptions, cancellationToken))
        {
            foreach (var content in update.ContentUpdate)
            {
                foreach (var translationEvent in reader.Append(content.Text))
                {
                    yield return translationEvent;
                }
            }
        }
#pragma warning restore OPENAI001

        foreach (var translationEvent in reader.Complete())
        {
            yield return translationEvent;
        }
    }
}
