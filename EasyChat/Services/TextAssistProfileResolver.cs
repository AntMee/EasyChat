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
            return new TextAssistProfile(
                general.SourceLanguage?.Id ?? "auto",
                general.TargetLanguage?.Id ?? "zh-Hans",
                correction ? TextAssistConstants.AiProvider : provider,
                general.UsingAiModelId,
                general.UsingMachineTransId ?? general.UsingMachineTrans,
                true,
                config.TranslationPromptId);
        }

        var aiModelId = config.AiModelId;
        if (string.IsNullOrWhiteSpace(aiModelId))
        {
            aiModelId = _configurationService.AiModel?.ConfiguredModels.FirstOrDefault()?.Id;
            if (!string.IsNullOrWhiteSpace(aiModelId))
                config.AiModelId = aiModelId;
        }

        return new TextAssistProfile(
            config.SourceLanguageId,
            config.TargetLanguageId,
            correction ? TextAssistConstants.AiProvider : config.Provider,
            aiModelId,
            config.MachineProvider,
            false,
            correction ? config.CorrectionPromptId : config.TranslationPromptId);
    }
}
