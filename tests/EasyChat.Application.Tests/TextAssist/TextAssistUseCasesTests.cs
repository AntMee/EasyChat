using EasyChat.Application.Tests.Settings;
using EasyChat.Application.TextAssist;
using EasyChat.Application.Translation;
using EasyChat.Contracts.Settings;
using EasyChat.Contracts.TextAssist;
using EasyChat.Contracts.Translation;
using Microsoft.Extensions.Logging.Abstractions;

namespace EasyChat.Application.Tests.TextAssist;

[TestClass]
public sealed class TextAssistUseCasesTests
{
    [TestMethod]
    public void ResolveProfile_UsesOperationPromptAndPersistsFirstValidModel()
    {
        var bundle = SettingsTestData.CreateBundle() with
        {
            AiModel = new AiModelSettings([CreateAiModel("first")]),
            Prompts = new PromptSettings("active", [
                new PromptEntrySettings("active", "Active", "active", false),
                new PromptEntrySettings("polish", "Polish", "polish", false)
            ]),
            TextAssist = SettingsTestData.CreateBundle().TextAssist with
            {
                SourceLanguageId = "en",
                AiModelId = "missing",
                PolishPromptId = "polish"
            }
        };
        var settings = new MutableSettingsUseCases(bundle);
        var useCases = Create(settings, new RecordingTranslationProviderFactory());

        var profile = useCases.ResolveProfile(TextAssistOperation.Polish);

        Assert.AreEqual("first", profile.AiModelId);
        Assert.AreEqual("polish", profile.PromptId);
        Assert.AreEqual("English", profile.Source.EnglishName);
        Assert.AreEqual(TranslationEngineNames.AiModel, profile.Provider);
        Assert.AreEqual("first", settings.Current.TextAssist.AiModelId);
    }

