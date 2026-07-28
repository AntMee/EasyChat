using System;
using System.Collections.Generic;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using EasyChat.Constants;
using EasyChat.Models.Configuration;
using EasyChat.Models.Translation.TextAssist;
using EasyChat.Services.Abstractions;
using EasyChat.Services.Languages;
using EasyChat.Services.Translation;
using EasyChat.Services.Streaming;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Chat;

namespace EasyChat.Services.TextAssist;

public sealed class TextAssistService : ITextAssistService
{
    private readonly IConfigurationService _configurationService;
    private readonly ITranslationServiceFactory _translationFactory;
    private readonly ILogger<TextAssistService> _logger;

    public TextAssistService(
        IConfigurationService configurationService,
        ITranslationServiceFactory translationFactory,
        ILogger<TextAssistService> logger)
    {
        _configurationService = configurationService;
        _translationFactory = translationFactory;
        _logger = logger;
    }

    public async IAsyncEnumerable<TextAssistStreamEvent> StreamTranslateAsync(
        string text,
        TextAssistProfile profile,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var source = LanguageService.GetLanguage(profile.SourceLanguageId);
        var target = LanguageService.GetLanguage(profile.TargetLanguageId);
        _logger.LogInformation("Text assist translation profile: source={SourceId} ({SourceName}), target={TargetId} ({TargetName}), provider={Provider}",
            source.Id, source.EnglishName, target.Id, target.EnglishName, profile.Provider);
        yield return new TextAssistStartedEvent("translation", source.EnglishName, target.EnglishName);

        if (profile.DetailedExplanation)
        {
            await foreach (var item in StreamDetailedTranslationAsync(text, profile, source, target, cancellationToken))
                yield return item;
            yield break;
        }

        var service = CreateTranslationService(profile);
        await foreach (var chunk in service.StreamTranslateAsync(text, source, target, cancellationToken))
        {
            if (!string.IsNullOrEmpty(chunk))
                yield return new TextAssistTranslationDeltaEvent(chunk);
        }

        yield return new TextAssistCompletedEvent();
    }

    private async IAsyncEnumerable<TextAssistStreamEvent> StreamDetailedTranslationAsync(
        string text,
        TextAssistProfile profile,
        LanguageDefinition source,
        LanguageDefinition target,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var client = CreateTranslationClient(profile);
        var annotationLanguage = ResolveOutputLanguage();
        _logger.LogInformation(
            "Detailed translation annotation language resolved to {AnnotationLanguage}; configured native language is {NativeLanguage}.",
            annotationLanguage,
            _configurationService.General?.NativeLanguage?.Id);
        var prompt = BuildDetailedTranslationPrompt(profile)
            .Replace("[SourceLang]", source.EnglishName)
            .Replace("[TargetLang]", target.EnglishName)
            .Replace("[AnnotationLanguage]", annotationLanguage);
        List<ChatMessage> messages =
        [
            new SystemChatMessage(prompt),
            new UserChatMessage(text)
        ];
        var options = new ChatCompletionOptions
        {
            Temperature = 0.2f,
            MaxOutputTokenCount = 5000,
#pragma warning disable OPENAI001
            ReasoningEffortLevel = ChatReasoningEffortLevel.Low
#pragma warning restore OPENAI001
        };
        var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var decoder = new JsonLinesDeltaStreamDecoder<TextAssistStreamEvent>(
            line => JsonSerializer.Deserialize<TextAssistStreamEvent>(line, jsonOptions)
                    ?? throw new JsonException("Empty detailed translation event."),
            "translation_delta",
            "text",
            (exception, line) => _logger.LogDebug(exception, "Ignoring invalid detailed translation event: {Line}", line));
        var rawResponse = new StringBuilder();
        var emittedEvent = false;

#pragma warning disable OPENAI001
        await foreach (var update in client.CompleteChatStreamingAsync(messages, options, cancellationToken))
        {
            foreach (var content in update.ContentUpdate)
            {
                rawResponse.Append(content.Text);
                foreach (var item in decoder.Append(content.Text))
                {
                    emittedEvent = true;
                    yield return item;
                }
            }
        }
#pragma warning restore OPENAI001

        foreach (var item in decoder.Complete())
        {
            emittedEvent = true;
            yield return item;
        }

        if (!emittedEvent)
        {
            var fallback = StripMarkdownFence(rawResponse.ToString().Trim());
            if (!string.IsNullOrWhiteSpace(fallback))
                yield return new TextAssistTranslationDeltaEvent(fallback);
            yield return new TextAssistCompletedEvent();
        }
    }

