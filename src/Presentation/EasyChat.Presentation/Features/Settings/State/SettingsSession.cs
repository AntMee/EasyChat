using EasyChat.Contracts.Settings;
using EasyChat.Shared.Results;

namespace EasyChat.Presentation.Features.Settings.State;

public sealed class SettingsSession(ISettingsUseCases useCases)
{
    private readonly ISettingsUseCases _useCases = useCases ?? throw new ArgumentNullException(nameof(useCases));
    private SettingsBundle? _current;

    public event EventHandler<SettingsChangedEventArgs>? Changed;

    public bool IsInitialized => _current is not null;
    public SettingsBundle Current => _current ?? throw new InvalidOperationException("Settings are not initialized.");

    public LiveGeneralSettings General { get; private set; } = null!;
    public LiveAiModelSettings AiModel { get; private set; } = null!;
    public LiveMachineTranslationSettings MachineTranslation { get; private set; } = null!;
    public LiveProxySettings Proxy { get; private set; } = null!;
    public LiveShortcutSettings Shortcut { get; private set; } = null!;
    public LivePromptSettings Prompts { get; private set; } = null!;
    public LiveResultSettings Result { get; private set; } = null!;
    public LiveInputSettings Input { get; private set; } = null!;
    public LiveScreenshotSettings Screenshot { get; private set; } = null!;
    public LiveSpeechRecognitionSettings SpeechRecognition { get; private set; } = null!;
    public LiveSelectionTranslationSettings SelectionTranslation { get; private set; } = null!;
    public LiveTtsSettings Tts { get; private set; } = null!;
    public LiveTextAssistSettings TextAssist { get; private set; } = null!;
    public LiveOcrSettings Ocr { get; private set; } = null!;

    public async ValueTask<Result<SettingsBundle>> InitializeAsync(CancellationToken cancellationToken = default)
    {
        var result = await _useCases.InitializeAsync(cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
            return result;

        Load(result.Value);
        return result;
    }

    public Result AttachCurrent()
    {
        if (!_useCases.IsInitialized)
            return EasyChat.Shared.Results.Result.Failure(
                new Error("settings.not-initialized", "Settings use cases are not initialized."));
        Load(_useCases.Current);
        return EasyChat.Shared.Results.Result.Success();
    }

    public Result FlushSection(SettingsSection section)
    {
        if (_current is null)
            return EasyChat.Shared.Results.Result.Failure(
                new Error("settings.not-initialized", "Settings are not initialized."));

        var next = new SettingsBundle(
            General.ToContract(),
            AiModel.ToContract(),
            MachineTranslation.ToContract(),
            Proxy.ToContract(),
            Shortcut.ToContract(),
            Prompts.ToContract(),
            Result.ToContract(),
            Input.ToContract(),
            Screenshot.ToContract(),
            SpeechRecognition.ToContract(),
            SelectionTranslation.ToContract(),
            Tts.ToContract(),
            TextAssist.ToContract(),
            Ocr.ToContract());

        var update = _useCases.Update(section, next);
        if (update.IsFailure)
            return update;

        _current = next;
        Changed?.Invoke(this, new SettingsChangedEventArgs(section, next));
        return EasyChat.Shared.Results.Result.Success();
    }

    private void Load(SettingsBundle settings)
    {
        _current = settings;
        General = new LiveGeneralSettings(settings.General, FlushSection);
        AiModel = new LiveAiModelSettings(settings.AiModel, FlushSection);
        MachineTranslation = new LiveMachineTranslationSettings(settings.MachineTranslation, FlushSection);
        Proxy = new LiveProxySettings(settings.Proxy, FlushSection);
        Shortcut = new LiveShortcutSettings(settings.Shortcut, FlushSection);
        Prompts = new LivePromptSettings(settings.Prompts, FlushSection);
        Result = new LiveResultSettings(settings.Result, FlushSection);
        Input = new LiveInputSettings(settings.Input, FlushSection);
        Screenshot = new LiveScreenshotSettings(settings.Screenshot, FlushSection);
        SpeechRecognition = new LiveSpeechRecognitionSettings(settings.SpeechRecognition, FlushSection);
        SelectionTranslation = new LiveSelectionTranslationSettings(settings.SelectionTranslation, FlushSection);
        Tts = new LiveTtsSettings(settings.Tts, FlushSection);
        TextAssist = new LiveTextAssistSettings(settings.TextAssist, FlushSection);
        Ocr = new LiveOcrSettings(settings.Ocr, FlushSection);
    }
}
