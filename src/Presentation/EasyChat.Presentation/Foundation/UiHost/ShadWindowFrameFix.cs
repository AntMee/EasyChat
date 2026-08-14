using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.VisualTree;

namespace EasyChat.Presentation.Foundation.UiHost;

public static class ShadWindowFrameFix
{
    private static readonly ConditionalWeakTable<ShadUI.Window, Border> Frames = new();

    public static void Apply(ShadUI.Window window)
    {
        if (Frames.TryGetValue(window, out _))
            return;

        var root = window.GetVisualDescendants()
            .OfType<Panel>()
            .FirstOrDefault(panel => panel.Name == "PART_Root");

        if (root is null)
            return;

        // ShadUI's root Border is painted before its content, so the content can hide the frame.
        // Replace it with a final child in the same root panel to keep every curved edge visible.
        window.BorderThickness = new Thickness(0);
        var frame = new Border
        {
            BorderThickness = new Thickness(1),
            IsHitTestVisible = false
        };
        frame.Bind(Border.BorderBrushProperty, new Binding
        {
            Source = window,
            Path = nameof(Border.BorderBrush)
        });
        frame.Bind(Border.CornerRadiusProperty, new Binding
        {
            Source = window,
            Path = nameof(ShadUI.Window.RootCornerRadius)
        });

        root.Children.Add(frame);
        Frames.Add(window, frame);
    }
}
