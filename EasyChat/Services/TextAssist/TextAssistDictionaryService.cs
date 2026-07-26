using System;
using System.Threading.Tasks;
using EasyChat.Views.Windows;

namespace EasyChat.Services.TextAssist;

public sealed class TextAssistDictionaryService : ITextAssistDictionaryService
{
    public async Task OpenAsync(string text, string sourceLanguageId, string targetLanguageId)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        var view = new TranslationDictionaryWindowView
        {
            WindowStartupLocation = Avalonia.Controls.WindowStartupLocation.CenterScreen
        };
        view.Show();
        await view.InitializeAsync(text, sourceLanguageId, targetLanguageId);
    }
}
