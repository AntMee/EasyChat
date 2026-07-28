using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia.Controls;
using EasyChat.Lang;

namespace EasyChat.Common;

public static class CopyFeedback
{
    private static readonly ConditionalWeakTable<Control, FeedbackState> States = new();
    private static readonly TimeSpan DisplayDuration = TimeSpan.FromMilliseconds(1200);

    public static async void Show(Control? anchor)
    {
        if (anchor == null)
        {
            return;
        }

        var state = States.GetOrCreateValue(anchor);
        if (!state.IsShowing)
        {
            state.OriginalTip = ToolTip.GetTip(anchor);
            state.IsShowing = true;
        }

        var version = ++state.Version;
        ToolTip.SetIsOpen(anchor, false);
        ToolTip.SetTip(anchor, Resources.Copied);
        ToolTip.SetIsOpen(anchor, true);

        await Task.Delay(DisplayDuration);
        if (state.Version != version)
        {
            return;
        }

        ToolTip.SetIsOpen(anchor, false);
        ToolTip.SetTip(anchor, state.OriginalTip);
        state.IsShowing = false;
        States.Remove(anchor);
    }

    private sealed class FeedbackState
    {
        public int Version { get; set; }
        public object? OriginalTip { get; set; }
        public bool IsShowing { get; set; }
    }
}
