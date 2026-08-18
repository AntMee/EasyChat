using Newtonsoft.Json;
using EasyChat.Contracts.Settings;

namespace EasyChat.Infrastructure.Settings.Persistence;

internal enum ClosingBehaviorDto
{
    Ask = 0,
    ExitApp = 1,
    MinimizeToTray = 2
}

internal enum AiModelTypeDto
{
    OpenAi = 0,
    Gemini = 1,
    Claude = 2,
    DeepSeek = 3,
    Custom = 4,
    Qwen = 5,
    Zhipu = 6,
    Moonshot = 7,
    Doubao = 8,
    MiniMax = 9,
    Hunyuan = 10,
    Grok = 11,
    Mistral = 12,
    Qianfan = 13,
    Spark = 14,
    StepFun = 15,
    ModelScope = 16,
    SiliconFlow = 17,
    XiaomiMimo = 18,
    OpenRouter = 19,
    Together = 20,
    Fireworks = 21,
    Groq = 22,
    Cerebras = 23,
    DeepInfra = 24,
    NvidiaNim = 25
}

[JsonObject(MemberSerialization.OptIn)]
internal sealed class LanguageSettingsDto
{
    [JsonProperty]
    public string Id { get; set; } = string.Empty;

    [JsonProperty]
    public string ChineseName { get; set; } = string.Empty;

    [JsonProperty]
    public string EnglishName { get; set; } = string.Empty;

    [JsonProperty]
    public string Icon { get; set; } = string.Empty;

    [JsonIgnore]
    public string LocalizedName { get; set; } = string.Empty;

    [JsonIgnore]
    public string DisplayName { get; set; } = string.Empty;

    [JsonProperty]
    public Dictionary<string, string> ProviderCodes { get; set; } = new();
}

[JsonObject(MemberSerialization.OptIn)]
internal sealed class GeneralSettingsDto
{
    private string? _displayLanguage;
    private string? _transEngine = "AiModel";
    private string? _usingAiModel = "OpenAI";
    private string _baseTheme = "Default";

    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public LanguageSettingsDto SourceLanguage { get; set; } =
        SettingsDefaults.CreateSourceLanguage();

    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public LanguageSettingsDto TargetLanguage { get; set; } =
        SettingsDefaults.CreateTargetLanguage();

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? DisplayLanguage
    {
        get => _displayLanguage ?? GetSystemDisplayLanguage();
        set => _displayLanguage = string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public bool ShouldSerializeDisplayLanguage() => _displayLanguage is not null;

    [JsonProperty("Language")]
    private string? PreviousLanguage
    {
        set
        {
            if (_displayLanguage is null && !string.IsNullOrWhiteSpace(value))
                DisplayLanguage = value;
        }
    }

    private static string GetSystemDisplayLanguage() =>
        string.Equals(
            System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName,
            "zh",
            StringComparison.OrdinalIgnoreCase)
            ? "Simplified Chinese"
            : "English";

    [JsonProperty]
    public LanguageSettingsDto? NativeLanguage { get; set; }

    [JsonProperty]
    public ClosingBehaviorDto ClosingBehavior { get; set; }

    [JsonProperty]
    public string? TransEngine
    {
        get => _transEngine ?? "AiModel";
        set => _transEngine = value ?? "AiModel";
    }

    [JsonProperty]
    public string? UsingAiModel
    {
        get => _usingAiModel ?? "OpenAI";
        set => _usingAiModel = value ?? "OpenAI";
    }

    [JsonProperty]
    public string? UsingAiModelId { get; set; }

    [JsonProperty]
    public string? UsingMachineTransId { get; set; }

    [JsonProperty]
    public string? UsingMachineTrans { get; set; }

    [JsonProperty]
    public string BaseTheme
    {
        get => _baseTheme ?? "Default";
        set => _baseTheme = value ?? "Default";
    }

    [JsonProperty]
    // Keep the current blue palette as the default for new and legacy settings.
    // Json.NET leaves an initialized property untouched when older files omit it.
    public string? ColorTheme { get; set; } = "builtin:blue";

    [JsonProperty]
    public string? CustomThemePrimaryColor { get; set; }

    [JsonProperty]
    public string? CustomThemeAccentColor { get; set; }

    [JsonProperty]
    public List<CustomColorThemeSettingsDto> CustomColorThemes { get; set; } = [];

    [JsonProperty]
    public bool TitleBarVisible { get; set; } = true;

    [JsonProperty]
    public bool FullScreen { get; set; }
}

[JsonObject(MemberSerialization.OptIn)]
internal sealed class CustomColorThemeSettingsDto
{
    [JsonProperty]
    public string Id { get; set; } = string.Empty;

