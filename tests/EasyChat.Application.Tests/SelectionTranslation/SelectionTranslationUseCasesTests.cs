using EasyChat.Application.SelectionTranslation;
using EasyChat.Application.Tests.Settings;
using EasyChat.Contracts.SelectionTranslation;
using EasyChat.Contracts.Settings;
using EasyChat.Contracts.Translation;
using Microsoft.Extensions.Logging.Abstractions;

namespace EasyChat.Application.Tests.SelectionTranslation;

[TestClass]
public sealed class SelectionTranslationUseCasesTests
{
    [TestMethod]
    public async Task StreamDictionaryAsync_UsesStructuredAiProtocolAndPersistsFallbackModel()
    {
        var bundle = SettingsTestData.CreateBundle() with
        {
            AiModel = new AiModelSettings([CreateAiModel("first")]),
            Prompts = new PromptSettings("prompt", [new PromptEntrySettings(
                "prompt", "Prompt", "User guidance", false)]),
            SelectionTranslation = new SelectionTranslationSettings(
                true, "AI", null, "missing", null, SelectionTriggerMode.All,
                true, false, false, false)
        };
        var settings = new MutableSettingsUseCases(bundle);
        var factory = new RecordingTranslationProviderFactory();
        factory.Chat.StreamChunks =
        [
            "{\"event\":\"start\",\"mode\":\"word\"}\n{\"event\":\"word_header\",\"word\":\"hello\",\"phonetic\":\"hə",
            "ˈləʊ\"}\n{\"event\":\"definition\",\"pos\":\"int.\",\"meaning\":\"你好\"}\n{\"event\":\"done\"}"
        ];
        var useCases = Create(settings, factory);

        var events = await useCases.StreamDictionaryAsync(CreateRequest("hello")).ToListAsync();

        Assert.IsInstanceOfType<SelectionTranslationStartedEvent>(events[0]);
        Assert.IsInstanceOfType<SelectionTranslationCompletedEvent>(events[^1]);
        Assert.AreEqual("first", settings.Current.SelectionTranslation.AiModelId);
        Assert.IsNotNull(factory.Chat.LastRequest);
        Assert.Contains("# Forced dictionary lookup", factory.Chat.LastRequest.SystemPrompt);
        Assert.Contains("Preserve the source text's paragraph and line-break structure", factory.Chat.LastRequest.SystemPrompt);
        Assert.AreEqual(0.3f, factory.Chat.LastRequest.Temperature);
        Assert.AreEqual(4000, factory.Chat.LastRequest.MaxOutputTokenCount);
        Assert.AreEqual(ChatReasoningEffort.Low, factory.Chat.LastRequest.ReasoningEffort);
    }

    [TestMethod]
    public async Task StreamAsync_GlobalScopeUsesGlobalModelAndPromptWithoutChangingSelectionSettings()
    {
        var initial = SettingsTestData.CreateBundle();
        var bundle = initial with
        {
            General = initial.General with
            {
                TranslationEngine = TranslationEngineNames.AiModel,
                AiModel = "global",
                AiModelId = "global"
            },
            AiModel = new AiModelSettings([CreateAiModel("selection"), CreateAiModel("global")]),
            Prompts = new PromptSettings("global-prompt", [
                new PromptEntrySettings("selection-prompt", "Selection", "selection guidance", false),
                new PromptEntrySettings("global-prompt", "Global", "global guidance", false)]),
            SelectionTranslation = initial.SelectionTranslation with
            {
                AiModelId = "selection",
                PromptId = "selection-prompt"
            }
        };
        var settings = new MutableSettingsUseCases(bundle);
        var factory = new RecordingTranslationProviderFactory();
        factory.Chat.StreamChunks = [
            "{\"event\":\"start\",\"mode\":\"sentence\"}\n"
            + "{\"event\":\"translation_delta\",\"text\":\"translated\"}\n"
            + "{\"event\":\"done\"}"
        ];
        var useCases = Create(settings, factory);

        await useCases.StreamAsync(
                CreateRequest("hello"),
                configurationScope: SelectionTranslationConfigurationScope.Global)
            .ToListAsync();

        Assert.AreEqual("global", factory.AiOptions!.Provider.Id);
        StringAssert.Contains(factory.Chat.LastRequest!.SystemPrompt, "global guidance");
        Assert.IsFalse(factory.Chat.LastRequest.SystemPrompt.Contains("selection guidance", StringComparison.Ordinal));
        Assert.AreEqual("selection", settings.Current.SelectionTranslation.AiModelId);
    }

    [TestMethod]
    public async Task StreamAsync_MachineWordModeUsesProviderLanguageCodes()
    {
        var bundle = SettingsTestData.CreateBundle() with
        {
            SelectionTranslation = new SelectionTranslationSettings(
                true, "Machine", "Baidu", null, null, SelectionTriggerMode.All,
                true, false, false, false)
        };
        var settings = new MutableSettingsUseCases(bundle);
        var factory = new RecordingTranslationProviderFactory();
        factory.Machine.Response = "你好";
        var useCases = Create(settings, factory);
        var source = new TranslationLanguage(
            "en", "English", ProviderCodes: new Dictionary<string, string> { ["Baidu"] = "en" });
        var target = new TranslationLanguage(
            "zh-Hans", "Simplified Chinese", ProviderCodes: new Dictionary<string, string> { ["Baidu"] = "zh" });

        var events = await useCases.StreamAsync(new SelectionTranslationRequest("hello", source, target)).ToListAsync();

        Assert.IsInstanceOfType<SelectionTranslationStartedEvent>(events[0]);
        Assert.AreEqual(SelectionTranslationMode.Word, ((SelectionTranslationStartedEvent)events[0]).Mode);
        Assert.AreEqual("en", factory.Machine.LastRequest!.SourceLanguageCode);
        Assert.AreEqual("zh", factory.Machine.LastRequest.TargetLanguageCode);
        Assert.AreEqual("你好", events.OfType<SelectionTranslationDefinitionEvent>().Single().Meaning);
    }

    [TestMethod]
    public async Task StreamDictionaryAsync_MachineScopeUsesConfiguredMachineProviderAndWordMode()
    {
        var bundle = SettingsTestData.CreateBundle() with
        {
            SelectionTranslation = new SelectionTranslationSettings(
                true, TranslationEngineNames.MachineTrans, "Baidu", null, null, SelectionTriggerMode.All,
                true, false, false, false)
        };
        var settings = new MutableSettingsUseCases(bundle);
        var factory = new RecordingTranslationProviderFactory();
        factory.Machine.Response = "你好世界";
        var useCases = Create(settings, factory);

        var events = await useCases.StreamDictionaryAsync(CreateRequest("hello world")).ToListAsync();

        Assert.IsInstanceOfType<SelectionTranslationStartedEvent>(events[0]);
        Assert.AreEqual(SelectionTranslationMode.Word, ((SelectionTranslationStartedEvent)events[0]).Mode);
        Assert.AreEqual("Baidu", factory.MachineOptions!.Provider.Name);
        Assert.AreEqual("你好世界", events.OfType<SelectionTranslationDefinitionEvent>().Single().Meaning);
    }

    private static SelectionTranslationUseCases Create(
        ISettingsUseCases settings,
        ITranslationProviderFactory factory) => new(
        settings,
        factory,
        new TranslationMessages("request failed"),
        NullLogger<SelectionTranslationUseCases>.Instance);

    private static SelectionTranslationRequest CreateRequest(string text) => new(
        text,
        new TranslationLanguage("en", "English"),
        new TranslationLanguage("zh-Hans", "Simplified Chinese"));

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
