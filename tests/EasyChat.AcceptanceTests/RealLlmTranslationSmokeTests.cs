using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using EasyChat.Application.DependencyInjection;
using EasyChat.Application.Speech;
using EasyChat.Application.Translation;
using EasyChat.Contracts.Platform;
using EasyChat.Contracts.Settings;
using EasyChat.Contracts.Speech;
using EasyChat.Contracts.Translation;
using EasyChat.Infrastructure.DependencyInjection;
using EasyChat.Infrastructure.Translation.OpenAi;
using EasyChat.Shared.Results;
using EasyChat.Shared.Streaming;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace EasyChat.AcceptanceTests;

[TestClass]
public sealed class RealLlmTranslationSmokeTests
{
    private const string ApiUrlVariable = "EASYCHAT_TEST_LLM_API_URL";
    private const string ApiKeyVariable = "EASYCHAT_TEST_LLM_API_KEY";
    private const string ModelVariable = "EASYCHAT_TEST_LLM_MODEL";

    [TestMethod]
    [TestCategory("Live")]
    public async Task ConfiguredOpenAiCompatibleEndpoint_StreamsNonEmptyTranslation()
    {
        var provider = CreateConfiguredProviderOrMarkInconclusive();
        var request = new ChatTranslationProviderRequest(
            "Translate the user's text into Simplified Chinese. Return only the translation.",
            "Good morning.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var translation = new StringBuilder();

        await foreach (var chunk in provider.StreamAsync(request, timeout.Token))
            translation.Append(chunk);

        Assert.IsFalse(
            string.IsNullOrWhiteSpace(translation.ToString()),
            "The configured LLM endpoint completed without streaming translated text.");
    }

    [TestMethod]
    [TestCategory("Live")]
    public async Task ConfiguredOpenAiCompatibleEndpoint_StreamsValidSemanticSubtitleJsonLines()
    {
        const string current =
            "The microphone is ready. We can begin the live demo now. Please keep the captions concise.";
        const string contract = """
            Translate live subtitles from English to Simplified Chinese. The user content is a JSON
            object with `context` and `current` fields. Use `context` only to resolve meaning and
            translate only `current`. Split `current` into consecutive semantic subtitle sentences.
            Every record must contain exactly one sentence. Never combine two terminal sentences
            in one record: for example, `A. B.` must produce two records, not one.
            Emit one raw JSON object per line with exactly this schema:
            {"seq":0,"source":"exact consecutive source slice","translation":"...","final":true}
            `seq` must start at 0 and increase by 1. The `source` values concatenated in order must
            equal `current` exactly, including whitespace and punctuation. Do not omit, repeat,
            normalize, or paraphrase source text. `translation` must contain only the translation
            of that record's source. Set `final` to true for a complete sentence. Only the last
            record may use false when `current` ends with an incomplete sentence. Return raw JSONL
            only, with no Markdown fences, comments, arrays, explanatory text, or additional fields.
            """;
        var provider = CreateConfiguredProviderOrMarkInconclusive();
        var userText = JsonSerializer.Serialize(new
        {
            context = new[]
            {
                new
                {
                    Original = "We are preparing a product demonstration.",
                    Translation = "\u6211\u4eec\u6b63\u5728\u51c6\u5907\u4ea7\u54c1\u6f14\u793a\u3002"
                }
            },
            current
        });
        var request = new ChatTranslationProviderRequest(contract, userText);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var rawResponse = new StringBuilder();
        var invalidLines = new List<string>();
        var records = new List<JsonElement>();
        var decoder = new JsonLinesEventStreamDecoder<JsonElement>(
            line => JsonSerializer.Deserialize<JsonElement>(line),
            (_, line) => invalidLines.Add(line));

        await foreach (var chunk in provider.StreamAsync(request, timeout.Token))
        {
            rawResponse.Append(chunk);
            records.AddRange(decoder.Append(chunk));
        }

        records.AddRange(decoder.Complete());

        Assert.IsFalse(
            rawResponse.ToString().Contains("```", StringComparison.Ordinal),
            "The endpoint wrapped its JSONL response in Markdown.");
        Assert.IsEmpty(
            invalidLines,
            $"The endpoint emitted non-JSONL content: {string.Join(" | ", invalidLines)}");
        Assert.HasCount(
            3,
            records,
            "The endpoint must emit one JSONL record for each of the three source sentences.");

        var reconstructedSource = new StringBuilder();
        string[] expectedSentences =
        [
            "The microphone is ready.",
            "We can begin the live demo now.",
            "Please keep the captions concise."
        ];
        for (var index = 0; index < records.Count; index++)
        {
            var record = records[index];
            Assert.AreEqual(
                JsonValueKind.Object,
                record.ValueKind,
                $"JSONL record {index} must be an object.");
            var properties = record.EnumerateObject().ToArray();
            Assert.HasCount(
                4,
                properties,
                $"JSONL record {index} must contain exactly seq, source, translation, and final.");
            Assert.IsTrue(record.TryGetProperty("seq", out var seq), $"Record {index} has no seq.");
            var sequence = -1;
            Assert.IsTrue(
                seq.ValueKind == JsonValueKind.Number && seq.TryGetInt32(out sequence),
                $"Record {index} has a non-integer seq.");
            Assert.AreEqual(index, sequence, $"Record {index} has an unexpected seq.");

            Assert.IsTrue(
                record.TryGetProperty("source", out var source)
                && source.ValueKind == JsonValueKind.String,
                $"Record {index} has no string source.");
            var sourceText = source.GetString();
            Assert.IsFalse(string.IsNullOrEmpty(sourceText), $"Record {index} has an empty source.");
            reconstructedSource.Append(sourceText);
            Assert.AreEqual(
                expectedSentences[index],
                sourceText!.Trim(),
                $"Record {index} did not contain exactly one source sentence.");

            Assert.IsTrue(
                record.TryGetProperty("translation", out var translation)
                && translation.ValueKind == JsonValueKind.String,
                $"Record {index} has no string translation.");
            var translationText = translation.GetString();
            Assert.IsFalse(
                string.IsNullOrWhiteSpace(translationText),
                $"Record {index} has an empty translation.");
            Assert.AreNotEqual(
                sourceText.Trim(),
                translationText!.Trim(),
                $"Record {index} echoed the English source instead of translating it.");
            Assert.IsTrue(
                translationText.Any(IsCjk),
                $"Record {index} did not contain a Simplified Chinese translation.");

            Assert.IsTrue(
                record.TryGetProperty("final", out var final)
                && final.ValueKind is JsonValueKind.True or JsonValueKind.False,
                $"Record {index} has no boolean final.");
            Assert.IsTrue(
                final.GetBoolean(),
                $"Record {index} covers a complete source sentence and must have final=true.");
        }

        Assert.IsTrue(
            string.Equals(current, reconstructedSource.ToString(), StringComparison.Ordinal),
            "The JSONL source slices do not reconstruct current exactly with ordinal comparison.");
    }

    [TestMethod]
    [TestCategory("Live")]
    public async Task ScriptedAsr_ProducesPersistentSemanticSubtitleLinesThroughConfiguredLlm()
    {
        const string source =
            "The microphone is ready. We can begin the live demo now. Please keep the captions concise.";
        var configuration = ReadConfigurationOrMarkInconclusive();
        var configurationDirectory = Path.Combine(
            Path.GetTempPath(),
            "EasyChat.LiveSubtitle",
            Guid.NewGuid().ToString("N"));

        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddEasyChatInfrastructure(configurationDirectory);
            services.AddEasyChatApplication(new TranslationMessages("request failed"));
            await using var provider = services.BuildServiceProvider();

            var settings = provider.GetRequiredService<ISettingsUseCases>();
            var initialized = await settings.InitializeAsync();
            Assert.IsTrue(initialized.IsSuccess, initialized.Error.Message);

            const string modelId = "live-subtitle-model";
            var configured = settings.Current with
            {
                AiModel = new AiModelSettings(
                [
                    new CustomAiModelSettings(
                        modelId,
                        "Live subtitle model",
                        AiModelType.DeepSeek,
                        [configuration.ApiKey],
                        configuration.ApiUrl,
                        configuration.Model,
                        UseProxy: false,
                        EnableThinking: false)
                ]),
                SpeechRecognition = settings.Current.SpeechRecognition with
                {
                    RecognitionLanguage = "en-US",
                    IsTranslationEnabled = true,
                    IsRealTimePreviewEnabled = true,
                    TargetLanguage = "zh-Hans",
                    EngineId = modelId,
                    EngineType = 1,
                    MaxFloatingHistory = 4,
                    AutoClearInterval = 0
                }
            };
            var liveSettings = new InMemorySettingsUseCases(configured);
            var translation = new TranslationUseCases(
                liveSettings,
                provider.GetRequiredService<ITranslationProviderFactory>(),
                provider.GetRequiredService<ITranslationFailureSink>(),
                new TranslationMessages("request failed"));

            var useCases = new SpeechRecognitionUseCases(
                new ScriptedRecognitionEngine(source),
                new AvailablePlatformAccess(),
                liveSettings,
                translation,
                new BuiltInTranslationLanguageCatalog(),
                NullLogger<SpeechRecognitionUseCases>.Instance);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var events = new List<SpeechSessionEvent>();

            await foreach (var item in useCases.RecognizeAsync(
                               new SpeechRecognitionCommand("en-US", "en-US", []),
                               timeout.Token))
            {
                events.Add(item);
            }

            var changed = events.OfType<SpeechSubtitleChangedEvent>().ToArray();
            var latest = changed
                .GroupBy(item => item.Subtitle.Id)
                .Select(group => group.Last().Subtitle)
                .Where(line => !string.IsNullOrEmpty(line.OriginalText))
                .OrderBy(line => line.Timestamp)
                .ThenBy(line => line.Id)
                .ToArray();

            Assert.IsTrue(
                changed.Any(item => item.Subtitle.IsTemporary
                                    && item.Subtitle.OriginalText == source),
                "The ASR partial was not projected as an immediate subtitle draft.");
            Assert.HasCount(
                3,
                latest,
                "The completed LLM plan must materialize all three semantic subtitle sentences.");
            Assert.AreEqual(source, string.Concat(latest.Select(line => line.OriginalText)));
            CollectionAssert.AreEqual(
                new[]
                {
                    "The microphone is ready.",
                    "We can begin the live demo now.",
                    "Please keep the captions concise."
                },
                latest.Select(line => line.OriginalText.Trim()).ToArray());
            Assert.IsTrue(latest.All(line => !string.IsNullOrWhiteSpace(line.DisplayTranslatedText)));
            Assert.IsTrue(latest.All(line => line.DisplayTranslatedText.Any(IsCjk)));
            Assert.IsTrue(latest.All(line => !line.IsTemporary && !line.IsTranslating));
            Assert.IsTrue(
                changed.Any(item => item.Subtitle.IsTranslating
                                    && !string.IsNullOrWhiteSpace(item.Subtitle.DisplayTranslatedText)),
                "The LLM translation was buffered until completion instead of producing a readable streaming subtitle.");
            Assert.IsFalse(events.OfType<SpeechFloatingSubtitleRemovedEvent>().Any());
            Assert.IsInstanceOfType<SpeechSessionStoppedEvent>(events[^1]);
        }
        finally
        {
            if (Directory.Exists(configurationDirectory))
                Directory.Delete(configurationDirectory, recursive: true);
        }
    }

