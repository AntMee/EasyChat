using EasyChat.Contracts.Settings;
using EasyChat.Contracts.Translation;

namespace EasyChat.Application.Translation;

internal sealed class ConfiguredTranslationProviderResolver
{
    private readonly ISettingsUseCases _settings;
    private readonly ITranslationProviderFactory _factory;
    private readonly TranslationMessages _messages;

    public ConfiguredTranslationProviderResolver(
        ISettingsUseCases settings,
        ITranslationProviderFactory factory,
        TranslationMessages messages)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _messages = messages ?? throw new ArgumentNullException(nameof(messages));
    }

    public ResolvedAiTranslationProvider CreateSelectedAi(string? id, string? name)
    {
        var settings = _settings.Current;
        var configuration = ResolveAi(settings, id, name)
                            ?? throw new ArgumentException("A valid AI model is required.");
        return CreateAi(settings, configuration);
    }

    public ResolvedAiTranslationProvider CreatePreferredAi(
        string? preferredId,
        bool useGlobalFallback,
        bool useFirstFallback)
    {
        var settings = _settings.Current;
        var configuration = ResolveAi(settings, preferredId, null);
        if (configuration is null && useGlobalFallback)
        {
            configuration = ResolveAi(
                settings,
                settings.General.AiModelId,
                settings.General.AiModel);
        }
        if (configuration is null && useFirstFallback)
            configuration = settings.AiModel.ConfiguredModels.FirstOrDefault();
        if (configuration is null)
            throw new InvalidOperationException("No active AI model configured");

        return CreateAi(settings, configuration);
    }

    public ResolvedMachineTranslationProvider CreateMachine(string? id, string? name)
    {
        var settings = _settings.Current;
        var configuration = ResolveMachine(settings, id, name)
                            ?? throw new ArgumentException("A valid machine translation provider is required.");
        var provider = _factory.Create(new MachineTranslationProviderOptions(
            configuration,
            ResolveProxyUrl(settings.Proxy, configuration.UseProxy),
            _messages.RequestError));
        return new ResolvedMachineTranslationProvider(provider, configuration);
    }

    public string ResolvePrompt(string? promptId)
    {
        var prompts = _settings.Current.Prompts;
        return ResolveOptionalPrompt(promptId)
               ?? prompts.Entries.FirstOrDefault(prompt => prompt.IsDefault)?.Content
               ?? TranslationPromptDefaults.DefaultContent;
    }

    public string? ResolveOptionalPrompt(string? promptId)
    {
        var prompts = _settings.Current.Prompts;
        if (!string.IsNullOrWhiteSpace(promptId))
        {
            var requested = prompts.Entries.FirstOrDefault(prompt =>
                string.Equals(prompt.Id, promptId, StringComparison.Ordinal));
            if (requested is not null)
                return requested.Content;
        }

        return string.IsNullOrEmpty(prompts.SelectedPromptId)
            ? null
            : prompts.Entries.FirstOrDefault(prompt =>
                string.Equals(prompt.Id, prompts.SelectedPromptId, StringComparison.Ordinal))?.Content;
    }

    private ResolvedAiTranslationProvider CreateAi(
        SettingsBundle settings,
        CustomAiModelSettings model)
    {
        var configuration = Map(model);
        var provider = _factory.Create(new AiTranslationProviderOptions(
            configuration,
            ResolveProxyUrl(settings.Proxy, configuration.UseProxy)));
        return new ResolvedAiTranslationProvider(provider, configuration);
    }

    private static CustomAiModelSettings? ResolveAi(
        SettingsBundle settings,
        string? id,
        string? name)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            var byId = settings.AiModel.ConfiguredModels.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, id, StringComparison.Ordinal));
            if (byId is not null)
                return byId;
        }

        return string.IsNullOrWhiteSpace(name)
            ? null
            : settings.AiModel.ConfiguredModels.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, name, StringComparison.Ordinal));
    }

    private static MachineTranslationProviderConfiguration? ResolveMachine(
        SettingsBundle settings,
        string? id,
        string? name)
    {
        var machine = settings.MachineTranslation;
        if (!string.IsNullOrWhiteSpace(id))
        {
            if (string.Equals(machine.Baidu.Id, id, StringComparison.Ordinal)) return Map(machine.Baidu);
            if (string.Equals(machine.Tencent.Id, id, StringComparison.Ordinal)) return Map(machine.Tencent);
            if (string.Equals(machine.Google.Id, id, StringComparison.Ordinal)) return Map(machine.Google);
            if (string.Equals(machine.DeepL.Id, id, StringComparison.Ordinal)) return Map(machine.DeepL);
            return null;
        }

        return name switch
        {
            MachineTranslationProviderNames.Baidu => Map(machine.Baidu),
            MachineTranslationProviderNames.Tencent => Map(machine.Tencent),
            MachineTranslationProviderNames.Google => Map(machine.Google),
            MachineTranslationProviderNames.DeepL => Map(machine.DeepL),
            _ => null
        };
    }

    private static AiTranslationProviderConfiguration Map(CustomAiModelSettings model) => new(
        model.Id,
        model.Name,
        model.ModelType.ToString(),
        model.ApiUrl,
        SelectRandom(model.ApiKeys) ?? string.Empty,
        model.Model,
        model.UseProxy,
        model.EnableThinking);

    private static BaiduTranslationProviderConfiguration Map(BaiduTranslationSettings provider)
    {
        var credentials = SelectRandom(provider.Items);
        return new BaiduTranslationProviderConfiguration(
            provider.Id,
            provider.UseProxy,
            credentials?.AppId ?? string.Empty,
            credentials?.AppKey ?? string.Empty);
    }

    private static TencentTranslationProviderConfiguration Map(TencentTranslationSettings provider)
    {
        var credentials = SelectRandom(provider.Items);
        return new TencentTranslationProviderConfiguration(
            provider.Id,
            provider.UseProxy,
            credentials?.SecretId ?? string.Empty,
            credentials?.SecretKey ?? string.Empty);
    }

    private static GoogleTranslationProviderConfiguration Map(GoogleTranslationSettings provider) => new(
        provider.Id,
        provider.UseProxy,
        provider.Model,
        SelectRandom(provider.ApiKeys) ?? string.Empty);

    private static DeepLTranslationProviderConfiguration Map(DeepLTranslationSettings provider) => new(
        provider.Id,
        provider.UseProxy,
        provider.ModelType,
        SelectRandom(provider.ApiKeys) ?? string.Empty);

    private static string? ResolveProxyUrl(ProxySettings proxy, bool useProxy) =>
        useProxy && !string.IsNullOrEmpty(proxy.ProxyUrl) ? proxy.ProxyUrl : null;

    private static T? SelectRandom<T>(IReadOnlyList<T> items) where T : class =>
        items.Count == 0 ? null : items[Random.Shared.Next(items.Count)];
}

internal sealed record ResolvedAiTranslationProvider(
    IChatTranslationProvider Provider,
    AiTranslationProviderConfiguration Configuration);

internal sealed record ResolvedMachineTranslationProvider(
    ITranslationProvider Provider,
    MachineTranslationProviderConfiguration Configuration);
