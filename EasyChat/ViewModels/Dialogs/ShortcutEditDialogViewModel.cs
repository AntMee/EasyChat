using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reactive;
using EasyChat.Lang;
using EasyChat.Models.Configuration;
using EasyChat.Services.Abstractions;
using EasyChat.Services.Languages;
using Material.Icons;
using ReactiveUI;
using SukiUI.Dialogs;

namespace EasyChat.ViewModels.Dialogs;

[SuppressMessage("ReSharper", "PrivateFieldCanBeConvertedToLocalVariable")]
public class ShortcutEditDialogViewModel : ViewModelBase
{
    private readonly ISukiDialog _dialog;
    private readonly ShortcutEntry? _existingEntry;
    private readonly IConfigurationService _configurationService;

    public class EngineOption
    {
        public string? Name { get; set; }
        public string? Id { get; set; }
        public bool IsMachine { get; set; }

        public override string? ToString() => Name;
        
        public override bool Equals(object? obj)
        {
            if (obj is EngineOption other)
            {
                return Id == other.Id;
            }
            return false;
        }

        public override int GetHashCode()
        {
            // ReSharper disable once NonReadonlyMemberInGetHashCode
            return Id!.GetHashCode();
        }
    }

    public bool IsComplexSwitchAction => SelectedAction.ActionType == "SwitchEngineSourceTarget";
    public bool IsTextAssistAction => SelectedAction.ActionType is "QuickTranslate" or "QuickCorrect";
    public bool IsInputTranslateAction => SelectedAction.ActionType == "InputTranslate";

    private EngineOption? _selectedEngineOption;
    private LanguageDefinition? _selectedSourceLang;
    private LanguageDefinition? _selectedTargetLang;

    public List<EngineOption> AvailableEngineOptions { get; }

    public List<LanguageDefinition>? AvailableLanguages
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
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

    public LanguageDefinition? SelectedSourceLang
    {
        get => _selectedSourceLang;
        set => this.RaiseAndSetIfChanged(ref _selectedSourceLang, value);
    }

    public LanguageDefinition? SelectedTargetLang
    {
        get => _selectedTargetLang;
        set => this.RaiseAndSetIfChanged(ref _selectedTargetLang, value);
    }

