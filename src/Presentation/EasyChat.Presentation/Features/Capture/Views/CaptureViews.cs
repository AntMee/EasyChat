using Avalonia.Controls;
using Avalonia.Interactivity;
using EasyChat.Presentation.Features.Settings.State;
using EasyChat.ViewModels.Dialogs;

namespace EasyChat.Views.Dialogs
{
    public partial class FixedAreaEditDialogView : UserControl
    {
        public FixedAreaEditDialogView() => InitializeComponent();
        private void EditButton_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is Control { DataContext: FixedAreaState area } &&
                DataContext is FixedAreaEditDialogViewModel viewModel)
                viewModel.EditArea(area);
        }

        private void DeleteButton_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is Control { DataContext: FixedAreaState area } &&
                DataContext is FixedAreaEditDialogViewModel viewModel)
                viewModel.DeleteArea(area);
        }
    }

    public partial class FixedAreaFormDialogView : UserControl
    {
        public FixedAreaFormDialogView() => InitializeComponent();
    }
}
