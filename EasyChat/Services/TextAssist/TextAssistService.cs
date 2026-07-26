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
using System.Globalization;
using System.Threading;
using EasyChat.Constants;
using EasyChat.Lang;
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

        var service = CreateTranslationService(profile);
        await foreach (var chunk in service.StreamTranslateAsync(text, source, target, cancellationToken))
        {
            if (!string.IsNullOrEmpty(chunk))
                yield return new TextAssistTranslationDeltaEvent(chunk);
        }

        yield return new TextAssistCompletedEvent();
    }

    public async IAsyncEnumerable<TextAssistStreamEvent> StreamCorrectAsync(
        string text,
        TextAssistProfile profile,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var source = LanguageService.GetLanguage(profile.SourceLanguageId);
        var (client, language) = CreateCorrectionClient(profile);
        var uiLanguage = ResolveUiLanguage();
        _logger.LogInformation("Correction UI language resolved to {UiLanguage}; configured language is {ConfiguredLanguage}.",
            uiLanguage, _configurationService.General?.Language);
        var prompt = BuildCorrectionPrompt(profile, """
# Role
You are a meticulous grammar, spelling, word-choice, and style editor.

# Task
Review the user's text in [Language].
The corrected text and all alternative expressions must remain in [Language].
Issue messages, suggestions, and the translations shown below each corrected
version must be written in [UiLanguage], matching the application's UI language.
Report every meaningful issue with UTF-16 `start` and `length` offsets into the original text.
Then provide a complete corrected version in [Language], followed by its translation in [UiLanguage].
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
After each corrected version, emit one or more {"event":"correction_translation_delta","variant":1,"text":"..."} objects containing its translation in [UiLanguage].
{"event":"done"}
""");
        prompt = prompt.Replace("[Language]", language.EnglishName)
            .Replace("[LanguageId]", language.Id)
            .Replace("[UiLanguage]", uiLanguage);
        prompt += BuildUiLanguageDirective(uiLanguage);

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

    private string ResolveUiLanguage()
    {
        var configured = _configurationService.General?.Language;
        if (string.Equals(configured, "Simplified Chinese", StringComparison.OrdinalIgnoreCase)
            || string.Equals(configured, "Chinese", StringComparison.OrdinalIgnoreCase)
            || string.Equals(configured, "简体中文", StringComparison.OrdinalIgnoreCase))
            return "Simplified Chinese";

        var culture = Resources.Culture ?? CultureInfo.CurrentUICulture;
        return culture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase)
            ? "Simplified Chinese"
            : "English";
    }

    private static string BuildUiLanguageDirective(string uiLanguage) =>
        uiLanguage.Equals("Simplified Chinese", StringComparison.OrdinalIgnoreCase)
            ? """

# Final mandatory language rule
The corrected text MUST remain in the original source language.
Only issue messages, suggestions, and correction translations MUST be written in Simplified Chinese.
The correction translations must be Simplified Chinese even when the source is English.
Every emitted corrected variant must be followed by its correction_translation_delta.
"""
            : """

# Final mandatory language rule
The corrected text MUST remain in the original source language.
Only issue messages, suggestions, and correction translations MUST be written in English.
Every emitted corrected variant must be followed by its correction_translation_delta.
""";

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
}
