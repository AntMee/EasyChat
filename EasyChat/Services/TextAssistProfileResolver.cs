using System;
using System.Linq;
using EasyChat.Constants;
using EasyChat.Models.Configuration;
using EasyChat.Services.Abstractions;

namespace EasyChat.Services;

public sealed class TextAssistProfileResolver
{
    private readonly IConfigurationService _configurationService;

    public TextAssistProfileResolver(IConfigurationService configurationService)
    {
        _configurationService = configurationService;
    }

    public TextAssistProfile Resolve(bool correction = false)
    {
        var general = _configurationService.General
                      ?? throw new InvalidOperationException("General configuration is unavailable.");
        var config = _configurationService.TextAssist
                     ?? throw new InvalidOperationException("Text assist configuration is unavailable.");

        if (config.FollowGlobal)
        {
            var provider = general.TransEngine ?? TextAssistConstants.AiProvider;
            var promptId = correction ? config.CorrectionPromptId : config.TranslationPromptId;
            return new TextAssistProfile(
                general.SourceLanguage?.Id ?? "auto",
                general.TargetLanguage?.Id ?? "zh-Hans",
                correction ? TextAssistConstants.AiProvider : provider,
                general.UsingAiModelId,
                general.UsingMachineTransId ?? general.UsingMachineTrans,
                true,
                ResolvePromptId(promptId),
                !correction && config.DetailedExplanation &&
                provider.Equals(TextAssistConstants.AiProvider, StringComparison.OrdinalIgnoreCase));
        }

        var models = _configurationService.AiModel?.ConfiguredModels;
        var selectedModel = models == null ? null : models.FirstOrDefault(x => x.Id == config.AiModelId);
        if (models != null && selectedModel == null)
        {
            selectedModel = models.FirstOrDefault();
            config.AiModelId = selectedModel?.Id;
        }

        var aiModelId = models == null ? config.AiModelId : selectedModel?.Id;

        return new TextAssistProfile(
            config.SourceLanguageId,
            config.TargetLanguageId,
            correction ? TextAssistConstants.AiProvider : config.Provider,
            aiModelId,
            config.MachineProvider,
            false,
            ResolvePromptId(correction ? config.CorrectionPromptId : config.TranslationPromptId),
            !correction && config.DetailedExplanation &&
            config.Provider.Equals(TextAssistConstants.AiProvider, StringComparison.OrdinalIgnoreCase));
    }

    private string? ResolvePromptId(string? promptId)
    {
        if (!string.IsNullOrWhiteSpace(promptId) && _configurationService.Prompts?.FindById(promptId) != null)
            return promptId;

        return string.IsNullOrWhiteSpace(_configurationService.Prompts?.SelectedPromptId)
            ? null
            : _configurationService.Prompts.SelectedPromptId;
    }
}
