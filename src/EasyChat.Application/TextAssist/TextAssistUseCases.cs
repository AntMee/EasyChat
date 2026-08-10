using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using EasyChat.Application.Translation;
using EasyChat.Contracts.Settings;
using EasyChat.Contracts.TextAssist;
using EasyChat.Contracts.Translation;
using EasyChat.Shared.Streaming;
using Microsoft.Extensions.Logging;

namespace EasyChat.Application.TextAssist;

public sealed class TextAssistUseCases : ITextAssistUseCases
{
    private readonly ISettingsUseCases _settings;
    private readonly ITranslationLanguageCatalog _languages;
    private readonly ITranslationUseCases _translation;
    private readonly ConfiguredTranslationProviderResolver _providers;
    private readonly ILogger<TextAssistUseCases> _logger;

    public TextAssistUseCases(
        ISettingsUseCases settings,
        ITranslationLanguageCatalog languages,
        ITranslationUseCases translation,
        ITranslationProviderFactory providerFactory,
        TranslationMessages messages,
        ILogger<TextAssistUseCases> logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _languages = languages ?? throw new ArgumentNullException(nameof(languages));
        _translation = translation ?? throw new ArgumentNullException(nameof(translation));
        _providers = new ConfiguredTranslationProviderResolver(settings, providerFactory, messages);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public TextAssistProfile ResolveProfile(TextAssistOperation operation)
    {
        var settings = _settings.Current;
        var general = settings.General;
        var config = settings.TextAssist;
        var requiresAi = operation != TextAssistOperation.Translation;
        var promptId = operation switch
        {
            TextAssistOperation.Correction => config.CorrectionPromptId,
            TextAssistOperation.Polish => config.PolishPromptId,
            TextAssistOperation.Summary => config.SummaryPromptId,
            TextAssistOperation.Explanation => config.SummaryPromptId,
            _ => config.TranslationPromptId
        };

        if (config.FollowGlobal)
        {
            var provider = requiresAi
                ? TranslationEngineNames.AiModel
                : general.TranslationEngine ?? TranslationEngineNames.AiModel;
            return new TextAssistProfile(
                Map(general.SourceLanguage),
                Map(general.TargetLanguage),
                provider,
                general.AiModelId,
                general.MachineTranslationId ?? general.MachineTranslation,
                UsesGlobalConfiguration: true,
                PromptId: ResolvePromptId(promptId),
                DetailedExplanation: operation == TextAssistOperation.Translation
                                     && config.DetailedExplanation
                                     && IsAiProvider(provider));
        }

        var model = settings.AiModel.ConfiguredModels.FirstOrDefault(candidate =>
                        string.Equals(candidate.Id, config.AiModelId, StringComparison.Ordinal))
                    ?? settings.AiModel.ConfiguredModels.FirstOrDefault();
        if (!string.Equals(config.AiModelId, model?.Id, StringComparison.Ordinal))
            PersistResolvedModel(config, model?.Id);
        var selectedProvider = requiresAi ? TranslationEngineNames.AiModel : config.Provider;
        return new TextAssistProfile(
            _languages.Get(config.SourceLanguageId),
            _languages.Get(config.TargetLanguageId),
            selectedProvider,
            model?.Id,
            config.MachineProvider,
            UsesGlobalConfiguration: false,
            PromptId: ResolvePromptId(promptId),
            DetailedExplanation: operation == TextAssistOperation.Translation
                                 && config.DetailedExplanation
                                 && IsAiProvider(selectedProvider));
    }

    public IAsyncEnumerable<TextAssistEvent> StreamAsync(
        TextAssistRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var profile = request.Profile ?? ResolveProfile(request.Operation);
        return request.Operation switch
        {
            TextAssistOperation.Translation => StreamTranslationAsync(request.Text, profile, cancellationToken),
            TextAssistOperation.Correction => StreamCorrectionAsync(request.Text, profile, cancellationToken),
            TextAssistOperation.Polish => StreamPolishAsync(request.Text, profile, cancellationToken),
            TextAssistOperation.Summary => StreamSummaryAsync(request.Text, profile, cancellationToken),
            TextAssistOperation.Explanation => StreamExplanationAsync(request.Text, profile, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(request), request.Operation, null)
        };
    }

    private async IAsyncEnumerable<TextAssistEvent> StreamTranslationAsync(
        string text,
        TextAssistProfile profile,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Text assist translation profile: source={SourceId} ({SourceName}), target={TargetId} ({TargetName}), provider={Provider}",
            profile.Source.Id,
            profile.Source.EnglishName,
            profile.Target.Id,
            profile.Target.EnglishName,
            profile.Provider);
        yield return new TextAssistStartedEvent(
            "translation",
            profile.Source.EnglishName,
            profile.Target.EnglishName);

        if (profile.DetailedExplanation)
        {
            await foreach (var item in StreamDetailedTranslationAsync(text, profile, cancellationToken)
                               .ConfigureAwait(false))
                yield return item;
            yield break;
        }

        var (machineId, machineName) = ResolveMachineProvider(profile.MachineProvider);
        var selection = new TranslationProviderSelection(
            profile.Provider,
            AiModelId: profile.AiModelId,
            AiModelName: profile.UsesGlobalConfiguration ? _settings.Current.General.AiModel : null,
            MachineProviderId: IsMachineProvider(profile.Provider) ? machineId : null,
            MachineProviderName: IsMachineProvider(profile.Provider) ? machineName : null,
            PromptOverride: BuildTranslationPrompt(profile));
        var prepared = _translation.Prepare(selection);
        try
        {
            await foreach (var item in prepared.StreamAsync(
                                   new TranslationRequest(text, profile.Source, profile.Target, PlainText: true),
                                   cancellationToken).ConfigureAwait(false))
            {
                if (item is TranslationDeltaEvent delta && !string.IsNullOrEmpty(delta.Text))
                    yield return new TextAssistTranslationDeltaEvent(delta.Text);
            }
        }
        finally
        {
            if (prepared is IDisposable disposable)
                disposable.Dispose();
        }

        yield return new TextAssistCompletedEvent();
    }

