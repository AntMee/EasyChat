namespace EasyChat.Models.Configuration;

public sealed record TextAssistProfile(
    string SourceLanguageId,
    string TargetLanguageId,
    string Provider,
    string? AiModelId,
    string? MachineProvider,
    bool UsesGlobalConfiguration = false,
    string? PromptId = null);
