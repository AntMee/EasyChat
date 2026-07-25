using EasyChat.Models.Configuration;
using EasyChat.Services;
using EasyChat.Services.Abstractions;
using EasyChat.Services.Languages;

namespace EasyChat.Tests.Services.TextAssist;

[TestClass]
public sealed class TextAssistProfileResolverTests
{
    [TestMethod]
    public void Resolve_FollowGlobal_UsesGlobalLanguagesAndEngine()
    {
        var config = new FakeConfiguration
        {
            General = new General
            {
                SourceLanguage = LanguageService.GetLanguage("en"),
                TargetLanguage = LanguageService.GetLanguage("zh-Hans"),
                TransEngine = "MachineTrans",
                UsingMachineTrans = "Google",
                UsingAiModelId = "global-ai"
            },
            TextAssist = new TextAssistConfig { FollowGlobal = true }
        };

        var profile = new TextAssistProfileResolver(config).Resolve();

        Assert.AreEqual("en", profile.SourceLanguageId);
        Assert.AreEqual("zh-Hans", profile.TargetLanguageId);
        Assert.AreEqual("MachineTrans", profile.Provider);
        Assert.AreEqual("Google", profile.MachineProvider);
    }

    [TestMethod]
    public void Resolve_Correction_AlwaysUsesAiProvider()
    {
        var config = new FakeConfiguration
        {
            General = new General(),
            TextAssist = new TextAssistConfig { FollowGlobal = false, Provider = "MachineTrans", AiModelId = "local-ai" }
        };

        var profile = new TextAssistProfileResolver(config).Resolve(correction: true);

        Assert.AreEqual("AiModel", profile.Provider);
        Assert.AreEqual("local-ai", profile.AiModelId);
    }

    private sealed class FakeConfiguration : IConfigurationService
    {
        public General? General { get; init; }
        public AiModel? AiModel => null;
        public MachineTrans? MachineTrans => null;
        public Proxy? Proxy => null;
        public Shortcut? Shortcut => null;
        public Prompts? Prompts => null;
        public ResultConfig? Result => null;
        public InputConfig? Input => null;
        public ScreenshotConfig? Screenshot => null;
        public SelectionTranslationConfig? SelectionTranslation => null;
        public SpeechRecognitionConfig? SpeechRecognition => null;
        public TtsConfig? Tts => null;
        public TextAssistConfig? TextAssist { get; init; }
    }
}
