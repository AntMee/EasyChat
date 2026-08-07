using System.Collections.ObjectModel;
using System.Reactive;
using Avalonia.Controls.Notifications;
using EasyChat.Contracts.Settings;
using EasyChat.Presentation.Features.Settings.State;
using EasyChat.Presentation.Foundation.Navigation;
using EasyChat.Presentation.Lang;
using Material.Icons;
using ReactiveUI;
using SukiUI.Dialogs;

namespace EasyChat.Presentation.Features.Settings.Prompts;

public sealed class PromptViewModel : NavigationPageViewModel
{
    private readonly SettingsSession _settings;
    private readonly ISukiDialogManager _dialogs;

    public PromptViewModel(SettingsSession settings, ISukiDialogManager dialogs)
        : base(Resources.Prompts, MaterialIconKind.TextBox, 3)
    {
        _settings = settings;
        _dialogs = dialogs;
        AddPromptCommand = ReactiveCommand.Create(() => ShowEditor(null));
        EditPromptCommand = ReactiveCommand.Create<PromptEntryState>(ShowEditor);
        RemovePromptCommand = ReactiveCommand.Create<PromptEntryState>(RemovePrompt);
        SetDefaultCommand = ReactiveCommand.Create<PromptEntryState>(SetDefault);
        Prompts.CollectionChanged += (_, _) =>
        {
            this.RaisePropertyChanged(nameof(HasPrompts));
            this.RaisePropertyChanged(nameof(HasNoPrompts));
        };
    }

    public ObservableCollection<PromptEntryState> Prompts => _settings.Prompts.Entries;
    public bool HasPrompts => Prompts.Count > 0;
    public bool HasNoPrompts => !HasPrompts;
    public ReactiveCommand<Unit, Unit> AddPromptCommand { get; }
    public ReactiveCommand<PromptEntryState, Unit> EditPromptCommand { get; }
    public ReactiveCommand<PromptEntryState, Unit> RemovePromptCommand { get; }
    public ReactiveCommand<PromptEntryState, Unit> SetDefaultCommand { get; }

    private void ShowEditor(PromptEntryState? entry)
    {
        _dialogs.CreateDialog()
            .WithViewModel(dialog => new PromptEditDialogViewModel(dialog, entry)
            {
                OnClose = result =>
                {
                    if (result is null)
                        return;
                    if (entry is null)
                    {
                        Prompts.Add(new PromptEntryState(result, _settings.FlushSection));
                        return;
                    }

                    entry.Name = result.Name;
                    entry.Role = result.Content;
                }
            })
            .TryShow();
    }

    private void RemovePrompt(PromptEntryState entry)
    {
        _dialogs.CreateDialog()
            .OfType(NotificationType.Warning)
            .WithTitle(Resources.ConfirmDeletion)
            .WithContent(Resources.ConfirmDeletePrompt)
            .WithActionButton(Resources.Delete, _ =>
            {
                var replacement = Prompts.FirstOrDefault(prompt =>
                                      !ReferenceEquals(prompt, entry) && prompt.IsDefault)
                                  ?? Prompts.FirstOrDefault(prompt => !ReferenceEquals(prompt, entry));
                var wasDefault = entry.IsDefault;
                var wasSelected = _settings.Prompts.SelectedPromptId == entry.Id;

                if (wasDefault && replacement is not null)
                    replacement.IsDefault = true;
                Prompts.Remove(entry);

                if (wasDefault || wasSelected)
                    _settings.Prompts.SelectedPromptId = replacement?.Id ?? string.Empty;
            }, true)
            .WithActionButton(Resources.Cancel, _ => { }, true)
            .TryShow();
    }

    private void SetDefault(PromptEntryState entry)
    {
        var current = Prompts.FirstOrDefault(prompt => prompt.IsDefault);
        if (current != entry)
        {
            if (current is not null)
                current.IsDefault = false;
            entry.IsDefault = true;
        }
        _settings.Prompts.SelectedPromptId = entry.Id;
    }
}

public sealed class PromptEditDialogViewModel : ConventionViewModelBase
{
    private readonly ISukiDialog _dialog;
    private readonly PromptEntryState? _existing;
    private string _name;
    private string _role;

    public PromptEditDialogViewModel(ISukiDialog dialog, PromptEntryState? existing = null)
    {
        _dialog = dialog;
        _existing = existing;
        _name = existing?.Name ?? string.Empty;
        _role = existing?.Role ?? string.Empty;
        SaveCommand = ReactiveCommand.Create(
            Save,
            this.WhenAnyValue(
                viewModel => viewModel.Name,
                viewModel => viewModel.Content,
                (name, content) => !string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(content)));
        CancelCommand = ReactiveCommand.Create(Cancel);
    }

    public string Name { get => _name; set => this.RaiseAndSetIfChanged(ref _name, value); }
    public string Content { get => _role; set => this.RaiseAndSetIfChanged(ref _role, value); }
    public ReactiveCommand<Unit, Unit> SaveCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }
    public Action<PromptEntrySettings?>? OnClose { get; init; }

    private void Save()
    {
        OnClose?.Invoke(new PromptEntrySettings(
            _existing?.Id ?? Guid.NewGuid().ToString(),
            Name,
            Content,
            _existing?.IsDefault ?? false));
        _dialog.Dismiss();
    }

    private void Cancel()
    {
        OnClose?.Invoke(null);
        _dialog.Dismiss();
    }
}
