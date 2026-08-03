namespace EasyChat.Infrastructure.Settings.Persistence;

internal sealed class SettingsBundleDto
{
    public required GeneralSettingsDto General { get; init; }
    public required AiModelSettingsDto AiModel { get; init; }
    public required MachineTranslationSettingsDto MachineTranslation { get; init; }
    public required ProxySettingsDto Proxy { get; init; }
    public required ShortcutSettingsDto Shortcut { get; init; }
    public required PromptSettingsDto Prompts { get; init; }
    public required ResultSettingsDto Result { get; init; }
    public required InputSettingsDto Input { get; init; }
    public required ScreenshotSettingsDto Screenshot { get; init; }
    public required SpeechRecognitionSettingsDto SpeechRecognition { get; init; }
    public required SelectionTranslationSettingsDto SelectionTranslation { get; init; }
    public required TtsSettingsDto Tts { get; init; }
    public required TextAssistSettingsDto TextAssist { get; init; }
    public required OcrSettingsDto Ocr { get; init; }
}