    private async IAsyncEnumerable<TextAssistEvent> StreamDetailedTranslationAsync(
        string text,
        TextAssistProfile profile,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var provider = CreateChatProvider(profile);
        var annotationLanguage = ResolveOutputLanguage();
        var prompt = BuildDetailedTranslationPrompt(profile)
            .Replace("[SourceLang]", profile.Source.EnglishName, StringComparison.Ordinal)
            .Replace("[TargetLang]", profile.Target.EnglishName, StringComparison.Ordinal)
            .Replace("[AnnotationLanguage]", annotationLanguage, StringComparison.Ordinal);
        await foreach (var item in StreamStructuredAsync(
                           provider,
                           new ChatTranslationProviderRequest(
                               prompt,
                               text,
                               Temperature: 0.2f,
                               MaxOutputTokenCount: 5000,
                               ReasoningEffort: ChatReasoningEffort.Low),
                           "translation_delta",
                           "Empty detailed translation event.",
                           fallbackMode: null,
                           fallbackLanguage: profile.Source.EnglishName,
                           cancellationToken).ConfigureAwait(false))
        {
            yield return item;
        }
    }

    private async IAsyncEnumerable<TextAssistEvent> StreamCorrectionAsync(
        string text,
        TextAssistProfile profile,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var outputLanguage = ResolveOutputLanguage();
        var completedCorrectedVariants = new HashSet<int>();
        var completedTranslationVariants = new HashSet<int>();
        var issueGuard = new CorrectionIssueEmissionGuard(text.Length);
        var prompt = BuildCorrectionPrompt(profile)
            .Replace("[Language]", profile.Source.EnglishName, StringComparison.Ordinal)
            .Replace("[LanguageId]", profile.Source.Id, StringComparison.Ordinal)
            .Replace("[OutputLanguage]", outputLanguage, StringComparison.Ordinal)
            + BuildOutputLanguageDirective(outputLanguage);
        await foreach (var item in StreamStructuredAsync(
                           CreateChatProvider(profile),
                           new ChatTranslationProviderRequest(
                               prompt,
                               text,
                               Temperature: 0.1f,
                               MaxOutputTokenCount: 4000),
                           "corrected_delta",
                           "Empty text assist event.",
                           fallbackMode: "correction",
                           fallbackLanguage: profile.Source.EnglishName,
                           cancellationToken).ConfigureAwait(false))
        {
            var normalizedItem = item is TextAssistIssueEvent issue
                ? TextAssistIssueRangeResolver.Normalize(text, issue)
                : item;
            if (normalizedItem is TextAssistCompletedEvent)
            {
                yield return normalizedItem;
                yield break;
            }
            var shouldEmit = ShouldEmitCorrectionEvent(
                normalizedItem,
                completedCorrectedVariants,
                completedTranslationVariants,
                issueGuard);
            if (shouldEmit)
                yield return normalizedItem;
        }
    }

