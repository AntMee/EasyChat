using System.Collections.ObjectModel;
using System.Reactive;
using Avalonia.Controls.Notifications;
using EasyChat.Contracts.Settings;
using EasyChat.Contracts.Translation;
using EasyChat.Lang;
using EasyChat.Presentation.Features.Settings.State;
using EasyChat.Presentation.Foundation.Localization;
using Material.Icons;
using ReactiveUI;
using SukiUI.Dialogs;

namespace EasyChat.Presentation.Features.Shortcuts
{
    public static class ShortcutActionCatalog
    {
        public static IReadOnlyList<EasyChat.ViewModels.Dialogs.ShortcutActionOption> All { get; } =
        [
            new("Screenshot", "Action_ScreenshotTranslate"),
            new("InputTranslate", "Action_InputTranslate"),
            new("QuickTranslate", "Action_QuickTranslate"),
            new("QuickCorrect", "Action_QuickCorrect"),
            new("SelectionTranslate", "Action_SelectionTranslate"),
            new("SwitchSourceLang", "Action_SwitchSourceLang", true, "Hint_TargetLangCode",
                ["zh", "en", "ja", "ko", "fr", "de", "es", "ru"]),
            new("SwitchTargetLang", "Action_SwitchTargetLang", true, "Hint_TargetLangCode",
                ["zh", "en", "ja", "ko", "fr", "de", "es", "ru"]),
            new("SwitchEngineSourceTarget", "Action_SwitchEngineSourceTarget", true, "Hint_SwitchConfig")
        ];

        public static EasyChat.ViewModels.Dialogs.ShortcutActionOption? Get(string actionType) =>
            All.FirstOrDefault(action => action.ActionType == actionType);

        public static string GetDisplayName(string actionType) => Get(actionType)?.DisplayName ?? actionType;
    }
}

namespace EasyChat.ViewModels.Pages
{
    using EasyChat.Presentation.Features.Shortcuts;

    public sealed class ShortcutViewModel : NavigationPageViewModel
    {
        private static readonly string[] BasicTypes =
            ["Screenshot", "InputTranslate", "SelectionTranslate", "QuickTranslate", "QuickCorrect"];
        private static readonly string[] TextAssistTypes = ["QuickTranslate", "QuickCorrect"];
        private static readonly string[] LanguageTypes = ["SwitchEngineSourceTarget"];
        private readonly SettingsSession _settings;
        private readonly ISukiDialogManager _dialogs;
        private readonly TranslationLanguageOptions _languages;
        private ObservableCollection<ShortcutEntryState> _basicShortcuts = [];
        private ObservableCollection<ShortcutEntryState> _languageShortcuts = [];

        public ShortcutViewModel(
            SettingsSession settings,
            ISukiDialogManager dialogs,
            TranslationLanguageOptions languages)
            : base(Resources.Shortcut, MaterialIconKind.Keyboard, 2)
        {
            _settings = settings;
            _dialogs = dialogs;
            _languages = languages;
            Refresh();
            settings.Shortcut.Entries.CollectionChanged += (_, _) => Refresh();
            AddEntryCommand = ReactiveCommand.Create<string>(AddEntry);
            EditEntryCommand = ReactiveCommand.Create<ShortcutEntryState>(EditEntry);
            RemoveEntryCommand = ReactiveCommand.Create<ShortcutEntryState>(RemoveEntry);
        }

        public ObservableCollection<ShortcutEntryState> BasicShortcuts
        {
            get => _basicShortcuts;
            private set => this.RaiseAndSetIfChanged(ref _basicShortcuts, value);
        }
        public ObservableCollection<ShortcutEntryState> LanguageShortcuts
        {
            get => _languageShortcuts;
            private set => this.RaiseAndSetIfChanged(ref _languageShortcuts, value);
        }
        public ReactiveCommand<string, Unit> AddEntryCommand { get; }
        public ReactiveCommand<ShortcutEntryState, Unit> EditEntryCommand { get; }
        public ReactiveCommand<ShortcutEntryState, Unit> RemoveEntryCommand { get; }

        private void Refresh()
        {
            BasicShortcuts = new ObservableCollection<ShortcutEntryState>(
                _settings.Shortcut.Entries.Where(entry => BasicTypes.Contains(entry.ActionType)));
            LanguageShortcuts = new ObservableCollection<ShortcutEntryState>(
                _settings.Shortcut.Entries.Where(entry => LanguageTypes.Contains(entry.ActionType)));
        }

