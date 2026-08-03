using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using EasyChat.Presentation.Features.Shortcuts;

namespace EasyChat.Presentation.Features.Shortcuts.Views
{
    public partial class ShortcutView : UserControl
    {
        public ShortcutView() => InitializeComponent();
    }
}

namespace EasyChat.Presentation.Features.Shortcuts.Views
{
    public partial class ShortcutEditDialogView : UserControl
    {
        public ShortcutEditDialogView()
        {
            InitializeComponent();
            Focusable = true;
            AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
            AddHandler(KeyUpEvent, OnKeyUp, RoutingStrategies.Tunnel);
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);
            if (DataContext is ShortcutEditDialogViewModel viewModel)
            {
                viewModel.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName is nameof(ShortcutEditDialogViewModel.IsRecording)
                        or nameof(ShortcutEditDialogViewModel.IsRecordingBeforeInputKey)
                        or nameof(ShortcutEditDialogViewModel.IsRecordingAfterInputKey))
                    {
                        Dispatcher.UIThread.Post(() => Focus());
                    }
                };
            }
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            Focus();
        }

        private void OnSelectionToolbarInfoPointerEntered(object? sender, PointerEventArgs e)
        {
            if (sender is Control control)
                control.SetValue(ToolTip.IsOpenProperty, true);
        }

        private void OnSelectionToolbarInfoPointerExited(object? sender, PointerEventArgs e)
        {
            if (sender is Control control)
                control.SetValue(ToolTip.IsOpenProperty, false);
        }

        private void OnKeyUp(object? sender, KeyEventArgs e)
        {
            if (DataContext is ShortcutEditDialogViewModel viewModel && IsRecording(viewModel))
                e.Handled = true;
        }

        private void OnKeyDown(object? sender, KeyEventArgs e)
        {
            if (DataContext is not ShortcutEditDialogViewModel viewModel || !IsRecording(viewModel))
                return;
            if (e.Key == Key.Escape)
            {
                viewModel.StopRecording();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Tab && viewModel.IsRecording)
                return;

            var combination = new StringBuilder();
            var control = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.Key is Key.LeftCtrl or Key.RightCtrl;
            var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt) || e.Key is Key.LeftAlt or Key.RightAlt;
            var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift) || e.Key is Key.LeftShift or Key.RightShift;
            var meta = e.KeyModifiers.HasFlag(KeyModifiers.Meta) || e.Key is Key.LWin or Key.RWin;
            if (control) combination.Append("Ctrl + ");
            if (alt) combination.Append("Alt + ");
            if (shift) combination.Append("Shift + ");
            if (meta) combination.Append("Win + ");
            var isModifier = e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
                or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.None;
            if (!isModifier)
            {
                combination.Append(e.Key);
                viewModel.SetRecordedKeyCombination(combination.ToString());
            }
            e.Handled = true;
        }

        private static bool IsRecording(ShortcutEditDialogViewModel viewModel) =>
            viewModel.IsRecording || viewModel.IsRecordingBeforeInputKey || viewModel.IsRecordingAfterInputKey;
    }
}