    public ShortcutEditDialogViewModel(ISukiDialog dialog, IConfigurationService configurationService, string[] allowedActionTypes, ShortcutEntry? existingEntry = null)
    {
        _dialog = dialog;
        _configurationService = configurationService;
        _existingEntry = existingEntry;

        AvailableActions = ShortcutActionDefinition.AvailableActions
            .Where(a => allowedActionTypes.Contains(a.ActionType))
            .ToArray();

        AvailableEngineOptions = [];
        
        // Add Machine Engines
        foreach (var provider in MachineTrans.SupportedProviders)
        {
            AvailableEngineOptions.Add(new EngineOption { Name = provider, Id = provider, IsMachine = true });
        }
        
        // Add AI Models
        if (_configurationService.AiModel?.ConfiguredModels != null)
        {
            foreach (var model in _configurationService.AiModel.ConfiguredModels)
            {
                AvailableEngineOptions.Add(new EngineOption { Name = model.Name, Id = model.Id, IsMachine = false });
            }
        }

        // defaults
        _selectedEngineOption = AvailableEngineOptions.FirstOrDefault(e => e.Id == "Baidu") ?? AvailableEngineOptions.FirstOrDefault();
        
        // Initialize languages based on default engine
        UpdateAvailableLanguages();
        
        _selectedSourceLang = AvailableLanguages?.FirstOrDefault(l => l.Id == "auto") ?? AvailableLanguages?.FirstOrDefault();
        _selectedTargetLang = AvailableLanguages?.FirstOrDefault(l => l.Id == "zh-Hans") ?? AvailableLanguages?.FirstOrDefault();

        // Ensure we have at least one action if possible, or matches existing
        if (existingEntry != null)
        {
            var def = ShortcutActionDefinition.GetByType(existingEntry.ActionType);
            if (def != null) SelectedAction = def;
            KeyCombination = existingEntry.KeyCombination;
            ReadSelectedText = IsTextAssistAction && (existingEntry.Parameter?.ReadSelectedText ?? true);
            InputTranslateBeforeKey = existingEntry.Parameter?.InputTranslateBeforeKey ?? string.Empty;
            InputTranslateAfterKey = existingEntry.Parameter?.InputTranslateAfterKey ?? string.Empty;
            ReplaceCurrentInput = existingEntry.Parameter?.ReplaceCurrentInput ?? false;

            if (IsComplexSwitchAction && existingEntry.Parameter != null)
            {
                var param = existingEntry.Parameter;
                
                // Try to find by ID first
                var engInfo = AvailableEngineOptions.FirstOrDefault(e => e.Id == param.EngineId);
                
                // Fallback to Name
                if (engInfo == null)
                {
                    engInfo = AvailableEngineOptions.FirstOrDefault(e => e.Name == param.Engine);
                }
                
                if (engInfo != null) 
                {
                    _selectedEngineOption = engInfo;
                    UpdateAvailableLanguages(); 
                }

                if (param.Source != null)
                {
                    var src = AvailableLanguages?.FirstOrDefault(l => l.Id == param.Source.Id);
                    if (src != null) _selectedSourceLang = src;
                }

                if (param.Target != null)
                {
                    var tgt = AvailableLanguages?.FirstOrDefault(l => l.Id == param.Target.Id);
                    if (tgt != null) _selectedTargetLang = tgt;
                }
            }
            else
            {
                 Parameter = existingEntry.Parameter?.Value ?? "";
            }
        }
        else if (AvailableActions.Any())
        {
            SelectedAction = AvailableActions.First();
        }

        UpdateAvailableParameterOptions();

        ToggleRecordingCommand = ReactiveCommand.Create(() =>
        {
            IsRecording = !IsRecording;
            IsRecordingBeforeInputKey = false;
            IsRecordingAfterInputKey = false;
            if (IsRecording)
                KeyCombination = "";
            else if (string.IsNullOrEmpty(KeyCombination) && _existingEntry != null)
                // Restore if cancelled/empty
                KeyCombination = _existingEntry.KeyCombination;
        });

        ToggleBeforeInputKeyRecordingCommand = ReactiveCommand.Create(() =>
            StartInputKeyRecording(beforeInput: true));
        ToggleAfterInputKeyRecordingCommand = ReactiveCommand.Create(() =>
            StartInputKeyRecording(beforeInput: false));
        ClearBeforeInputKeyCommand = ReactiveCommand.Create(() =>
        {
            InputTranslateBeforeKey = string.Empty;
            StopRecording();
        });
        ClearAfterInputKeyCommand = ReactiveCommand.Create(() =>
        {
            InputTranslateAfterKey = string.Empty;
            StopRecording();
        });

        var canSave = this.WhenAnyValue(
            x => x.KeyCombination,
            x => x.SelectedAction,
            x => x.Parameter,
            x => x.SelectedEngineOption,
            (key, action, param, option) =>
            {
                if (string.IsNullOrEmpty(key)) return false;
                if (!action.RequiresParameter) return true;
                
                if (IsComplexSwitchAction)
                {
                    // For complex action, check selected engine/langs instead of param string
                    return option != null && _selectedSourceLang != null && _selectedTargetLang != null;
                }

                return !string.IsNullOrWhiteSpace(param);
            });

        SaveCommand = ReactiveCommand.Create(() =>
        {
            OnClose?.Invoke(GetResult());
            _dialog.Dismiss();
        }, canSave);

        CancelCommand = ReactiveCommand.Create(() =>
        {
            OnClose?.Invoke(null);
            _dialog.Dismiss();
        });
    }

    public string Title => _existingEntry == null
        ? $"{Resources.Add} {Resources.Shortcut}"
        : $"{Resources.Edit} {Resources.Shortcut}";

    public string ButtonText => _existingEntry == null ? Resources.Add : Resources.Save;
    public MaterialIconKind Icon => _existingEntry == null ? MaterialIconKind.Plus : MaterialIconKind.Edit;