        private void AddEntry(string category)
        {
            var allowed = category switch
            {
                "TextAssist" => TextAssistTypes,
                "Basic" => BasicTypes,
                _ => LanguageTypes
            };
            ShowEditor(null, allowed, category switch
            {
                "TextAssist" => "QuickTranslate",
                "Basic" => "Screenshot",
                _ => "SwitchEngineSourceTarget"
            });
        }

        private void EditEntry(ShortcutEntryState entry)
        {
            var allowed = TextAssistTypes.Contains(entry.ActionType)
                ? TextAssistTypes
                : BasicTypes.Contains(entry.ActionType) ? BasicTypes : LanguageTypes;
            ShowEditor(entry, allowed, entry.ActionType);
        }

        private void ShowEditor(ShortcutEntryState? entry, IReadOnlyList<string> allowed, string defaultAction)
        {
            _dialogs.CreateDialog()
                .WithViewModel(dialog => new EasyChat.ViewModels.Dialogs.ShortcutEditDialogViewModel(
                    dialog, _settings, _languages, allowed, entry, defaultAction)
                {
                    OnClose = result =>
                    {
                        if (result is null)
                            return;
                        var replacement = new ShortcutEntryState(result, _settings.FlushSection);
                        if (entry is null)
                            _settings.Shortcut.Entries.Add(replacement);
                        else
                            _settings.Shortcut.Entries[_settings.Shortcut.Entries.IndexOf(entry)] = replacement;
                    }
                })
                .TryShow();
        }

        private void RemoveEntry(ShortcutEntryState entry) => _dialogs.CreateDialog()
            .OfType(NotificationType.Warning)
            .WithTitle(Resources.ConfirmDeletion)
            .WithContent(Resources.AreYouSureDelete)
            .WithActionButton(Resources.Delete, _ => _settings.Shortcut.Entries.Remove(entry), true)
            .WithActionButton(Resources.Cancel, _ => { }, true)
            .TryShow();
    }
}

namespace EasyChat.ViewModels.Dialogs
{
    using EasyChat.Presentation.Features.Shortcuts;

    public sealed class ShortcutEditDialogViewModel : ViewModelBase
    {
        private readonly ISukiDialog _dialog;
        private readonly ShortcutEntryState? _existing;
        private readonly SettingsSession _settings;
        private readonly TranslationLanguageOptions _languageOptions;
        private ShortcutActionOption _selectedAction;
        private EngineOption? _selectedEngineOption;
        private LanguageSettings? _selectedSourceLang;
        private LanguageSettings? _selectedTargetLang;
        private string _parameter = string.Empty;
        private string _keyCombination = string.Empty;
        private bool _isRecording;
        private bool _isRecordingBeforeInputKey;
        private bool _isRecordingAfterInputKey;
        private string _inputTranslateBeforeKey = string.Empty;
        private string _inputTranslateAfterKey = string.Empty;
        private bool _replaceCurrentInput;
        private bool _readSelectedText;
        private bool _showSelectionToolbar;
        private TextAssistShortcutMode _textAssistMode;

