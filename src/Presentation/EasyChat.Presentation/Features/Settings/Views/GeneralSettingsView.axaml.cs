using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using LangResources = EasyChat.Presentation.Lang.Resources;

namespace EasyChat.Presentation.Features.Settings.Views;

public partial class GeneralSettingsView : UserControl
{
    public GeneralSettingsView() => InitializeComponent();

    private async void ChangeApplicationDataLocation_OnClick(object? sender, RoutedEventArgs args)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null || DataContext is not SettingViewModel viewModel)
            return;

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = LangResources.SelectApplicationDataLocation,
            AllowMultiple = false
        });
        var path = folders.FirstOrDefault()?.Path.LocalPath;
        if (!string.IsNullOrWhiteSpace(path))
            await viewModel.ChangeApplicationDataLocationAsync(path);
    }
}
