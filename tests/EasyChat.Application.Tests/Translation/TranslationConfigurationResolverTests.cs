using EasyChat.Application.Translation;
using EasyChat.Application.Tests.Settings;
using EasyChat.Contracts.Settings;
using EasyChat.Contracts.Translation;

namespace EasyChat.Application.Tests.Translation;

[TestClass]
public sealed class TranslationConfigurationResolverTests
{
    [TestMethod]
    public void ResolveFields_OnlyGlobalReferencesUseTheirMatchingGlobalValue()
    {
        var bundle = SettingsTestData.CreateBundle();
        var general = bundle.General with
        {
            TranslationEngine = TranslationEngineNames.MachineTrans,
            AiModel = "global-model-name",
            AiModelId = "global-model-id",
            MachineTranslation = "global-machine-name",
            MachineTranslationId = "global-machine-id"
        };
        var prompts = new PromptSettings("global-prompt", []);

        Assert.AreEqual(
            TranslationEngineNames.MachineTrans,
            TranslationConfigurationResolver.ResolveProvider(
                TranslationConfigurationOptionIds.FollowGlobal,
                general,
                TranslationEngineNames.AiModel));
        Assert.AreEqual(
            "local-model-id",
            TranslationConfigurationResolver.ResolveAiModelId("local-model-id", general));
        Assert.AreEqual(
            "global-model-id",
            TranslationConfigurationResolver.ResolveAiModelId(
                TranslationConfigurationOptionIds.FollowGlobal,
                general));
        Assert.AreEqual(
            "local-machine-id",
            TranslationConfigurationResolver.ResolveMachineProvider("local-machine-id", general, "Baidu"));
        Assert.AreEqual(
            "global-prompt",
            TranslationConfigurationResolver.ResolvePromptId(
                TranslationConfigurationOptionIds.FollowGlobal,
                prompts));
        Assert.AreEqual("local-prompt", TranslationConfigurationResolver.ResolvePromptId("local-prompt", prompts));
    }
}