        public ShortcutEditDialogViewModel(
            ISukiDialog dialog,
            SettingsSession settings,
            TranslationLanguageOptions languageOptions,
            IReadOnlyList<string> allowedActionTypes,
            ShortcutEntryState? existing = null,
            string? defaultAction = null)
        {
            _dialog = dialog;
            _settings = settings;
            _languageOptions = languageOptions;
            _existing = existing;
            AvailableActions = ShortcutActionCatalog.All
                .Where(action => allowedActionTypes.Contains(action.ActionType))
                .ToArray();
            _selectedAction = ShortcutActionCatalog.Get(existing?.ActionType ?? defaultAction ?? string.Empty)
                              ?? AvailableActions.First();
            AvailableEngineOptions =
            [
                .. new[] { "Baidu", "Tencent", "Google", "DeepL" }
                    .Select(provider => new EngineOption(provider, provider, true)),
                .. settings.AiModel.ConfiguredModels
                    .Select(model => new EngineOption(model.Name, model.Id, false))
            ];
            _selectedEngineOption = AvailableEngineOptions.FirstOrDefault(option => option.Id == "Baidu")
                                    ?? AvailableEngineOptions.FirstOrDefault();
            UpdateAvailableLanguages();
            _selectedSourceLang = AvailableLanguages.FirstOrDefault(language => language.Id == "auto")
                                  ?? AvailableLanguages.FirstOrDefault();
            _selectedTargetLang = AvailableLanguages.FirstOrDefault(language => language.Id == "zh-Hans")
                                  ?? AvailableLanguages.FirstOrDefault();
            Restore(existing);

            ToggleRecordingCommand = ReactiveCommand.Create(() =>
            {
                IsRecording = !IsRecording;
                IsRecordingBeforeInputKey = false;
                IsRecordingAfterInputKey = false;
                if (IsRecording)
                    KeyCombination = string.Empty;
                else if (string.IsNullOrEmpty(KeyCombination))
                    KeyCombination = _existing?.KeyCombination ?? string.Empty;
            });
            ToggleBeforeInputKeyRecordingCommand = ReactiveCommand.Create(() => StartInputKeyRecording(true));
            ToggleAfterInputKeyRecordingCommand = ReactiveCommand.Create(() => StartInputKeyRecording(false));
            ClearBeforeInputKeyCommand = ReactiveCommand.Create(() => { InputTranslateBeforeKey = string.Empty; StopRecording(); });
            ClearAfterInputKeyCommand = ReactiveCommand.Create(() => { InputTranslateAfterKey = string.Empty; StopRecording(); });
            var canSave = this.WhenAnyValue(
                viewModel => viewModel.KeyCombination,
                viewModel => viewModel.SelectedAction,
                viewModel => viewModel.Parameter,
                viewModel => viewModel.SelectedEngineOption,
                (key, action, parameter, engine) =>
                    !string.IsNullOrWhiteSpace(key) &&
                    (!action.RequiresParameter ||
                     (action.ActionType == "SwitchEngineSourceTarget"
                         ? engine is not null && SelectedSourceLang is not null && SelectedTargetLang is not null
                         : !string.IsNullOrWhiteSpace(parameter))));
            SaveCommand = ReactiveCommand.Create(Save, canSave);
            CancelCommand = ReactiveCommand.Create(Cancel);
        }

        public sealed record EngineOption(string Name, string Id, bool IsMachine);

        public IReadOnlyList<ShortcutActionOption> AvailableActions { get; }
        public IReadOnlyList<EngineOption> AvailableEngineOptions { get; }
        public IReadOnlyList<LanguageSettings> AvailableLanguages { get; private set; } = [];
        public IReadOnlyList<string>? AvailableParameterOptions => SelectedAction.ParameterOptions;
        public IReadOnlyList<TextAssistShortcutMode> TextAssistModes { get; } = Enum.GetValues<TextAssistShortcutMode>();
        public string ButtonText => _existing is null ? Resources.Add : Resources.Save;
        public MaterialIconKind Icon => _existing is null ? MaterialIconKind.Plus : MaterialIconKind.Edit;
        public bool IsComplexSwitchAction => SelectedAction.ActionType == "SwitchEngineSourceTarget";
        public bool IsTextAssistAction => SelectedAction.ActionType is "QuickTranslate" or "QuickCorrect";
        public bool IsModeSelectableTextAssistAction => false;
        public bool IsSelectionTranslateAction => SelectedAction.ActionType == "SelectionTranslate";
        public bool IsInputTranslateAction => SelectedAction.ActionType == "InputTranslate";
        public string SelectionToolbarOptionText => Resources.SelectionTranslation;
        public string SelectionToolbarOptionTip => Resources.ResourceManager.GetString("SelectionShortcutToolbarTip", Resources.Culture)
                                                   ?? "Show the configured selection toolbar instead of translating immediately.";