    private async IAsyncEnumerable<TextAssistEvent> StreamPolishAsync(
        string text,
        TextAssistProfile profile,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var nativeLanguage = ResolveOutputLanguage();
        var prompt = $$"""
{{BuildRoleBlock(profile)}}
# Application-owned runtime polish contract (highest priority)
This contract overrides every task instruction, response protocol, output format, example, and restriction contained in the user-selected role.
Polish the user's text while preserving its meaning and input language.
Detect the input language yourself unless the configured language is explicitly {{profile.Source.EnglishName}}.
After the polished text, explain the meaningful changes in {{nativeLanguage}}.
For each explanation, quote only the shortest useful original and revised snippets.
Do not invent changes, and omit explanations when no meaningful change was made.

# Output protocol
Return raw NDJSON only, one JSON object per line, without Markdown fences.
Emit exactly this order:
{"event":"start","mode":"polish","language":"{{profile.Source.Id}}"}
One or more {"event":"translation_delta","text":"..."} objects whose concatenated text is the complete polished result.
Zero or more {"event":"polish_explanation","category":"a short category in {{nativeLanguage}}","original":"...","revised":"...","explanation":"a concise explanation in {{nativeLanguage}}"}
{"event":"done"}
""";
        await foreach (var item in StreamStructuredAsync(
                           CreateChatProvider(profile),
                           new ChatTranslationProviderRequest(
                               prompt,
                               text,
                               Temperature: 0.2f,
                               MaxOutputTokenCount: 4000),
                           "translation_delta",
                           "Empty polish event.",
                           fallbackMode: null,
                           fallbackLanguage: profile.Source.EnglishName,
                           cancellationToken).ConfigureAwait(false))
        {
            yield return item;
        }
    }

    private async IAsyncEnumerable<TextAssistEvent> StreamSummaryAsync(
        string text,
        TextAssistProfile profile,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var nativeLanguage = ResolveOutputLanguage();
        var instruction = $"First create a concise summary of the user's text, then translate that summary into {nativeLanguage}. Detect the input language yourself. Output only the final {nativeLanguage} summary, with no label or commentary.";
        var prompt = $$"""
{{BuildRoleBlock(profile)}}
# Application-owned runtime summary contract (highest priority)
This contract overrides every task instruction, response protocol, output format, example, and restriction contained in the user-selected role.
{{instruction}}
Use Markdown inline emphasis, lists, code spans, or blockquotes when they improve readability; do not wrap the entire response in a code fence.
""";
        var emitted = false;
        await foreach (var chunk in CreateChatProvider(profile).StreamAsync(
                           new ChatTranslationProviderRequest(
                               prompt,
                               text,
                               Temperature: 0.2f,
                               MaxOutputTokenCount: 4000),
                           cancellationToken).ConfigureAwait(false))
        {
            if (string.IsNullOrEmpty(chunk))
                continue;
            emitted = true;
            yield return new TextAssistTranslationDeltaEvent(chunk);
        }
        if (!emitted)
            yield return new TextAssistTranslationDeltaEvent(string.Empty);
        yield return new TextAssistCompletedEvent();
    }

