using System;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using EasyChat.ViewModels.Dialogs;

namespace EasyChat.Views.Dialogs;

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
        if (DataContext is ShortcutEditDialogViewModel vm)
            // Simple subscription - in a real app consider WeakEventManager or ReactiveUI's WhenActivated
            vm.PropertyChanged += (s, args) =>
            {
                if (args.PropertyName is nameof(ShortcutEditDialogViewModel.IsRecording)
                    or nameof(ShortcutEditDialogViewModel.IsRecordingBeforeInputKey)
                    or nameof(ShortcutEditDialogViewModel.IsRecordingAfterInputKey))
                    if (IsRecording(vm))
                        // Slight delay to ensure command execution finishes and UI state settles
                        // Force focus to the UserControl itself so it captures all keys
                        Dispatcher.UIThread.Post(() => Focus());
            };
    }

    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        if (DataContext is not ShortcutEditDialogViewModel viewModel || !IsRecording(viewModel))
            return;

        // Suppress KeyUp to prevent buttons from firing on release (like Space)
        e.Handled = true;
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

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // Try to focus when shown
        Focus();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not ShortcutEditDialogViewModel viewModel || !IsRecording(viewModel))
            return;

        // Escape cancels recording
        if (e.Key == Key.Escape)
        {
            viewModel.StopRecording();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Tab && viewModel.IsRecording) return;

        var sb = new StringBuilder();

        // Check modifiers
        // Note: checking both KeyModifiers and the Key itself to capture "Ctrl" being held or just pressed
        var hasCtrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.Key == Key.LeftCtrl || e.Key == Key.RightCtrl;
        var hasAlt = e.KeyModifiers.HasFlag(KeyModifiers.Alt) || e.Key == Key.LeftAlt || e.Key == Key.RightAlt;
        var hasShift = e.KeyModifiers.HasFlag(KeyModifiers.Shift) || e.Key == Key.LeftShift || e.Key == Key.RightShift;
        var hasWin = e.KeyModifiers.HasFlag(KeyModifiers.Meta) || e.Key == Key.LWin || e.Key == Key.RWin;

        if (hasCtrl) sb.Append("Ctrl + ");
        if (hasAlt) sb.Append("Alt + ");
        if (hasShift) sb.Append("Shift + ");
        if (hasWin) sb.Append("Win + ");

        // Check if the current key is a modifier key
        var isModifierKey = e.Key is Key.LeftCtrl or Key.RightCtrl or
            Key.LeftAlt or Key.RightAlt or
            Key.LeftShift or Key.RightShift or
            Key.LWin or Key.RWin or
            Key.None;

        if (!isModifierKey) sb.Append(e.Key.ToString());

        var currentCombo = sb.ToString();

        // If it ends with " + ", trim it for display if it's just modifiers
        // But for "setting the value", maybe we want to keep it? 
        // Actually, if it's just modifiers, we show "Ctrl + "

        if (!isModifierKey)
            viewModel.SetRecordedKeyCombination(currentCombo);

        e.Handled = true;
    }

    private static bool IsRecording(ShortcutEditDialogViewModel viewModel)
    {
        return viewModel.IsRecording ||
               viewModel.IsRecordingBeforeInputKey ||
               viewModel.IsRecordingAfterInputKey;
    }
}
