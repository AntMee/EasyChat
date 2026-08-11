using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Material.Icons;
using Material.Icons.Avalonia;

namespace EasyChat.Presentation.Features.Shell;

public static class UpdateToastContentFactory
{
    public static StackPanel CreateAvailabilityContent(
        string latestVersion,
        Action dismissAction,
        Action updateAction)
    {
        var laterButton = new Button
        {
            Content = Lang.Resources.Later,
            MinWidth = 88,
            Height = 34,
            Padding = new Thickness(14, 7),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        laterButton.Classes.Add("Ghost");

        var updateButton = new Button
        {
            Content = Lang.Resources.Update,
            MinWidth = 104,
            Height = 34,
            Padding = new Thickness(14, 7),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        updateButton.Classes.Add("Primary");

        laterButton.Click += (_, _) => dismissAction();
        updateButton.Click += (_, _) =>
        {
            dismissAction();
            updateAction();
        };

        return new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 10,
                    Children =
                    {
                        CreateIconSurface(),
                        new TextBlock
                        {
                            Text = string.Format(Lang.Resources.NewVersionContent, latestVersion),
                            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                            VerticalAlignment = VerticalAlignment.Center
                        }
                    }
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { laterButton, updateButton }
                }
            }
        };
    }

    public static StackPanel CreateProgressContent(
        out ProgressBar progress,
        out TextBlock progressText)
    {
        progress = new ProgressBar
        {
            Value = 0,
            Minimum = 0,
            Maximum = 100,
            Width = 300,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsHitTestVisible = false
        };
        progress.Classes.Add("UpdateProgress");

        progressText = new TextBlock
        {
            Text = "0%",
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        progressText.Classes.Add("Muted");

        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            ColumnSpacing = 8,
            Children =
            {
                CreateIconSurface(),
                progressText
            }
        };
        Grid.SetColumn(progressText, 1);

        return new StackPanel
        {
            Spacing = 6,
            Children = { header, progress }
        };
    }

    private static Border CreateIconSurface()
    {
        var surface = new Border
        {
            Width = 32,
            Height = 32,
            CornerRadius = new CornerRadius(10),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = CreateIcon()
        };
        surface.Classes.Add("UpdateToastIconSurface");
        return surface;
    }

    private static MaterialIcon CreateIcon()
    {
        var icon = new MaterialIcon
        {
            Kind = MaterialIconKind.Download,
            Width = 16,
            Height = 16,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        icon.Classes.Add("UpdateToastIcon");
        return icon;
    }
}
