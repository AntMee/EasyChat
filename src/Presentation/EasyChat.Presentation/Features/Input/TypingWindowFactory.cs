using Avalonia.Threading;
using EasyChat.Contracts.Input;
using EasyChat.Contracts.Platform;
using EasyChat.Contracts.Settings;
using EasyChat.Presentation.Features.Settings.State;
using EasyChat.Presentation.Foundation.Localization;
using EasyChat.ViewModels.Typing;
using EasyChat.Views.Typing;
using Microsoft.Extensions.Logging;

namespace EasyChat.Presentation.Features.Input;

public interface ITypingWindowFactory
{
    void Show(ExternalTargetToken target, ShortcutParameterSettings? shortcut = null);
}

public sealed class TypingWindowFactory(
    SettingsSession settings,
    TranslationLanguageOptions languages,
    IInputTranslationUseCases inputTranslation,
    ILoggerFactory loggerFactory) : ITypingWindowFactory
{
    public void Show(ExternalTargetToken target, ShortcutParameterSettings? shortcut = null) =>
        Dispatcher.UIThread.Post(() =>
        {
            var viewModel = new TypingViewModel(
                target,
                shortcut,
                settings,
                languages,
                inputTranslation,
                loggerFactory.CreateLogger<TypingViewModel>());
            new TypingView(viewModel, settings).Show();
        });
}
