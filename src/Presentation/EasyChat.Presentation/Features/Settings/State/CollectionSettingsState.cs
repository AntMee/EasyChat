using System.Collections.ObjectModel;
using EasyChat.Contracts.Settings;
using EasyChat.Presentation.Features.Shortcuts;
using ReactiveUI;

namespace EasyChat.Presentation.Features.Settings.State;

public sealed class PromptEntryState : LiveSettingsSection
{
    private string _name;
    private string _content;
    private bool _isDefault;

    public PromptEntryState(PromptEntrySettings value, Func<SettingsSection, EasyChat.Shared.Results.Result> commit)
        : base(SettingsSection.Prompts, commit)
    {
        Id = value.Id;
        _name = value.Name;
        _content = value.Content;
        _isDefault = value.IsDefault;
    }

    public string Id { get; }
    public string Name { get => _name; set => Set(ref _name, value); }
    public string Content
    {
        get => _content;
        set
        {
            if (Set(ref _content, value))
                this.RaisePropertyChanged(nameof(ContentPreview));
        }
    }
    public bool IsDefault { get => _isDefault; set => Set(ref _isDefault, value); }
    public string ContentPreview => Content.Length > 100 ? Content[..100] + "..." : Content;
    public PromptEntrySettings ToContract() => new(Id, Name, Content, IsDefault);
}

public sealed class LivePromptSettings : LiveSettingsSection
{
    private string _selectedPromptId;

    public LivePromptSettings(PromptSettings value, Func<SettingsSection, EasyChat.Shared.Results.Result> commit)
        : base(SettingsSection.Prompts, commit)
    {
        _selectedPromptId = value.SelectedPromptId;
        Entries = new ObservableCollection<PromptEntryState>(
            value.Entries.Select(entry => new PromptEntryState(entry, commit)));
        Entries.CollectionChanged += (_, _) => Commit();
    }

    public string SelectedPromptId { get => _selectedPromptId; set => Set(ref _selectedPromptId, value); }
    public ObservableCollection<PromptEntryState> Entries { get; }
    public PromptSettings ToContract() => new(SelectedPromptId, Entries.Select(entry => entry.ToContract()).ToArray());
}

public sealed class ShortcutParameterState
{
    public ShortcutParameterState(ShortcutParameterSettings value)
    {
        Engine = value.Engine;
        EngineId = value.EngineId;
        Source = value.Source;
        Target = value.Target;
        Value = value.Value;
        ReadSelectedText = value.ReadSelectedText;
        InputTranslateBeforeKey = value.InputTranslateBeforeKey;
        InputTranslateAfterKey = value.InputTranslateAfterKey;
        ReplaceCurrentInput = value.ReplaceCurrentInput;
        TextAssistMode = value.TextAssistMode;
        ShowSelectionToolbar = value.ShowSelectionToolbar;
    }

    public string Engine { get; set; }
    public string? EngineId { get; set; }
    public LanguageSettings? Source { get; set; }
    public LanguageSettings? Target { get; set; }
    public string? Value { get; set; }
    public bool? ReadSelectedText { get; set; }
    public string? InputTranslateBeforeKey { get; set; }
    public string? InputTranslateAfterKey { get; set; }
    public bool? ReplaceCurrentInput { get; set; }
    public TextAssistShortcutMode? TextAssistMode { get; set; }
    public bool? ShowSelectionToolbar { get; set; }

    public ShortcutParameterSettings ToContract() => new(
        Engine, EngineId, Source, Target, Value, ReadSelectedText,
        InputTranslateBeforeKey, InputTranslateAfterKey, ReplaceCurrentInput,
        TextAssistMode, ShowSelectionToolbar);
}

public sealed class ShortcutEntryState : LiveSettingsSection
{
    private string _actionType;
    private ShortcutParameterState? _parameter;
    private string _keyCombination;
    private bool _isEnabled;

    public ShortcutEntryState(
        ShortcutEntrySettings value,
        Func<SettingsSection, EasyChat.Shared.Results.Result> commit)
        : base(SettingsSection.Shortcut, commit)
    {
        _actionType = value.ActionType;
        _parameter = value.Parameter is null ? null : new ShortcutParameterState(value.Parameter);
        _keyCombination = value.KeyCombination;
        _isEnabled = value.IsEnabled;
    }

    public string ActionType { get => _actionType; set => Set(ref _actionType, value); }
    public ShortcutParameterState? Parameter { get => _parameter; set => Set(ref _parameter, value); }
    public string KeyCombination { get => _keyCombination; set => Set(ref _keyCombination, value); }
    public bool IsEnabled { get => _isEnabled; set => Set(ref _isEnabled, value); }
    public string DisplayText => ShortcutActionCatalog.GetDisplayName(ActionType);
    public string ParameterDisplayText => Parameter?.Value
        ?? (Parameter is null ? string.Empty : FormatParameter(Parameter));

    public ShortcutEntrySettings ToContract() => new(
        ActionType, Parameter?.ToContract(), KeyCombination, IsEnabled);

    private static string FormatParameter(ShortcutParameterState parameter)
    {
        var display = parameter.Engine;
        if (parameter.Source is not null && parameter.Target is not null)
            display += $" ({parameter.Source.DisplayName} -> {parameter.Target.DisplayName})";
        return display;
    }
}

public sealed class LiveShortcutSettings : LiveSettingsSection
{
    public LiveShortcutSettings(ShortcutSettings value, Func<SettingsSection, EasyChat.Shared.Results.Result> commit)
        : base(SettingsSection.Shortcut, commit)
    {
        Entries = new ObservableCollection<ShortcutEntryState>(
            value.Entries.Select(entry => new ShortcutEntryState(entry, commit)));
        Entries.CollectionChanged += (_, _) => Commit();
    }

    public ObservableCollection<ShortcutEntryState> Entries { get; }
    public ShortcutSettings ToContract() => new(Entries.Select(entry => entry.ToContract()).ToArray());
}
