using EasyChat.Contracts.Settings;
using EasyChat.Contracts.Translation;

namespace EasyChat.Application.Translation;

internal sealed class TranslationSessionResolver
{
    private readonly ISettingsUseCases _settings;
    private readonly ConfiguredTranslationProviderResolver _providers;

    public TranslationSessionResolver(
        ISettingsUseCases settings,
        ITranslationProviderFactory providerFactory,
        TranslationMessages messages)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _providers = new ConfiguredTranslationProviderResolver(settings, providerFactory, messages);
    }

    public ITranslationSession Create(TranslationProviderSelection? selection)
    {
        var settings = _settings.Current;
        var selected = selection ?? FromDefaults(settings.General);

        if (IsMachineEngine(selected.Engine))
            return CreateMachineSession(selected);

        if (IsAiEngine(selected.Engine))
            return CreateAiSession(selected);

        throw new ArgumentException(
            $"Unknown translation engine: {selected.Engine}",
            nameof(selection));
    }

    private ITranslationSession CreateMachineSession(TranslationProviderSelection selected)
    {
        var resolved = _providers.CreateMachine(
            selected.MachineProviderId,
            selected.MachineProviderName);
        return new MachineTranslationSession(resolved.Provider, resolved.Configuration.Name);
    }

    private ITranslationSession CreateAiSession(TranslationProviderSelection selected)
    {
        var role = ResolvePromptRole(selected);
        var resolved = _providers.CreateSelectedAi(selected.AiModelId, selected.AiModelName);
        return new AiTranslationSession(resolved.Provider, role);
    }

    private string ResolvePromptRole(TranslationProviderSelection selected)
    {
        if (string.IsNullOrWhiteSpace(selected.PromptId))
        {
            return string.IsNullOrWhiteSpace(selected.PromptOverride)
                ? _providers.ResolvePromptRole(null)
                : selected.PromptOverride;
        }

        var role = _providers.ResolvePromptRole(selected.PromptId);
        return string.IsNullOrWhiteSpace(selected.PromptOverride)
            ? role
            : role + "\n\n" + selected.PromptOverride;
    }

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