    [JsonProperty]
    public string DisplayName { get; set; } = string.Empty;

    [JsonProperty]
    public string PrimaryColor { get; set; } = string.Empty;

    [JsonProperty]
    public string AccentColor { get; set; } = string.Empty;
}

[JsonObject(MemberSerialization.OptIn)]
internal sealed class AiModelSettingsDto
{
    [JsonProperty]
    public List<CustomAiModelSettingsDto> ConfiguredModels { get; set; } = [];
}

[JsonObject(MemberSerialization.OptIn)]
internal sealed class CustomAiModelSettingsDto
{
    [JsonProperty]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [JsonProperty]
    public string Name { get; set; } = string.Empty;

    [JsonProperty]
    public AiModelTypeDto ModelType { get; set; } = AiModelTypeDto.Custom;

    [JsonProperty]
    public List<string> ApiKeys { get; set; } = [];

    [JsonProperty]
    public string ApiUrl { get; set; } = string.Empty;

    [JsonProperty]
    public string Model { get; set; } = string.Empty;

    [JsonProperty]
    public bool UseProxy { get; set; }

    [JsonProperty]
    public bool EnableThinking { get; set; }
}

[JsonObject(MemberSerialization.OptIn)]
internal sealed class MachineTranslationSettingsDto
{
    [JsonProperty]
    public BaiduTranslationSettingsDto Baidu { get; set; } = new();

    [JsonProperty]
    public TencentTranslationSettingsDto Tencent { get; set; } = new();

    [JsonProperty]
    public GoogleTranslationSettingsDto Google { get; set; } = new();

    [JsonProperty]
    public DeepLTranslationSettingsDto DeepL { get; set; } = new();
}

[JsonObject(MemberSerialization.OptIn)]
internal sealed class BaiduTranslationSettingsDto
{
    [JsonProperty]
    public bool UseProxy { get; set; }

    [JsonProperty]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [JsonProperty]
    public List<BaiduCredentialSettingsDto> Items { get; set; } = [];
}

[JsonObject(MemberSerialization.OptIn)]
internal sealed class BaiduCredentialSettingsDto
{
    [JsonProperty]
    public string AppId { get; set; } = string.Empty;

    [JsonProperty]
    public string AppKey { get; set; } = string.Empty;
}

[JsonObject(MemberSerialization.OptIn)]
internal sealed class TencentTranslationSettingsDto
{
    [JsonProperty]
    public bool UseProxy { get; set; }

    [JsonProperty]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [JsonProperty]
    public List<TencentCredentialSettingsDto> Items { get; set; } = [];
}

[JsonObject(MemberSerialization.OptIn)]
internal sealed class TencentCredentialSettingsDto
{
    [JsonProperty]
    public string SecretId { get; set; } = string.Empty;

    [JsonProperty]
    public string SecretKey { get; set; } = string.Empty;
}

[JsonObject(MemberSerialization.OptIn)]
internal sealed class GoogleTranslationSettingsDto
{
    [JsonProperty]
    public bool UseProxy { get; set; }

    [JsonProperty]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [JsonProperty]
    public string Model { get; set; } = "nmt";

    [JsonProperty]
    public List<string> ApiKeys { get; set; } = [];
}

[JsonObject(MemberSerialization.OptIn)]
internal sealed class DeepLTranslationSettingsDto
{
    [JsonProperty]
    public bool UseProxy { get; set; }

    [JsonProperty]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [JsonProperty]
    public string ModelType { get; set; } = "latency_optimized";

    [JsonProperty]
    public List<string> ApiKeys { get; set; } = [];
}

[JsonObject(MemberSerialization.OptIn)]
internal sealed class ProxySettingsDto
{
    [JsonProperty]
    public NetworkProxyMode? Mode { get; set; }

    [JsonProperty]
    public string ProxyUrl { get; set; } = string.Empty;
}
