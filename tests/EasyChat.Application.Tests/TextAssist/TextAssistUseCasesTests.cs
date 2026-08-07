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
    public async Task StreamAsync_CorrectionWaitsForCompleteEventsAndFiltersRepeatedPayloads()
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
        Assert.HasCount(1, corrected);
        Assert.AreEqual("Fixed text", corrected[0].Text);
        Assert.HasCount(1, translations);
        Assert.AreEqual("Corrected translation", translations[0].Text);
        StringAssert.Contains(
            factory.Chat.LastRequest!.SystemPrompt,
            "Exactly one {\"event\":\"corrected_delta\",\"variant\":1,\"text\":\"...\"} object");
        StringAssert.Contains(
            factory.Chat.LastRequest.SystemPrompt,
            "Do not split, repeat, retransmit, restate, or emit a second corrected_delta");
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