        public ShortcutActionOption SelectedAction
        {
            get => _selectedAction;
            set
            {
                if (_selectedAction == value)
                    return;
                this.RaiseAndSetIfChanged(ref _selectedAction, value);
                RaiseActionProperties();
                if (_existing?.ActionType != value.ActionType)
                {
                    Parameter = string.Empty;
                    ReadSelectedText = false;
                    ShowSelectionToolbar = false;
                    ReplaceCurrentInput = false;
                }
            }
        }
        public EngineOption? SelectedEngineOption
        {
            get => _selectedEngineOption;
            set
            {
                this.RaiseAndSetIfChanged(ref _selectedEngineOption, value);
                UpdateAvailableLanguages();
            }
        }
        public LanguageSettings? SelectedSourceLang { get => _selectedSourceLang; set => this.RaiseAndSetIfChanged(ref _selectedSourceLang, value); }
        public LanguageSettings? SelectedTargetLang { get => _selectedTargetLang; set => this.RaiseAndSetIfChanged(ref _selectedTargetLang, value); }
        public string Parameter { get => _parameter; set => this.RaiseAndSetIfChanged(ref _parameter, value); }
        public string KeyCombination { get => _keyCombination; set => this.RaiseAndSetIfChanged(ref _keyCombination, value); }
        public bool IsRecording { get => _isRecording; set => this.RaiseAndSetIfChanged(ref _isRecording, value); }
        public bool IsRecordingBeforeInputKey
        {
            get => _isRecordingBeforeInputKey;
            set { this.RaiseAndSetIfChanged(ref _isRecordingBeforeInputKey, value); this.RaisePropertyChanged(nameof(IsNotRecordingBeforeInputKey)); }
        }
        public bool IsNotRecordingBeforeInputKey => !IsRecordingBeforeInputKey;
        public bool IsRecordingAfterInputKey
        {
            get => _isRecordingAfterInputKey;
            set { this.RaiseAndSetIfChanged(ref _isRecordingAfterInputKey, value); this.RaisePropertyChanged(nameof(IsNotRecordingAfterInputKey)); }
        }
        public bool IsNotRecordingAfterInputKey => !IsRecordingAfterInputKey;
        public string InputTranslateBeforeKey { get => _inputTranslateBeforeKey; set => this.RaiseAndSetIfChanged(ref _inputTranslateBeforeKey, value); }
        public string InputTranslateAfterKey { get => _inputTranslateAfterKey; set => this.RaiseAndSetIfChanged(ref _inputTranslateAfterKey, value); }
        public bool ReplaceCurrentInput { get => _replaceCurrentInput; set => this.RaiseAndSetIfChanged(ref _replaceCurrentInput, value); }
        public bool ReadSelectedText { get => _readSelectedText; set => this.RaiseAndSetIfChanged(ref _readSelectedText, value); }
        public bool ShowSelectionToolbar { get => _showSelectionToolbar; set => this.RaiseAndSetIfChanged(ref _showSelectionToolbar, value); }
        public TextAssistShortcutMode TextAssistMode
        {
            get => _textAssistMode;
            set { this.RaiseAndSetIfChanged(ref _textAssistMode, value); this.RaisePropertyChanged(nameof(IsReadSelectionLocked)); }
        }
        public bool IsReadSelectionLocked => IsModeSelectableTextAssistAction && TextAssistMode == TextAssistShortcutMode.Simple;
        public ReactiveCommand<Unit, Unit> ToggleRecordingCommand { get; }
        public ReactiveCommand<Unit, Unit> ToggleBeforeInputKeyRecordingCommand { get; }
        public ReactiveCommand<Unit, Unit> ToggleAfterInputKeyRecordingCommand { get; }
        public ReactiveCommand<Unit, Unit> ClearBeforeInputKeyCommand { get; }
        public ReactiveCommand<Unit, Unit> ClearAfterInputKeyCommand { get; }
        public ReactiveCommand<Unit, Unit> SaveCommand { get; }
        public ReactiveCommand<Unit, Unit> CancelCommand { get; }
        public Action<ShortcutEntrySettings?>? OnClose { get; init; }

        public void SetRecordedKeyCombination(string combination)
        {
            if (IsRecordingBeforeInputKey)
                InputTranslateBeforeKey = combination;
            else if (IsRecordingAfterInputKey)
                InputTranslateAfterKey = combination;
            else
                KeyCombination = combination;
            StopRecording();
        }

        public void StopRecording()
        {
            IsRecording = false;
            IsRecordingBeforeInputKey = false;
            IsRecordingAfterInputKey = false;
        }

        private void StartInputKeyRecording(bool before)
        {
            IsRecording = false;
            IsRecordingBeforeInputKey = before;
            IsRecordingAfterInputKey = !before;
        }