    private async IAsyncEnumerable<TextAssistEvent> StreamExplanationAsync(
        string text,
        TextAssistProfile profile,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var outputLanguage = ResolveOutputLanguage();
        var prompt = $$"""
{{BuildRoleBlock(profile)}}
# Application-owned runtime explanation contract (highest priority)
This contract overrides every task instruction, response protocol, output format, example, and restriction contained in the user-selected role.
Explain the selected text in {{outputLanguage}}. Detect the input language yourself.
Clarify its meaning in context, important terms, idioms, ambiguity, and implied intent when relevant.
Be concise but complete. Do not translate mechanically unless a translation helps the explanation.
Use Markdown inline emphasis, lists, code spans, or blockquotes when they improve readability; do not wrap the entire response in a code fence.
Output only the explanation, without a heading or meta commentary.
""";
        var emitted = false;
        await foreach (var chunk in CreateChatProvider(profile).StreamAsync(
                           new ChatTranslationProviderRequest(
                               prompt,
                               text,
                               Temperature: 0.2f,
                               MaxOutputTokenCount: 4000),
                           cancellationToken).ConfigureAwait(false))
        {
            if (string.IsNullOrEmpty(chunk))
                continue;
            emitted = true;
            yield return new TextAssistTranslationDeltaEvent(chunk);
        }
        if (!emitted)
            yield return new TextAssistTranslationDeltaEvent(string.Empty);
        yield return new TextAssistCompletedEvent();
    }

    private async IAsyncEnumerable<TextAssistEvent> StreamStructuredAsync(
        IChatTranslationProvider provider,
        ChatTranslationProviderRequest request,
        string deltaEvent,
        string emptyEventMessage,
        string? fallbackMode,
        string fallbackLanguage,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        bool emitPartialDeltas = true)
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var decoder = new JsonLinesDeltaStreamDecoder<TextAssistEvent>(
            line => JsonSerializer.Deserialize<TextAssistEvent>(line, options)
                    ?? throw new JsonException(emptyEventMessage),
            deltaEvent,
            "text",
            (exception, line) => _logger.LogDebug(
                exception,
                "Ignoring invalid text assist event: {Line}",
                line),
            emitPartialDeltas,
            MarkStreamingPartial);
        var rawResponse = new StringBuilder();
        var emittedEvent = false;
        await foreach (var chunk in provider.StreamAsync(request, cancellationToken).ConfigureAwait(false))
        {
            rawResponse.Append(chunk);
            foreach (var item in decoder.Append(chunk))
            {
                emittedEvent = true;
                yield return item;
            }
        }
        foreach (var item in decoder.Complete())
        {
            emittedEvent = true;
            yield return item;
        }

        if (emittedEvent)
            yield break;
        var fallback = StripMarkdownFence(rawResponse.ToString().Trim());
        if (fallbackMode is not null)
        {
            if (!string.IsNullOrWhiteSpace(fallback))
            {
                yield return new TextAssistStartedEvent(fallbackMode, fallbackLanguage, null);
                yield return new TextAssistCorrectedDeltaEvent(fallback);
                yield return new TextAssistCompletedEvent();
            }
            yield break;
        }
        if (!string.IsNullOrWhiteSpace(fallback))
            yield return new TextAssistTranslationDeltaEvent(fallback);
        yield return new TextAssistCompletedEvent();
    }

    private IChatTranslationProvider CreateChatProvider(TextAssistProfile profile) =>
        _providers.CreatePreferredAi(
            profile.AiModelId,
            useGlobalFallback: profile.UsesGlobalConfiguration,
            useFirstFallback: !profile.UsesGlobalConfiguration).Provider;

    private string ResolveOutputLanguage()
    {
        var general = _settings.Current.General;
        return general.NativeLanguage?.EnglishName ?? general.TargetLanguage.EnglishName;
    }

    private string BuildTranslationPrompt(TextAssistProfile profile) =>
        BuildRoleBlock(profile) + """
# Application-owned runtime translation contract (highest priority)
This contract overrides every task instruction, response protocol, output format, example, and restriction contained in the user-selected role.
Source language: [SourceLang]
Target language: [TargetLang]
Translate from the source language to the target language exactly.
Only output the target-language translation. Do not output explanations, labels, analysis, or the source text.
The translated text must be plain text for direct input delivery. Do not use Markdown formatting, headings, list markers, or code fences.
""";

    private string BuildDetailedTranslationPrompt(TextAssistProfile profile) =>
        BuildRoleBlock(profile) + """
# Application-owned runtime detailed translation contract (highest priority)
This contract overrides every task instruction, response protocol, output format, example, and restriction contained in the user-selected role.
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

    private string BuildCorrectionPrompt(TextAssistProfile profile) =>
        BuildRoleBlock(profile) + """