    public async IAsyncEnumerable<TextAssistStreamEvent> StreamCorrectAsync(
        string text,
        TextAssistProfile profile,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (client, language) = CreateCorrectionClient(profile);
        var outputLanguage = ResolveOutputLanguage();
        _logger.LogInformation("Correction output language resolved to {OutputLanguage}; configured native language is {NativeLanguage}.",
            outputLanguage, _configurationService.General?.NativeLanguage?.Id);
        var prompt = BuildCorrectionPrompt(profile, """
# Role
You are a meticulous grammar, spelling, word-choice, and style editor.

# Task
Review the user's text in [Language].
The corrected text and all alternative expressions must remain in [Language].
Issue messages, suggestions, and the translations shown below each corrected
version must be written in [OutputLanguage], matching the user's native language.
Report every meaningful issue with UTF-16 `start` and `length` offsets into the original text.
Then provide a complete corrected version in [Language], followed by its translation in [OutputLanguage].
When a meaningful alternative expression exists, provide up to two additional
complete corrected versions in [Language]. The first version must be
the direct correction; alternatives should preserve the meaning while using
different natural wording. If no alternative is useful, emit only variant 1.

# Output protocol
Return raw NDJSON only, one JSON object per line, no Markdown fences.
Emit exactly this order:
{"event":"start","mode":"correction","language":"[LanguageId]"}
Zero or more {"event":"issue","start":0,"length":1,"category":"grammar|spelling|word_choice|style","message":"...","suggestion":"..."}
One or more {"event":"corrected_delta","variant":1,"text":"..."} objects whose concatenated text is the complete corrected version in [Language].
Optional variants 2 and 3 use their own concatenated corrected_delta sequence.
After each corrected version, emit one or more {"event":"correction_translation_delta","variant":1,"text":"..."} objects containing its translation in [OutputLanguage].
{"event":"done"}
""");
        prompt = prompt.Replace("[Language]", language.EnglishName)
            .Replace("[LanguageId]", language.Id)
            .Replace("[OutputLanguage]", outputLanguage);
        prompt += BuildOutputLanguageDirective(outputLanguage);

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(prompt),
            new UserChatMessage(text)
        };
        var options = new ChatCompletionOptions { Temperature = 0.1f, MaxOutputTokenCount = 4000 };
        var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var decoder = new JsonLinesDeltaStreamDecoder<TextAssistStreamEvent>(
            line => JsonSerializer.Deserialize<TextAssistStreamEvent>(line, jsonOptions)
                    ?? throw new JsonException("Empty text assist event."),
            "corrected_delta",
            "text",
                    (exception, line) => _logger.LogDebug(exception, "Ignoring invalid text assist event: {Line}", line));
        var rawResponse = new StringBuilder();
        var emittedEvent = false;

#pragma warning disable OPENAI001
        await foreach (var update in client.CompleteChatStreamingAsync(messages, options, cancellationToken))
        {
            foreach (var content in update.ContentUpdate)
            {
                rawResponse.Append(content.Text);
                foreach (var item in decoder.Append(content.Text))
                {
                    emittedEvent = true;
                    yield return item;
                }
            }
        }
#pragma warning restore OPENAI001

        foreach (var item in decoder.Complete())
        {
            emittedEvent = true;
            yield return item;
        }