        private void Restore(ShortcutEntryState? entry)
        {
            if (entry is null)
                return;
            KeyCombination = entry.KeyCombination;
            Parameter = entry.Parameter?.Value ?? string.Empty;
            ReadSelectedText = IsTextAssistAction && (entry.Parameter?.ReadSelectedText ?? true);
            InputTranslateBeforeKey = entry.Parameter?.InputTranslateBeforeKey ?? string.Empty;
            InputTranslateAfterKey = entry.Parameter?.InputTranslateAfterKey ?? string.Empty;
            ReplaceCurrentInput = entry.Parameter?.ReplaceCurrentInput ?? false;
            ShowSelectionToolbar = entry.Parameter?.ShowSelectionToolbar ?? false;
            TextAssistMode = entry.Parameter?.TextAssistMode ?? TextAssistShortcutMode.Simple;
            if (!IsComplexSwitchAction || entry.Parameter is null)
                return;
            SelectedEngineOption = AvailableEngineOptions.FirstOrDefault(option => option.Id == entry.Parameter.EngineId)
                                   ?? AvailableEngineOptions.FirstOrDefault(option => option.Name == entry.Parameter.Engine)
                                   ?? SelectedEngineOption;
            SelectedSourceLang = AvailableLanguages.FirstOrDefault(language => language.Id == entry.Parameter.Source?.Id)
                                 ?? SelectedSourceLang;
            SelectedTargetLang = AvailableLanguages.FirstOrDefault(language => language.Id == entry.Parameter.Target?.Id)
                                 ?? SelectedTargetLang;
        }

        private void UpdateAvailableLanguages()
        {
            var all = _languageOptions.All.ToList();
            if (SelectedEngineOption is { IsMachine: true } engine)
            {
                all = all.Where(language => language.Id == "auto" ||
                    language.ProviderCodes.TryGetValue(engine.Id, out var code) && !string.IsNullOrWhiteSpace(code)).ToList();
            }
            AvailableLanguages = all;
            this.RaisePropertyChanged(nameof(AvailableLanguages));
            SelectedSourceLang = all.FirstOrDefault(language => language.Id == SelectedSourceLang?.Id)
                                 ?? all.FirstOrDefault(language => language.Id == "auto")
                                 ?? all.FirstOrDefault();
            SelectedTargetLang = all.FirstOrDefault(language => language.Id == SelectedTargetLang?.Id)
                                 ?? all.FirstOrDefault(language => language.Id == "zh-Hans")
                                 ?? all.FirstOrDefault();
        }

        private void RaiseActionProperties()
        {
            this.RaisePropertyChanged(nameof(IsComplexSwitchAction));
            this.RaisePropertyChanged(nameof(IsTextAssistAction));
            this.RaisePropertyChanged(nameof(IsModeSelectableTextAssistAction));
            this.RaisePropertyChanged(nameof(IsSelectionTranslateAction));
            this.RaisePropertyChanged(nameof(IsInputTranslateAction));
            this.RaisePropertyChanged(nameof(AvailableParameterOptions));
        }

        private void Save()
        {
            var parameter = IsComplexSwitchAction
                ? new ShortcutParameterSettings(
                    SelectedEngineOption?.Name ?? string.Empty,
                    SelectedEngineOption?.Id,
                    SelectedSourceLang,
                    SelectedTargetLang,
                    null, null, null, null, null, null, null)
                : new ShortcutParameterSettings(
                    string.Empty, null, null, null,
                    string.IsNullOrWhiteSpace(Parameter) ? null : Parameter,
                    IsTextAssistAction ? ReadSelectedText : null,
                    IsInputTranslateAction ? NullIfEmpty(InputTranslateBeforeKey) : null,
                    IsInputTranslateAction ? NullIfEmpty(InputTranslateAfterKey) : null,
                    IsInputTranslateAction ? ReplaceCurrentInput : null,
                    IsModeSelectableTextAssistAction ? TextAssistMode : null,
                    IsSelectionTranslateAction ? ShowSelectionToolbar : null);
            OnClose?.Invoke(new ShortcutEntrySettings(
                SelectedAction.ActionType,
                SelectedAction.RequiresParameter || IsTextAssistAction || IsInputTranslateAction || IsSelectionTranslateAction
                    ? parameter
                    : null,
                KeyCombination,
                _existing?.IsEnabled ?? true));
            _dialog.Dismiss();
        }

        private void Cancel()
        {
            OnClose?.Invoke(null);
            _dialog.Dismiss();
        }

        private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public sealed record ShortcutActionOption(
        string ActionType,
        string ResourceKey,
        bool RequiresParameter = false,
        string? ParameterHintKey = null,
        IReadOnlyList<string>? ParameterOptions = null)
    {
        public string DisplayName => Resources.ResourceManager.GetString(ResourceKey, Resources.Culture) ?? ResourceKey;
        public string? ParameterHint => string.IsNullOrWhiteSpace(ParameterHintKey)
            ? null
            : Resources.ResourceManager.GetString(ParameterHintKey, Resources.Culture) ?? ParameterHintKey;
    }
}