    private static OpenAiTranslationProvider CreateConfiguredProviderOrMarkInconclusive()
    {
        var configuration = ReadConfigurationOrMarkInconclusive();
        return new OpenAiTranslationProvider(
            configuration.ApiUrl,
            configuration.ApiKey,
            configuration.Model,
            proxy: TranslationProxyOptions.Direct);
    }

    private static LiveLlmConfiguration ReadConfigurationOrMarkInconclusive()
    {
        var apiUrl = Environment.GetEnvironmentVariable(ApiUrlVariable);
        var apiKey = Environment.GetEnvironmentVariable(ApiKeyVariable);
        var model = Environment.GetEnvironmentVariable(ModelVariable);
        var missing = new[]
        {
            (Name: ApiUrlVariable, Value: apiUrl),
            (Name: ApiKeyVariable, Value: apiKey),
            (Name: ModelVariable, Value: model)
        }.Where(variable => string.IsNullOrWhiteSpace(variable.Value))
            .Select(variable => variable.Name)
            .ToArray();

        if (missing.Length > 0)
        {
            Assert.Inconclusive(
                $"Set {string.Join(", ", missing)} to run the live LLM smoke test.");
        }

        return new LiveLlmConfiguration(apiUrl!, apiKey!, model!);
    }

    private static bool IsCjk(char character) =>
        character is >= '\u3400' and <= '\u9fff'
            or >= '\uf900' and <= '\ufaff';

