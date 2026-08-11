using EasyChat.Shared.Results;

namespace EasyChat.Contracts.Settings;

public enum SettingsSection
{
    General,
    AiModel,
    MachineTranslation,
    NetworkProxy,
    Proxy = NetworkProxy,
    Shortcut,
    Prompts,
    Result,
    Input,
    Screenshot,
    SpeechRecognition,
    SelectionTranslation,
    Tts,
    TextAssist,
    Ocr
}

public sealed class SettingsChangedEventArgs(SettingsSection section, SettingsBundle current) : EventArgs
{
    public SettingsSection Section { get; } = section;
    public SettingsBundle Current { get; } = current;
}

public sealed class SettingsSaveFailedEventArgs(SettingsSection section, Error error) : EventArgs
{
    public SettingsSection Section { get; } = section;
    public Error Error { get; } = error;
}

public interface ISettingsUseCases : IAsyncDisposable
{
    event EventHandler<SettingsChangedEventArgs>? SettingsChanged;
    event EventHandler<SettingsSaveFailedEventArgs>? SaveFailed;

    bool IsInitialized { get; }
    SettingsBundle Current { get; }

    ValueTask<Result<SettingsBundle>> InitializeAsync(CancellationToken cancellationToken = default);
    Result Update(SettingsSection section, SettingsBundle settings);
    ValueTask<Result> FlushAsync(CancellationToken cancellationToken = default);
}
