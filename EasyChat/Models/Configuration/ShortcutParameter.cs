using EasyChat.Services.Languages;
using Newtonsoft.Json;

namespace EasyChat.Models.Configuration;

public class ShortcutParameter
{
    [JsonProperty]
    public string Engine { get; set; } = string.Empty;

    [JsonProperty]
    public string? EngineId { get; set; }

    [JsonProperty]
    public LanguageDefinition? Source { get; set; }

    [JsonProperty]
    public LanguageDefinition? Target { get; set; }

    [JsonProperty]
    public string? Value { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public bool? ReadSelectedText { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? InputTranslateBeforeKey { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? InputTranslateAfterKey { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public bool? ReplaceCurrentInput { get; set; }
}