    public IShortcutActionDefinition SelectedAction
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(IsComplexSwitchAction));
            this.RaisePropertyChanged(nameof(IsTextAssistAction));
            this.RaisePropertyChanged(nameof(IsInputTranslateAction));
            UpdateAvailableParameterOptions();

            if (_existingEntry == null || _existingEntry.ActionType != value.ActionType)
            {
                Parameter = "";
                ReadSelectedText = false;
                ReplaceCurrentInput = false;
            }
        }
    } = ShortcutActionDefinition.AvailableActions.First();

    public string Parameter
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "";

    public IEnumerable<string>? AvailableParameterOptions
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public string KeyCombination
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "";

    public bool IsRecording
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public bool IsRecordingBeforeInputKey
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(IsNotRecordingBeforeInputKey));
        }
    }

    public bool IsNotRecordingBeforeInputKey => !IsRecordingBeforeInputKey;

    public bool IsRecordingAfterInputKey
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(IsNotRecordingAfterInputKey));
        }
    }

    public bool IsNotRecordingAfterInputKey => !IsRecordingAfterInputKey;

    public string InputTranslateBeforeKey
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    public string InputTranslateAfterKey
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    public bool ReplaceCurrentInput
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public bool ReadSelectedText
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public IShortcutActionDefinition[] AvailableActions { get; }

    public ReactiveCommand<Unit, Unit> ToggleRecordingCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleBeforeInputKeyRecordingCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleAfterInputKeyRecordingCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearBeforeInputKeyCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearAfterInputKeyCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }

    public Action<ShortcutEntry?>? OnClose { get; set; }

    public ShortcutEntry GetResult()
    {
        return new ShortcutEntry
        {
            ActionType = SelectedAction.ActionType,
            Parameter = GetShortcutParameter(),
            KeyCombination = KeyCombination,
            IsEnabled = _existingEntry?.IsEnabled ?? true
        };
    }

    private ShortcutParameter GetShortcutParameter()
    {
        if (IsComplexSwitchAction)
        {
            return new ShortcutParameter
            {
                Engine = SelectedEngineOption?.Name ?? "",
                EngineId = SelectedEngineOption?.Id,
                Source = SelectedSourceLang,
                Target = SelectedTargetLang
            };
        }

        return new ShortcutParameter
        {
            Value = Parameter,
            ReadSelectedText = IsTextAssistAction ? ReadSelectedText : null,
            InputTranslateBeforeKey = IsInputTranslateAction
                ? NullIfWhiteSpace(InputTranslateBeforeKey)
                : null,
            InputTranslateAfterKey = IsInputTranslateAction
                ? NullIfWhiteSpace(InputTranslateAfterKey)
                : null,
            ReplaceCurrentInput = IsInputTranslateAction ? ReplaceCurrentInput : null
        };
    }

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

    private void StartInputKeyRecording(bool beforeInput)
    {
        IsRecording = false;
        IsRecordingBeforeInputKey = beforeInput;
        IsRecordingAfterInputKey = !beforeInput;
    }

    private static string? NullIfWhiteSpace(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private void UpdateAvailableParameterOptions()
    {
        AvailableParameterOptions = SelectedAction.GetParameterOptions(_configurationService);
    }



    private void UpdateAvailableLanguages()
    {
        var allLanguages = LanguageService.GetAllLanguages();

        // Safe check for null
        if (SelectedEngineOption == null)
        {
             AvailableLanguages = allLanguages.ToList();
             return;
        }

        if (SelectedEngineOption.IsMachine)
        {
            AvailableLanguages = allLanguages
                .Where(l => SelectedEngineOption.Id != null && (l.Id == "auto" || !string.IsNullOrEmpty(l.GetCode(SelectedEngineOption.Id))))
                .ToList();
        }
        else
        {
            // AI Model - assume all valid
            AvailableLanguages = allLanguages.ToList();
        }

        // Re-validate selections
        var currentSourceId = SelectedSourceLang?.Id ?? "auto";
        var currentTargetId = SelectedTargetLang?.Id ?? "zh-Hans";

        var newSource = AvailableLanguages.FirstOrDefault(l => l.Id == currentSourceId);
        SelectedSourceLang = newSource ?? AvailableLanguages.FirstOrDefault(l => l.Id == "auto") ?? AvailableLanguages.FirstOrDefault()!;

        var newTarget = AvailableLanguages.FirstOrDefault(l => l.Id == currentTargetId);
        SelectedTargetLang = newTarget ?? AvailableLanguages.FirstOrDefault(l => l.Id == "zh-Hans") ?? AvailableLanguages.FirstOrDefault()!;
    }
}
