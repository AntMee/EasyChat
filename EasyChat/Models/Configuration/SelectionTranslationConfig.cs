using Newtonsoft.Json;
using ReactiveUI;

namespace EasyChat.Models.Configuration;

[JsonObject(MemberSerialization.OptIn)]
public class SelectionTranslationConfig : ReactiveObject
{
    private bool _enabled;

    private string _provider = "AI";
    private string? _aiModelId;
    private string? _machineProvider;
    private SelectionTriggerMode _triggerMode = SelectionTriggerMode.All;
    private bool _translationEnabled = true;
    private bool _correctionEnabled;
    private bool _polishEnabled;
    private bool _summaryEnabled;

    [JsonProperty]
    public bool Enabled
    {
        get => _enabled;
        set => this.RaiseAndSetIfChanged(ref _enabled, value);
    }

    [JsonProperty]
    public string Provider
    {
        get => _provider;
        set => this.RaiseAndSetIfChanged(ref _provider, value);
    }

    [JsonProperty]
    public string? MachineProvider
    {
        get => _machineProvider;
        set => this.RaiseAndSetIfChanged(ref _machineProvider, value);
    }

    [JsonProperty]
    public string? AiModelId
    {
        get => _aiModelId;
        set => this.RaiseAndSetIfChanged(ref _aiModelId, value);
    }

    [JsonProperty]
    public string? PromptId
    {
        get => field;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    [JsonProperty]
    public SelectionTriggerMode TriggerMode
    {
        get => _triggerMode;
        set => this.RaiseAndSetIfChanged(ref _triggerMode, value);
    }

    [JsonProperty]
    public bool TranslationEnabled
    {
        get => _translationEnabled;
        set => this.RaiseAndSetIfChanged(ref _translationEnabled, value);
    }

    [JsonProperty]
    public bool CorrectionEnabled
    {
        get => _correctionEnabled;
        set => this.RaiseAndSetIfChanged(ref _correctionEnabled, value);
    }

    [JsonProperty]
    public bool PolishEnabled
    {
        get => _polishEnabled;
        set => this.RaiseAndSetIfChanged(ref _polishEnabled, value);
    }

    [JsonProperty]
    public bool SummaryEnabled
    {
        get => _summaryEnabled;
        set => this.RaiseAndSetIfChanged(ref _summaryEnabled, value);
    }
}

public enum SelectionTriggerMode
{
    DoubleClick,
    DragSelection,
    All
}
