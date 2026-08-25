using Avalonia.Threading;
using EasyChat.Contracts.Input;
using EasyChat.Contracts.Platform;
using EasyChat.Contracts.Settings;
using EasyChat.Presentation.Features.Settings.State;
using EasyChat.Presentation.Foundation.Localization;
using EasyChat.Presentation.Features.Input;
using EasyChat.Presentation.Features.Input.Views;
using EasyChat.Presentation.Features.Translation;
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
    ITranslationWindowCoordinator translationWindows,
    ILoggerFactory loggerFactory) : ITypingWindowFactory
{
    public void Show(ExternalTargetToken target, ShortcutParameterSettings? shortcut = null)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var viewModel = new TypingViewModel(
                target,
                shortcut,
                settings,
                languages,
                inputTranslation,
                translationWindows,
                loggerFactory.CreateLogger<TypingViewModel>());
            new TypingView(viewModel, settings).Show();
        });
    }
}
