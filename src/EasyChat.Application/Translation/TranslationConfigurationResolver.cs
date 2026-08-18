using EasyChat.Contracts.Settings;

namespace EasyChat.Application.Translation;

/// <summary>
/// Resolves field-level translation references. The Presentation layer stores either
/// a local value or the stable FollowGlobal reference; only Application decides what
/// value is effective for a workflow.
/// </summary>
internal static class TranslationConfigurationResolver
{
    public static bool IsGlobal(string? value) =>
        string.Equals(value, TranslationConfigurationOptionIds.FollowGlobal, StringComparison.Ordinal);

    public static string ResolveProvider(
        string? localProvider,
        GeneralSettings general,
        string fallback) =>
        IsGlobal(localProvider) || string.IsNullOrWhiteSpace(localProvider)
            ? general.TranslationEngine ?? fallback
            : localProvider;

    public static string? ResolveAiModelId(string? localModelId, GeneralSettings general) =>
        IsGlobal(localModelId)
            ? general.AiModelId ?? general.AiModel
            : localModelId;

    public static string ResolveMachineProvider(
        string? localProvider,
        GeneralSettings general,
        string fallback) =>
        IsGlobal(localProvider) || string.IsNullOrWhiteSpace(localProvider)
            ? general.MachineTranslationId ?? general.MachineTranslation ?? fallback
            : localProvider;

    public static string? ResolvePromptId(string? localPromptId, PromptSettings prompts) =>
        IsGlobal(localPromptId) || string.IsNullOrWhiteSpace(localPromptId)
            ? prompts.SelectedPromptId
            : localPromptId;

    public static string ResolveLanguageId(string? localLanguageId, string globalLanguageId) =>
        IsGlobal(localLanguageId) || string.IsNullOrWhiteSpace(localLanguageId)
            ? globalLanguageId
            : localLanguageId;
}