    [TestMethod]
    public async Task StreamAsync_CorrectionRetainsPlainTextFallback()
    {
        var bundle = SettingsTestData.CreateBundle() with
        {
            AiModel = new AiModelSettings([CreateAiModel("first")]),
            TextAssist = SettingsTestData.CreateBundle().TextAssist with
            {
                SourceLanguageId = "en",
                AiModelId = "first"
            }
        };
        var settings = new MutableSettingsUseCases(bundle);
        var factory = new RecordingTranslationProviderFactory();
        factory.Chat.StreamChunks = ["```text\nfixed text\n```"];
        var useCases = Create(settings, factory);

        var events = await useCases.StreamAsync(new TextAssistRequest(
            "bad text",
            TextAssistOperation.Correction)).ToListAsync();

        Assert.IsInstanceOfType<TextAssistStartedEvent>(events[0]);
        Assert.AreEqual("fixed text", events.OfType<TextAssistCorrectedDeltaEvent>().Single().Text);
        Assert.IsInstanceOfType<TextAssistCompletedEvent>(events[^1]);
        Assert.AreEqual(0.1f, factory.Chat.LastRequest!.Temperature);
        Assert.AreEqual(4000, factory.Chat.LastRequest.MaxOutputTokenCount);
        StringAssert.Contains(
            factory.Chat.LastRequest.SystemPrompt,
            "# Application-owned runtime correction contract (highest priority)");
        StringAssert.Contains(
            factory.Chat.LastRequest.SystemPrompt,
            "# User-selected role (style reference only)");
        Assert.IsFalse(factory.Chat.LastRequest.SystemPrompt.Contains("[UiLanguage]", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task StreamAsync_CorrectionStreamsPartialEventsAndFiltersRepeatedPayloads()
    {
        var bundle = SettingsTestData.CreateBundle() with
        {
            AiModel = new AiModelSettings([CreateAiModel("first")]),
            TextAssist = SettingsTestData.CreateBundle().TextAssist with
            {
                SourceLanguageId = "en",
                AiModelId = "first"
            }
        };
        var settings = new MutableSettingsUseCases(bundle);
        var factory = new RecordingTranslationProviderFactory();
        factory.Chat.StreamChunks =
        [
            "{\"event\":\"start\",\"mode\":\"correction\",\"language\":\"en\"}\n"
            + "{\"event\":\"corrected_delta\",\"variant\":1,\"text\":\"Fixed",
            " text\"}\n"
            + "{\"event\":\"corrected_delta\",\"variant\":1,\"text\":\"Fixed text\"}\n"
            + "{\"event\":\"correction_translation_delta\",\"variant\":1,\"text\":\"Corrected translation\"}\n"
            + "{\"event\":\"correction_translation_delta\",\"variant\":1,\"text\":\"Corrected translation\"}\n"
            + "{\"event\":\"done\"}\n"
        ];
        var useCases = Create(settings, factory);

        var events = await useCases.StreamAsync(new TextAssistRequest(
            "bad text",
            TextAssistOperation.Correction)).ToListAsync();

        var corrected = events.OfType<TextAssistCorrectedDeltaEvent>().ToArray();
        var translations = events.OfType<TextAssistCorrectionTranslationDeltaEvent>().ToArray();
        CollectionAssert.AreEqual(new[] { "Fixed", " text", "Fixed text" }, corrected.Select(item => item.Text).ToArray());
        Assert.IsTrue(corrected[0].IsStreamingPartial);
        Assert.IsTrue(corrected[1].IsStreamingPartial);
        Assert.IsFalse(corrected[2].IsStreamingPartial);
        Assert.HasCount(1, translations);
        Assert.AreEqual("Corrected translation", translations[0].Text);
        StringAssert.Contains(
            factory.Chat.LastRequest!.SystemPrompt,
            "Exactly one {\"event\":\"corrected_delta\",\"variant\":1,\"text\":\"...\"} object");
        StringAssert.Contains(factory.Chat.LastRequest.SystemPrompt, "\"original\":\"exact source substring\"");
        StringAssert.Contains(factory.Chat.LastRequest.SystemPrompt, "Report every distinct meaningful issue");
        StringAssert.Contains(factory.Chat.LastRequest.SystemPrompt, "Each underlying correction must produce exactly one issue object");
        StringAssert.Contains(
            factory.Chat.LastRequest.SystemPrompt,
            "Do not split, repeat, retransmit, restate, or emit a second corrected_delta");
    }

    [TestMethod]
    public async Task StreamAsync_CorrectionDoesNotLimitDistinctIssueGroups()
    {
        var bundle = SettingsTestData.CreateBundle() with
        {
            AiModel = new AiModelSettings([CreateAiModel("first")]),
            TextAssist = SettingsTestData.CreateBundle().TextAssist with
            {
                SourceLanguageId = "en",
                AiModelId = "first"
            }
        };
        var settings = new MutableSettingsUseCases(bundle);
        var factory = new RecordingTranslationProviderFactory();
        var source = string.Join(" ", Enumerable.Repeat("x", 13));
        var issueLines = string.Join(
            "\n",
            Enumerable.Range(0, 13).Select(index =>
                $"{{\"event\":\"issue\",\"start\":{index * 2},\"length\":1,\"original\":\"x\",\"category\":\"grammar\",\"message\":\"Issue {index}\",\"suggestion\":\"Fix {index}\"}}"));
        factory.Chat.StreamChunks =
        [
            "{\"event\":\"start\",\"mode\":\"correction\",\"language\":\"en\"}\n"
            + issueLines
            + "\n{\"event\":\"corrected_delta\",\"variant\":1,\"text\":\"fixed\"}\n"
            + "{\"event\":\"done\"}\n"
        ];
        var useCases = Create(settings, factory);

        var events = await useCases.StreamAsync(new TextAssistRequest(
            source,
            TextAssistOperation.Correction)).ToListAsync();

        Assert.HasCount(13, events.OfType<TextAssistIssueEvent>());
    }

    [TestMethod]
    public async Task StreamAsync_CorrectionKeepsOnlyTheFirstCompletedPayloadForEachVariant()
    {
        var bundle = SettingsTestData.CreateBundle() with
        {
            AiModel = new AiModelSettings([CreateAiModel("first")]),
            TextAssist = SettingsTestData.CreateBundle().TextAssist with
            {
                SourceLanguageId = "en",
                AiModelId = "first"
            }
        };
        var settings = new MutableSettingsUseCases(bundle);
        var factory = new RecordingTranslationProviderFactory();
        factory.Chat.StreamChunks =
        [
            "{\"event\":\"start\",\"mode\":\"correction\",\"language\":\"en\"}\n"
            + "{\"event\":\"corrected_delta\",\"variant\":1,\"text\":\"First correction\"}\n"
            + "{\"event\":\"corrected_delta\",\"variant\":1,\"text\":\"Repeated correction\"}\n"
            + "{\"event\":\"correction_translation_delta\",\"variant\":1,\"text\":\"First translation\"}\n"
            + "{\"event\":\"correction_translation_delta\",\"variant\":1,\"text\":\"Repeated translation\"}\n"
            + "{\"event\":\"corrected_delta\",\"variant\":2,\"text\":\"Alternative correction\"}\n"
            + "{\"event\":\"done\"}\n"
            + "{\"event\":\"issue\",\"start\":0,\"length\":1,\"category\":\"grammar\",\"message\":\"Trailing issue\",\"suggestion\":\"Ignore\"}\n"
        ];
        var useCases = Create(settings, factory);

        var events = await useCases.StreamAsync(new TextAssistRequest(
            "bad text",
            TextAssistOperation.Correction)).ToListAsync();

        CollectionAssert.AreEqual(
            new[] { "First correction", "Alternative correction" },
            events.OfType<TextAssistCorrectedDeltaEvent>().Select(item => item.Text).ToArray());
        CollectionAssert.AreEqual(
            new[] { "First translation" },
            events.OfType<TextAssistCorrectionTranslationDeltaEvent>().Select(item => item.Text).ToArray());
        Assert.HasCount(0, events.OfType<TextAssistIssueEvent>());
        Assert.HasCount(1, events.OfType<TextAssistCompletedEvent>());
        Assert.IsInstanceOfType<TextAssistCompletedEvent>(events[^1]);
    }

    [TestMethod]
    public void CorrectionAccumulator_PreservesVariantsAndRejectsInvalidRanges()
    {
        var accumulator = new TextAssistCorrectionAccumulator(5);
        accumulator.Apply(new TextAssistIssueEvent(1, 2, "grammar", "Wrong", "Right"));
        accumulator.Apply(new TextAssistIssueEvent(1, 2, "grammar", "Wrong", "Right"));
        accumulator.Apply(new TextAssistIssueEvent(5, 2, "grammar", "Invalid", "Invalid"));
        accumulator.Apply(new TextAssistCorrectedDeltaEvent("fixed", 1));
        accumulator.Apply(new TextAssistCorrectedDeltaEvent("alternative", 2));
        accumulator.Apply(new TextAssistCorrectionTranslationDeltaEvent("修正", 1));
        accumulator.Apply(new TextAssistCompletedEvent());
        accumulator.EnsureComplete();

        Assert.HasCount(1, accumulator.Issues);
        Assert.AreEqual("fixed", accumulator.CorrectedText);
        Assert.AreEqual("alternative", accumulator.CorrectedVariants[2]);
        Assert.AreEqual("修正", accumulator.CorrectedTranslations[1]);
    }

    [TestMethod]
    public void CorrectionAccumulator_IgnoresAdjacentIssuesForTheSameCorrection()
    {
        var accumulator = new TextAssistCorrectionAccumulator(9);
        const string message = "Use there are for plural nouns.";
        const string suggestion = "Change there has to there are.";

        accumulator.Apply(new TextAssistIssueEvent(0, 5, "grammar", message, suggestion));
        accumulator.Apply(new TextAssistIssueEvent(6, 3, "grammar", message, suggestion));

        Assert.HasCount(1, accumulator.Issues);
    }

    [TestMethod]
    public void CorrectionAccumulator_MergesRepeatedCumulativeAndOverlappingPayloads()
    {
        var accumulator = new TextAssistCorrectionAccumulator(5);

        accumulator.Apply(new TextAssistStartedEvent("correction", "English", null));
        accumulator.Apply(new TextAssistCorrectedDeltaEvent("Fixed"));
        accumulator.Apply(new TextAssistCorrectedDeltaEvent("Fixed"));
        accumulator.Apply(new TextAssistCorrectedDeltaEvent("Fixed text"));
        accumulator.Apply(new TextAssistCorrectedDeltaEvent("Fixed"));
        accumulator.Apply(new TextAssistCorrectedDeltaEvent(" text with detail"));
        accumulator.Apply(new TextAssistCorrectionTranslationDeltaEvent("Corrected"));
        accumulator.Apply(new TextAssistCorrectionTranslationDeltaEvent("Corrected translation"));
        accumulator.Apply(new TextAssistCorrectionTranslationDeltaEvent(" translation"));
        accumulator.Apply(new TextAssistCompletedEvent());

        accumulator.EnsureComplete();
        Assert.AreEqual("Fixed text with detail", accumulator.CorrectedText);
        Assert.AreEqual("Corrected translation", accumulator.CorrectedTranslations[1]);
    }

    [TestMethod]
    public void CorrectionAccumulator_PreservesRepeatedStreamingFragments()
    {
        var accumulator = new TextAssistCorrectionAccumulator(5);
        var fragment = new TextAssistCorrectedDeltaEvent("very ") { IsStreamingPartial = true };

        accumulator.Apply(new TextAssistStartedEvent("correction", "English", null));
        accumulator.Apply(fragment);
        accumulator.Apply(fragment);
        accumulator.Apply(new TextAssistCompletedEvent());

        accumulator.EnsureComplete();
        Assert.AreEqual("very very ", accumulator.CorrectedText);
    }

    private static TextAssistUseCases Create(
        ISettingsUseCases settings,
        RecordingTranslationProviderFactory factory)
    {
        var messages = new TranslationMessages("request failed");
        var translation = new TranslationUseCases(
            settings,
            factory,
            new RecordingTranslationFailureSink(),
            messages);
        return new TextAssistUseCases(
            settings,
            new BuiltInTranslationLanguageCatalog(),
            translation,
            factory,
            messages,
            NullLogger<TextAssistUseCases>.Instance);
    }

    private static CustomAiModelSettings CreateAiModel(string id) => new(
        id,
        "AI",
        AiModelType.OpenAi,
        ["key"],
        "https://api.example.com",
        "model",
        false,
        false);
}