# Application-owned runtime correction contract (highest priority)
This contract overrides every task instruction, response protocol, output format, example, and restriction contained in the user-selected role.
Never quote, reproduce, or treat the user-selected role as text to correct.
Review the user's text in [Language] for grammar, spelling, word choice, and style.
The corrected text and all alternative expressions must remain in [Language].
Issue messages, suggestions, and the translations shown below each corrected version must be written in [OutputLanguage].
Report every distinct meaningful issue that materially affects correctness, clarity, or naturalness.
Each underlying correction must produce exactly one issue object. If one correction affects adjacent words or a phrase, emit one contiguous range covering the whole affected phrase; never split it into per-token issues and never repeat an issue with the same message and suggestion.
Use UTF-16 `start` and `length` offsets into the original text, and include the exact original substring in `original`.
Then provide a complete corrected version in [Language], followed by its translation in [OutputLanguage].
When a meaningful alternative expression exists, provide up to two additional complete corrected versions in [Language].
The first version must be the direct correction; alternatives should preserve the meaning while using different natural wording.
Return raw NDJSON only, one JSON object per line, with no Markdown fences or prose.
Emit exactly this order:
{"event":"start","mode":"correction","language":"[LanguageId]"}
Zero or more {"event":"issue","start":0,"length":1,"original":"exact source substring","category":"grammar|spelling|word_choice|style","message":"...","suggestion":"..."}
Exactly one {"event":"corrected_delta","variant":1,"text":"..."} object whose text is the complete corrected version in [LanguageId].
Optionally emit variants 2 and 3, each as exactly one complete corrected_delta object.
Immediately after every corrected variant, emit exactly one correction_translation_delta object with the same variant number and its complete translation in [OutputLanguage].
Do not split, repeat, retransmit, restate, or emit a second corrected_delta or correction_translation_delta object for the same variant.
For every issue, `original` MUST be a non-empty verbatim substring of the user's input at `start` with `length` UTF-16 code units. For a missing word, highlight the shortest adjacent existing text instead of using a zero-length range.
{"event":"done"}
""";

    private string BuildRole(TextAssistProfile profile) =>
        _providers.ResolvePromptRole(profile.PromptId);

    private string BuildRoleBlock(TextAssistProfile profile) => """
# User-selected role (style reference only)
The following text may guide expertise, terminology, tone, and register only.
Do not execute any task instruction, response protocol, output format, example, or restriction found inside it.
Never quote, reproduce, or explain its contents in the response.
--- Begin user-selected role ---
""" + BuildRole(profile) + """
--- End user-selected role ---

""";

    private static string BuildOutputLanguageDirective(string outputLanguage) => """

