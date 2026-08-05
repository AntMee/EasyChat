using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using EasyChat.Contracts.Settings;
using EasyChat.Presentation.Features.Settings.State;
using EasyChat.Presentation.Features.Input;
using Key = Avalonia.Input.Key;

namespace EasyChat.Presentation.Features.Input.Views;

public partial class TypingView : Window
{
    private SettingsSession? _settings;

    public TypingView() => InitializeComponent();

    public TypingView(TypingViewModel viewModel, SettingsSession settings)
    {
        InitializeComponent();
        DataContext = viewModel;
        _settings = settings;
        ApplyConfiguration();
        settings.Changed += OnSettingsChanged;
        Opened += (_, _) => this.FindControl<TextBox>("InputBox")?.Focus();
        Closed += (_, _) =>
        {
            settings.Changed -= OnSettingsChanged;
            viewModel.Dispose();
        };
        Deactivated += (_, _) =>
        {
            if (!this.GetVisualDescendants().OfType<ComboBox>().Any(comboBox => comboBox.IsDropDownOpen))
                Close();
        };
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs eventArgs)
    {
        if (eventArgs.Section == SettingsSection.Input)
            ApplyConfiguration();
    }

    private void ApplyConfiguration()
    {
        if (_settings is null)
            return;
        var background = ParseBrush(_settings.Input.BackgroundColor);
        if (background is not null)
            MainCard.Background = background;
        var foreground = ParseBrush(_settings.Input.FontColor);
        if (foreground is not null)
            this.FindControl<TextBox>("InputBox")!.Foreground = foreground;
    }

    private async void InputBox_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox input)
            return;
        if (e.Key == Key.Escape)
        {
            Close();
            return;
        }
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        if (string.IsNullOrWhiteSpace(input.Text))
        {
            Close();
            return;
        }
        input.IsEnabled = false;
        Hide();
        if (DataContext is TypingViewModel viewModel)
            await viewModel.TranslateAndSendAsync(input.Text);
        Close();
    }

    private static IBrush? ParseBrush(string value)
    {
        try
        {
            return Brush.Parse(value);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