        // Some compatible endpoints ignore the NDJSON instruction and return
        // a plain-text correction. Keep the editor usable instead of failing
        // with "Correction stream did not start" in that case.
        if (!emittedEvent)
        {
            var fallback = StripMarkdownFence(rawResponse.ToString().Trim());
            if (!string.IsNullOrWhiteSpace(fallback))
            {
                yield return new TextAssistStartedEvent("correction", language.EnglishName, null);
                yield return new TextAssistCorrectedDeltaEvent(fallback);
                yield return new TextAssistCompletedEvent();
            }
        }
    }

    public async IAsyncEnumerable<TextAssistStreamEvent> StreamPolishAsync(
        string text,
        TextAssistProfile profile,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (client, language) = CreateCorrectionClient(profile);
        var nativeLanguage = ResolveOutputLanguage();
        var prompt = $$"""
# Role
You are a precise writing editor.

# Task
Polish the user's text while preserving its meaning and input language.
Detect the input language yourself unless the configured language is explicitly {{language.EnglishName}}.
After the polished text, explain the meaningful changes in {{nativeLanguage}}.
For each explanation, quote only the shortest useful original and revised snippets.
Do not invent changes, and omit explanations when no meaningful change was made.

# Optional user guidance
{{BuildAssistGuidance(profile)}}

# Output protocol
Return raw NDJSON only, one JSON object per line, without Markdown fences.
Emit exactly this order:
{"event":"start","mode":"polish","language":"{{language.Id}}"}
One or more {"event":"translation_delta","text":"..."} objects whose concatenated text is the complete polished result.
Zero or more {"event":"polish_explanation","category":"a short category in {{nativeLanguage}}","original":"...","revised":"...","explanation":"a concise explanation in {{nativeLanguage}}"}
{"event":"done"}
""";
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(prompt),
            new UserChatMessage(text)
        };
        var options = new ChatCompletionOptions { Temperature = 0.2f, MaxOutputTokenCount = 4000 };
        var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var decoder = new JsonLinesDeltaStreamDecoder<TextAssistStreamEvent>(
            line => JsonSerializer.Deserialize<TextAssistStreamEvent>(line, jsonOptions)
                    ?? throw new JsonException("Empty polish event."),
            "translation_delta",
            "text",
            (exception, line) => _logger.LogDebug(exception, "Ignoring invalid polish event: {Line}", line));
        var rawResponse = new StringBuilder();
        var emittedEvent = false;

#pragma warning disable OPENAI001
        await foreach (var update in client.CompleteChatStreamingAsync(messages, options, cancellationToken))
        {
            foreach (var content in update.ContentUpdate)
            {
                rawResponse.Append(content.Text);
                foreach (var item in decoder.Append(content.Text))
                {
                    emittedEvent = true;
                    yield return item;
                }
            }
        }
#pragma warning restore OPENAI001

        foreach (var item in decoder.Complete())
        {
            emittedEvent = true;
            yield return item;
        }

        if (!emittedEvent)
        {
            var fallback = StripMarkdownFence(rawResponse.ToString().Trim());
            if (!string.IsNullOrWhiteSpace(fallback))
                yield return new TextAssistTranslationDeltaEvent(fallback);
            yield return new TextAssistCompletedEvent();
        }
    }

    public IAsyncEnumerable<TextAssistStreamEvent> StreamSummarizeAsync(
        string text,
        TextAssistProfile profile,
        CancellationToken cancellationToken = default) =>
        StreamPlainTextAssistAsync(text, profile, cancellationToken);

    private async IAsyncEnumerable<TextAssistStreamEvent> StreamPlainTextAssistAsync(
        string text,
        TextAssistProfile profile,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var (client, _) = CreateCorrectionClient(profile);
        var nativeLanguage = ResolveOutputLanguage();
        var instruction = $"First create a concise summary of the user's text, then translate that summary into {nativeLanguage}. Detect the input language yourself. Output only the final {nativeLanguage} summary, with no label or commentary.";
        var prompt = $"""
# Role
You are a precise writing assistant.

# Task
{instruction}

# Optional user guidance
{BuildAssistGuidance(profile)}
""";
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(prompt),
            new UserChatMessage(text)
        };
        var options = new ChatCompletionOptions { Temperature = 0.2f, MaxOutputTokenCount = 4000 };
        var emitted = false;
#pragma warning disable OPENAI001
        await foreach (var update in client.CompleteChatStreamingAsync(messages, options, cancellationToken))
        {
            foreach (var content in update.ContentUpdate)
            {
                if (string.IsNullOrEmpty(content.Text)) continue;
                emitted = true;
                yield return new TextAssistTranslationDeltaEvent(content.Text);
            }
        }
