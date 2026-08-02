using EasyChat.Contracts.Settings;
using EasyChat.Contracts.Translation;

namespace EasyChat.Application.Translation;

internal sealed class TranslationSessionResolver
{
    private readonly ISettingsUseCases _settings;
    private readonly ITranslationProviderFactory _providerFactory;
    private readonly TranslationMessages _messages;

    public TranslationSessionResolver(
        ISettingsUseCases settings,
        ITranslationProviderFactory providerFactory,
        TranslationMessages messages)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _providerFactory = providerFactory ?? throw new ArgumentNullException(nameof(providerFactory));
        _messages = messages ?? throw new ArgumentNullException(nameof(messages));
    }

    public ITranslationSession Create(TranslationProviderSelection? selection)
    {
        var settings = _settings.Current;
        var selected = selection ?? FromDefaults(settings.General);

        if (IsMachineEngine(selected.Engine))
            return CreateMachineSession(settings, selected);

        if (IsAiEngine(selected.Engine))
            return CreateAiSession(settings, selected);

        throw new ArgumentException(
            $"Unknown translation engine: {selected.Engine}",
            nameof(selection));
    }

    private ITranslationSession CreateMachineSession(
        SettingsBundle settings,
        TranslationProviderSelection selected)
    {
        var configuration = ResolveMachineProvider(settings, selected)
                            ?? throw new ArgumentException(
                                "A valid machine translation provider is required.",
                                "selection");
        var provider = _providerFactory.Create(new MachineTranslationProviderOptions(
            configuration,
            ResolveProxyUrl(settings.Proxy, configuration.UseProxy),
            _messages.RequestError));
        return new MachineTranslationSession(provider, configuration.Name);
    }

    private ITranslationSession CreateAiSession(
        SettingsBundle settings,
        TranslationProviderSelection selected)
    {
        var configuration = ResolveAiProvider(settings, selected)
                            ?? throw new ArgumentException(
                                "A valid AI model is required.",
                                "selection");
        var prompt = string.IsNullOrWhiteSpace(selected.PromptOverride)
            ? ResolveActivePrompt(settings.Prompts)
            : selected.PromptOverride;
        var provider = _providerFactory.Create(new AiTranslationProviderOptions(
            configuration,
            ResolveProxyUrl(settings.Proxy, configuration.UseProxy)));
        return new AiTranslationSession(provider, prompt);
    }

    private static AiTranslationProviderConfiguration? ResolveAiProvider(
        SettingsBundle settings,
        TranslationProviderSelection selection)
    {
        var model = !string.IsNullOrWhiteSpace(selection.AiModelId)
            ? settings.AiModel.ConfiguredModels.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, selection.AiModelId, StringComparison.Ordinal))
            : !string.IsNullOrWhiteSpace(selection.AiModelName)
                ? settings.AiModel.ConfiguredModels.FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, selection.AiModelName, StringComparison.Ordinal))
                : null;

        return model is null
            ? null
            : new AiTranslationProviderConfiguration(
                model.Id,
                model.Name,
                model.ModelType.ToString(),
                model.ApiUrl,
                SelectRandom(model.ApiKeys) ?? string.Empty,
                model.Model,
                model.UseProxy,
                model.EnableThinking);
    }

    private static MachineTranslationProviderConfiguration? ResolveMachineProvider(
        SettingsBundle settings,
        TranslationProviderSelection selection)
    {
        var machine = settings.MachineTranslation;
        if (!string.IsNullOrWhiteSpace(selection.MachineProviderId))
        {
            var id = selection.MachineProviderId;
            if (string.Equals(machine.Baidu.Id, id, StringComparison.Ordinal))
                return Map(machine.Baidu);
            if (string.Equals(machine.Tencent.Id, id, StringComparison.Ordinal))
                return Map(machine.Tencent);
            if (string.Equals(machine.Google.Id, id, StringComparison.Ordinal))
                return Map(machine.Google);
            if (string.Equals(machine.DeepL.Id, id, StringComparison.Ordinal))
                return Map(machine.DeepL);
            return null;
        }

        return selection.MachineProviderName switch
        {
            MachineTranslationProviderNames.Baidu => Map(machine.Baidu),
            MachineTranslationProviderNames.Tencent => Map(machine.Tencent),
            MachineTranslationProviderNames.Google => Map(machine.Google),
            MachineTranslationProviderNames.DeepL => Map(machine.DeepL),
            _ => null
        };
    }

    private static BaiduTranslationProviderConfiguration Map(
        BaiduTranslationSettings provider)
    {
        var credentials = SelectRandom(provider.Items);
        return new BaiduTranslationProviderConfiguration(
            provider.Id,
            provider.UseProxy,
            credentials?.AppId ?? string.Empty,
            credentials?.AppKey ?? string.Empty);
    }

    private static TencentTranslationProviderConfiguration Map(
        TencentTranslationSettings provider)
    {
        var credentials = SelectRandom(provider.Items);
        return new TencentTranslationProviderConfiguration(
            provider.Id,
            provider.UseProxy,
            credentials?.SecretId ?? string.Empty,
            credentials?.SecretKey ?? string.Empty);
    }

    private static GoogleTranslationProviderConfiguration Map(
        GoogleTranslationSettings provider) => new(
        provider.Id,
        provider.UseProxy,
        provider.Model,
        SelectRandom(provider.ApiKeys) ?? string.Empty);

    private static DeepLTranslationProviderConfiguration Map(
        DeepLTranslationSettings provider) => new(
        provider.Id,
        provider.UseProxy,
        provider.ModelType,
        SelectRandom(provider.ApiKeys) ?? string.Empty);

    private static string ResolveActivePrompt(PromptSettings prompts)
    {
        if (!string.IsNullOrEmpty(prompts.SelectedPromptId))
        {
            var selected = prompts.Entries.FirstOrDefault(prompt =>
                string.Equals(prompt.Id, prompts.SelectedPromptId, StringComparison.Ordinal));
            if (selected is not null)
                return selected.Content;
        }

        return prompts.Entries.FirstOrDefault(prompt => prompt.IsDefault)?.Content
               ?? TranslationPromptDefaults.DefaultContent;
    }

    private static string? ResolveProxyUrl(ProxySettings proxy, bool useProxy) =>
        useProxy && !string.IsNullOrEmpty(proxy.ProxyUrl) ? proxy.ProxyUrl : null;

    private static T? SelectRandom<T>(IReadOnlyList<T> items) where T : class =>
        items.Count == 0 ? null : items[Random.Shared.Next(items.Count)];

    private static TranslationProviderSelection FromDefaults(GeneralSettings settings) => new(
        settings.TranslationEngine ?? string.Empty,
        settings.AiModelId,
        settings.AiModel,
        settings.MachineTranslationId,
        settings.MachineTranslation);

    private static bool IsMachineEngine(string engine) =>
        string.Equals(engine, TranslationEngineNames.MachineTrans, StringComparison.OrdinalIgnoreCase)
        || string.Equals(engine, "Machine", StringComparison.OrdinalIgnoreCase);

    private static bool IsAiEngine(string engine) =>
        string.Equals(engine, TranslationEngineNames.AiModel, StringComparison.OrdinalIgnoreCase)
        || string.Equals(engine, "AI", StringComparison.OrdinalIgnoreCase);
}