    private sealed record LiveLlmConfiguration(string ApiUrl, string ApiKey, string Model);

    private sealed class ScriptedRecognitionEngine(string source) : ISpeechRecognitionEngine
    {
        public async IAsyncEnumerable<SpeechRecognitionEvent> RecognizeAsync(
            SpeechRecognitionOptions options,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new SpeechRecognitionEvent(SpeechRecognitionEventKind.Started);
            yield return new SpeechRecognitionEvent(SpeechRecognitionEventKind.Partial, source);
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            yield return new SpeechRecognitionEvent(SpeechRecognitionEventKind.Final, source);
            yield return new SpeechRecognitionEvent(SpeechRecognitionEventKind.Stopped);
        }
    }

    private sealed class AvailablePlatformAccess : IPlatformAccessUseCases
    {
        public ValueTask<Result<CapabilityStatus>> EnsureAvailableAsync(
            PlatformCapability capability,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Result<CapabilityStatus>.Success(
                new CapabilityStatus(capability, CapabilityState.Available)));

        public ValueTask<Result<PermissionStatus>> EnsurePermissionAsync(
            PlatformPermission permission,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Result<PermissionStatus>.Success(
                new PermissionStatus(permission, PermissionState.Granted)));
    }

    private sealed class InMemorySettingsUseCases(SettingsBundle current) : ISettingsUseCases
    {
        public event EventHandler<SettingsChangedEventArgs>? SettingsChanged
        {
            add { }
            remove { }
        }

        public event EventHandler<SettingsSaveFailedEventArgs>? SaveFailed
        {
            add { }
            remove { }
        }

        public bool IsInitialized => true;
        public SettingsBundle Current { get; private set; } = current;

        public ValueTask<Result<SettingsBundle>> InitializeAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Result<SettingsBundle>.Success(Current));

        public Result Update(SettingsSection section, SettingsBundle settings)
        {
            Current = settings;
            return Result.Success();
        }

        public ValueTask<Result> FlushAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Result.Success());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