#pragma warning restore OPENAI001
        if (!emitted) yield return new TextAssistTranslationDeltaEvent(string.Empty);
        yield return new TextAssistCompletedEvent();
    }

    private static string StripMarkdownFence(string value)
    {
        if (!value.StartsWith("```", StringComparison.Ordinal)) return value;
        var firstLineEnd = value.IndexOf('\n');
        if (firstLineEnd >= 0) value = value[(firstLineEnd + 1)..];
        if (value.EndsWith("```", StringComparison.Ordinal)) value = value[..^3];
        return value.Trim();
    }

    private ITranslation CreateTranslationService(TextAssistProfile profile)
    {
        if (profile.Provider.Equals(TextAssistConstants.MachineProvider, StringComparison.OrdinalIgnoreCase))
            return _translationFactory.CreateMachineService(profile.MachineProvider ?? Constant.MachineTranslationProviders.Baidu);

        var prompt = BuildTranslationPrompt(profile);
        if (!string.IsNullOrWhiteSpace(profile.AiModelId)
            && _configurationService.AiModel?.ConfiguredModels.Any(x => x.Id == profile.AiModelId) == true)
            return _translationFactory.CreateAiServiceById(profile.AiModelId, prompt);

        if (profile.UsesGlobalConfiguration)
        {
            var name = _configurationService.General?.UsingAiModel;
            if (!string.IsNullOrWhiteSpace(name))
                return _translationFactory.CreateAiService(name, prompt);
        }

        throw new InvalidOperationException("No AI model is configured for translation.");
    }

    private (ChatClient Client, LanguageDefinition Language) CreateCorrectionClient(TextAssistProfile profile)
    {
        var model = !string.IsNullOrWhiteSpace(profile.AiModelId)
            ? _configurationService.AiModel?.ConfiguredModels.FirstOrDefault(x => x.Id == profile.AiModelId)
            : null;
        if (profile.UsesGlobalConfiguration)
        {
            model ??= _configurationService.AiModel?.ConfiguredModels.FirstOrDefault(x =>
                x.Id == _configurationService.General?.UsingAiModelId ||
                x.Name == _configurationService.General?.UsingAiModel);
        }
        if (model == null) throw new InvalidOperationException("No AI model is configured for correction.");

        var options = new OpenAIClientOptions { Endpoint = new Uri(model.ApiUrl) };
        if (model.UseProxy && !string.IsNullOrWhiteSpace(_configurationService.Proxy?.ProxyUrl))
        {
            options.Transport = new HttpClientPipelineTransport(new HttpClient(new HttpClientHandler
            {
                Proxy = new WebProxy(_configurationService.Proxy.ProxyUrl),
                UseProxy = true
            }));
        }

        var client = new OpenAIClient(new ApiKeyCredential(model.ApiKey), options).GetChatClient(model.Model);
        return (client, LanguageService.GetLanguage(profile.SourceLanguageId));
    }

    private ChatClient CreateTranslationClient(TextAssistProfile profile)
    {
        var model = !string.IsNullOrWhiteSpace(profile.AiModelId)
            ? _configurationService.AiModel?.ConfiguredModels.FirstOrDefault(x => x.Id == profile.AiModelId)
            : null;
        if (profile.UsesGlobalConfiguration)
        {
            model ??= _configurationService.AiModel?.ConfiguredModels.FirstOrDefault(x =>
                x.Id == _configurationService.General?.UsingAiModelId ||
                x.Name == _configurationService.General?.UsingAiModel);
        }
        if (model == null) throw new InvalidOperationException("No AI model is configured for translation.");

        var options = new OpenAIClientOptions { Endpoint = new Uri(model.ApiUrl) };
        if (model.UseProxy && !string.IsNullOrWhiteSpace(_configurationService.Proxy?.ProxyUrl))
        {
            options.Transport = new HttpClientPipelineTransport(new HttpClient(new HttpClientHandler
            {
                Proxy = new WebProxy(_configurationService.Proxy.ProxyUrl),
                UseProxy = true
            }));
        }

        return new OpenAIClient(new ApiKeyCredential(model.ApiKey), options).GetChatClient(model.Model);
    }

    private string ResolveOutputLanguage()
    {
        return _configurationService.General?.NativeLanguage?.EnglishName
               ?? _configurationService.General?.TargetLanguage?.EnglishName
               ?? LanguageService.GetLanguage("zh-Hans").EnglishName;
    }

    private static string BuildOutputLanguageDirective(string outputLanguage) => """

# Final mandatory language rule
The corrected text MUST remain in the original source language.
Only issue messages, suggestions, and correction translations MUST be written in [OutputLanguage].
Every emitted corrected variant must be followed by its correction_translation_delta.
""".Replace("[OutputLanguage]", outputLanguage);

    private string BuildTranslationPrompt(TextAssistProfile profile)
    {
        var configured = profile.PromptId is { Length: > 0 }
            ? _configurationService.Prompts?.FindById(profile.PromptId)?.Content
            : null;
        var prompt = configured ?? _configurationService.Prompts?.ActivePromptContent ?? Prompts.DefaultPromptContent;
        return prompt + """

# Runtime translation contract
Source language: [SourceLang]
Target language: [TargetLang]
Translate from the source language to the target language exactly.
Only output the target-language translation. Do not output explanations, labels, analysis, or the source text.
""";
    }

    private string BuildDetailedTranslationPrompt(TextAssistProfile profile)
    {
        var configured = profile.PromptId is { Length: > 0 }
            ? _configurationService.Prompts?.FindById(profile.PromptId)?.Content
            : null;
        var guidance = configured ?? _configurationService.Prompts?.ActivePromptContent ?? Prompts.DefaultPromptContent;
        return """
# Role
You are a professional translator and language-learning annotator.

# User-selected translation guidance (secondary)
""" + guidance + """

# Runtime detailed translation contract
Source language: [SourceLang]
Target language: [TargetLang]
Annotation language: [AnnotationLanguage]
Translate the input naturally, then explain the source-language vocabulary and expressions that materially help a reader understand or learn it.
The translation MUST be in [TargetLang]. All annotation meanings, notes, labels, and explanations MUST be in [AnnotationLanguage], matching the user's native language.

Return raw NDJSON only, one complete JSON object per line, with no Markdown fences or prose.
Emit exactly this order:
1. `{"event":"source_detected","language":"en"}` when the source language is auto-detected.
2. One or more `{"event":"translation_delta","text":"..."}` objects. Concatenating their text MUST produce only the complete translation.
3. Zero to twelve annotation objects:
   `{"event":"annotation","term":"source word or phrase","category":"important_word|uncommon_word|collocation|usage_tip","meaning":"concise meaning in [AnnotationLanguage]","note":"context, grammar, nuance, or collocation guidance in [AnnotationLanguage]","relatedTerms":["source-language related word or phrase"]}`
4. `{"event":"done"}`

Annotation rules:
- Cover important words, uncommon words, fixed collocations, contextual meanings, register, and easy-to-miss usage when relevant.
- Use `term` for the exact source-language word or phrase being explained. Use its dictionary lemma only when that makes lookup clearer.
- Every value in `relatedTerms` MUST also be a source-language word or phrase suitable for dictionary lookup.
- Do not repeat annotations or annotate trivial function words.
- `meaning` is required. Omit `note` or use an empty string only when no extra explanation is useful. Use an empty array when there are no related terms.
- The protocol above has priority over the user-selected guidance. Never emit text outside the documented NDJSON events.
""";
    }

    private string BuildCorrectionPrompt(TextAssistProfile profile, string fallback)
    {
        var configured = profile.PromptId is { Length: > 0 }
            ? _configurationService.Prompts?.FindById(profile.PromptId)?.Content
            : null;
        return """
# User-selected correction guidance
""" + (configured ?? fallback) + """

# Runtime correction contract
The guidance above is secondary. You MUST follow this correction protocol even if it conflicts with the selected guidance.
Return raw NDJSON only, one JSON object per line, with no Markdown fences or prose.
Emit exactly this order:
{"event":"start","mode":"correction","language":"[LanguageId]"}
Zero or more {"event":"issue","start":0,"length":1,"category":"grammar|spelling|word_choice|style","message":"...","suggestion":"..."}
One or more {"event":"corrected_delta","variant":1,"text":"..."} objects whose concatenated text is the complete corrected version in [LanguageId].
Optional variants 2 and 3 use their own concatenated corrected_delta sequence.
After each corrected version, emit one or more {"event":"correction_translation_delta","variant":1,"text":"..."} objects containing its translation in [UiLanguage].
{"event":"done"}
""";
    }

    private string BuildAssistGuidance(TextAssistProfile profile)
    {
        var configured = profile.PromptId is { Length: > 0 }
            ? _configurationService.Prompts?.FindById(profile.PromptId)?.Content
            : null;
        return configured ?? _configurationService.Prompts?.ActivePromptContent ?? string.Empty;
    }
}
