using Newtonsoft.Json;
using ReactiveUI;

namespace EasyChat.Models.Configuration;

[JsonObject(MemberSerialization.OptIn)]
public sealed class TextAssistConfig : ReactiveObject
{
    [JsonProperty]
    public bool FollowGlobal
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = true;

    [JsonProperty]
    public string SourceLanguageId
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "auto";

    [JsonProperty]
    public string TargetLanguageId
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "zh-Hans";

    [JsonProperty]
    public string Provider
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "AiModel";

    [JsonProperty]
    public string? AiModelId
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    [JsonProperty]
    public string? TranslationPromptId
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    [JsonProperty]
    public string? CorrectionPromptId
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    [JsonProperty]
    public string MachineProvider
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "Baidu";
}