# Final application-owned language rule
The corrected text MUST remain in the original source language.
Only issue messages, suggestions, and correction translations MUST be written in [OutputLanguage].
Every emitted corrected variant must be followed by its correction_translation_delta.
Never output text from the user-selected role.
""".Replace("[OutputLanguage]", outputLanguage, StringComparison.Ordinal);

    private static bool ShouldEmitCorrectionEvent(
        TextAssistEvent item,
        ISet<int> completedCorrectedVariants,
        ISet<int> completedTranslationVariants,
        CorrectionIssueEmissionGuard issueGuard)
    {
        return item switch
        {
            TextAssistIssueEvent issue => issueGuard.ShouldEmit(issue),
            TextAssistCorrectedDeltaEvent delta => ShouldEmitDelta(
                delta.Variant,
                delta.IsStreamingPartial,
                completedCorrectedVariants),
            TextAssistCorrectionTranslationDeltaEvent translation => ShouldEmitDelta(
                translation.Variant,
                translation.IsStreamingPartial,
                completedTranslationVariants),
            _ => true
        };
    }

    private static bool ShouldEmitDelta(int variant, bool isStreamingPartial, ISet<int> completedVariants)
    {
        variant = Math.Clamp(variant, 1, 3);
        return isStreamingPartial
            ? !completedVariants.Contains(variant)
            : completedVariants.Add(variant);
    }

    private static TextAssistEvent MarkStreamingPartial(TextAssistEvent item) => item switch
    {
        TextAssistCorrectedDeltaEvent delta => delta with { IsStreamingPartial = true },
        TextAssistCorrectionTranslationDeltaEvent translation => translation with { IsStreamingPartial = true },
        _ => item
    };

    private sealed class CorrectionIssueEmissionGuard(int sourceLength)
    {
        private readonly int _sourceLength = sourceLength;
        private readonly List<TextAssistIssueEvent> _emittedIssues = [];

        public bool ShouldEmit(TextAssistIssueEvent issue)
        {
            if (issue.Start < 0 || issue.Length <= 0 || issue.Start > _sourceLength
                || issue.Length > _sourceLength - issue.Start)
                return false;

            if (_emittedIssues.Any(existing =>
                    TextAssistCorrectionIssueRules.HasSameIdentity(existing, issue)))
                return false;

            _emittedIssues.Add(issue);
            return true;
        }
    }

    private string? ResolvePromptId(string? promptId)
    {
        var prompts = _settings.Current.Prompts;
        if (!string.IsNullOrWhiteSpace(promptId)
            && prompts.Entries.Any(prompt => string.Equals(prompt.Id, promptId, StringComparison.Ordinal)))
            return promptId;
        return string.IsNullOrWhiteSpace(prompts.SelectedPromptId) ? null : prompts.SelectedPromptId;
    }

    private void PersistResolvedModel(TextAssistSettings current, string? modelId)
    {
        var settings = _settings.Current;
        _settings.Update(
            SettingsSection.TextAssist,
            settings with { TextAssist = current with { AiModelId = modelId } });
    }

    private (string? Id, string? Name) ResolveMachineProvider(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return (null, MachineTranslationProviderNames.Baidu);
        var machine = _settings.Current.MachineTranslation;
        var isId = string.Equals(machine.Baidu.Id, value, StringComparison.Ordinal)
                   || string.Equals(machine.Tencent.Id, value, StringComparison.Ordinal)
                   || string.Equals(machine.Google.Id, value, StringComparison.Ordinal)
                   || string.Equals(machine.DeepL.Id, value, StringComparison.Ordinal);
        return isId ? (value, null) : (null, value);
    }

    private static TranslationLanguage Map(LanguageSettings language) => new(
        language.Id,
        language.EnglishName,
        language.ChineseName,
        language.ProviderCodes,
        language.Icon);

    private static bool IsAiProvider(string provider) =>
        string.Equals(provider, TranslationEngineNames.AiModel, StringComparison.OrdinalIgnoreCase)
        || string.Equals(provider, "AI", StringComparison.OrdinalIgnoreCase);

    private static bool IsMachineProvider(string provider) =>
        string.Equals(provider, TranslationEngineNames.MachineTrans, StringComparison.OrdinalIgnoreCase)
        || string.Equals(provider, "Machine", StringComparison.OrdinalIgnoreCase);

    private static string StripMarkdownFence(string value)
    {
        if (!value.StartsWith("```", StringComparison.Ordinal))
            return value;
        var firstLineEnd = value.IndexOf('\n');
        if (firstLineEnd >= 0)
            value = value[(firstLineEnd + 1)..];
        if (value.EndsWith("```", StringComparison.Ordinal))
            value = value[..^3];
        return value.Trim();
    }
}
