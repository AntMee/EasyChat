using System;
using EasyChat.Constants;
using EasyChat.Services.Languages;
using System.Globalization;
using Newtonsoft.Json;
using ReactiveUI;

namespace EasyChat.Models.Configuration;

[JsonObject(MemberSerialization.OptIn)]
public class General : ReactiveObject
{
    private string? _displayLanguage;

    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public LanguageDefinition SourceLanguage
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = LanguageService.GetLanguage("auto");

    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public LanguageDefinition TargetLanguage
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = LanguageService.GetLanguage("zh-Hans");


    [JsonProperty]
    public string? DisplayLanguage
    {
        get => _displayLanguage ?? GetSystemDisplayLanguage();
        set => this.RaiseAndSetIfChanged(
            ref _displayLanguage,
            string.IsNullOrWhiteSpace(value) ? null : value);
    }

    // Keep an unset preference dynamic so a new installation follows the OS language.
    public bool ShouldSerializeDisplayLanguage() => _displayLanguage != null;

    private static string GetSystemDisplayLanguage()
    {
        return string.Equals(
            CultureInfo.CurrentUICulture.TwoLetterISOLanguageName,
            "zh",
            StringComparison.OrdinalIgnoreCase)
            ? "Simplified Chinese"
            : "English";
    }

    // Retain the old serialized field so existing installations keep their UI language.
    [JsonProperty("Language")]
    private string? LegacyLanguage
    {
        set
        {
            if (string.IsNullOrWhiteSpace(_displayLanguage) && !string.IsNullOrWhiteSpace(value))
                DisplayLanguage = value;
        }
    }

    [JsonProperty]
    public LanguageDefinition? NativeLanguage
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    [JsonProperty]
    public WindowClosingBehavior ClosingBehavior
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = WindowClosingBehavior.Ask;

    [JsonProperty]
    public string? TransEngine
    {
        get => field ?? "AiModel";
        set
        {
            var newValue = value ?? "AiModel";
            this.RaiseAndSetIfChanged(ref field, newValue);
        }
    } = Constant.TransEngineType.Ai;

    [JsonProperty]
    public string? UsingAiModel
    {
        get => field ?? "OpenAI";
        set => this.RaiseAndSetIfChanged(ref field, value ?? "OpenAI");
    } = "OpenAI";

    [JsonProperty]
    public string? UsingAiModelId
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    [JsonProperty]
    public string? UsingMachineTransId
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    [JsonProperty]
    public string? UsingMachineTrans
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    [JsonProperty]
    public string BaseTheme
    {
        get => field ?? "Light";
        set => this.RaiseAndSetIfChanged(ref field, value ?? "Light");
    } = "Light";

    [JsonProperty]
    public string? ColorTheme
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    [JsonProperty]
    public string? CustomThemePrimaryColor
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    [JsonProperty]
    public string? CustomThemeAccentColor
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    [JsonProperty]
    public bool TitleBarVisible
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = true;

    [JsonProperty]
    public bool FullScreen
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }
}
