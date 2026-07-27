using EasyChat.Models.Configuration;
using EasyChat.Services;
using EasyChat.Services.Abstractions;
using EasyChat.Services.TextAssist;
using EasyChat.ViewModels.Pages;

namespace EasyChat.Tests.ViewModels.Pages;

[TestClass]
public sealed class TextAssistEditorViewModelTests
{
    [TestMethod]
    public void AvailableAiModels_RefreshWhenConfiguredModelsChange()
    {
        var initialModel = new CustomAiModel { Id = "initial", Name = "Initial" };
        var configuration = new FakeConfiguration
        {
            AiModel = new AiModel { ConfiguredModels = [initialModel] },
            TextAssist = new TextAssistConfig { AiModelId = initialModel.Id }
        };
        var profileResolver = new TextAssistProfileResolver(configuration);
        var translation = new TextAssistTranslationViewModel(configuration, profileResolver, null!, null!, null!, null!, null);
        var correction = new TextAssistCorrectionViewModel(configuration, profileResolver, null!, null);
        var addedModel = new CustomAiModel { Id = "added", Name = "Added" };

        configuration.AiModel!.ConfiguredModels.Add(addedModel);

        CollectionAssert.AreEqual(new[] { initialModel, addedModel }, translation.AvailableAiModels.ToArray());
        CollectionAssert.AreEqual(new[] { initialModel, addedModel }, correction.AvailableAiModels.ToArray());
    }

    private sealed class FakeConfiguration : IConfigurationService
    {
        public General? General => null;
        public AiModel? AiModel { get; init; }
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
        public OcrConfig? Ocr => null;
    }
}
