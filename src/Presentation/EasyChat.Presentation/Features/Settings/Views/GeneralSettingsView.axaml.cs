using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using EasyChat.Contracts.Speech;
using LangResources = EasyChat.Presentation.Lang.Resources;

namespace EasyChat.Presentation.Features.Settings.Views;

public partial class GeneralSettingsView : UserControl
{
    public GeneralSettingsView() => InitializeComponent();

    private async void ImportAsrModelFolder_OnClick(object? sender, RoutedEventArgs args)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null || DataContext is not SettingViewModel viewModel)
            return;

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = LangResources.ImportAsrModelFolder,
            AllowMultiple = false
        });
        if (folders.Count == 1 && folders[0].Path.IsFile)
        {
            await viewModel.ImportAsrModelsAsync(
                folders[0].Path.LocalPath,
                SpeechRecognitionModelImportSourceKind.Directory);
        }
    }

    private async void ImportAsrModelArchive_OnClick(object? sender, RoutedEventArgs args)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null || DataContext is not SettingViewModel viewModel)
            return;

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = LangResources.ImportAsrModelArchive,
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(LangResources.AsrModelArchives)
                {
                    Patterns = ["*.zip", "*.tar", "*.tar.gz", "*.tgz"]
                }
            ]
        });
        if (files.Count == 1 && files[0].Path.IsFile)
        {
            await viewModel.ImportAsrModelsAsync(
                files[0].Path.LocalPath,
                SpeechRecognitionModelImportSourceKind.Archive);
        }
    }
}
