using System;
using System.Collections.Generic;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
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
        var prompt = """
# Role
You are a meticulous grammar, spelling, word-choice, and style editor.

# Task
Review the user's text in [Language]. Keep the same language.
The corrected text must remain in the user's input language.
All explanations, issue messages, and suggestions must be written in [UiLanguage], matching the application's UI language.
Report every meaningful issue with UTF-16 `start` and `length` offsets into the original text.
Then provide a complete corrected version.

# Output protocol
Return raw NDJSON only, one JSON object per line, no Markdown fences.
Emit exactly this order:
{"event":"start","mode":"correction","language":"[LanguageId]"}
Zero or more {"event":"issue","start":0,"length":1,"category":"grammar|spelling|word_choice|style","message":"...","suggestion":"..."}
One or more {"event":"corrected_delta","text":"..."} objects whose concatenated text is the complete corrected version.
{"event":"done"}
""".Replace("[Language]", language.EnglishName)
            .Replace("[LanguageId]", language.Id)
            .Replace("[UiLanguage]", uiLanguage);

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

#pragma warning disable OPENAI001
        await foreach (var update in client.CompleteChatStreamingAsync(messages, options, cancellationToken))
        {
            foreach (var content in update.ContentUpdate)
            {
                foreach (var item in decoder.Append(content.Text))
                    yield return item;
            }
        }
#pragma warning restore OPENAI001

        foreach (var item in decoder.Complete())
            yield return item;
    }

    private ITranslation CreateTranslationService(TextAssistProfile profile)
    {
        if (profile.Provider.Equals(TextAssistConstants.MachineProvider, StringComparison.OrdinalIgnoreCase))
            return _translationFactory.CreateMachineService(profile.MachineProvider ?? Constant.MachineTranslationProviders.Baidu);

        if (!string.IsNullOrWhiteSpace(profile.AiModelId))
            return _translationFactory.CreateAiServiceById(profile.AiModelId);

        if (profile.UsesGlobalConfiguration)
        {
            var name = _configurationService.General?.UsingAiModel;
            if (!string.IsNullOrWhiteSpace(name))
                return _translationFactory.CreateAiService(name);
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

    private static string ResolveUiLanguage()
    {
        var culture = Resources.Culture ?? CultureInfo.CurrentUICulture;
        return culture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase)
            ? "Simplified Chinese"
            : "English";
    }
}
