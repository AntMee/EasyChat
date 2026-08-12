using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Reactive;
using EasyChat.Contracts.Platform;
using EasyChat.Presentation.Features.Settings.State;
using EasyChat.Presentation.Foundation.Navigation;
using ReactiveUI;
using ShadUI;

namespace EasyChat.Presentation.Features.Settings;

/// <summary>
/// Second-level dialog that manages the selection blacklist/whitelist. It edits the live
/// application list directly (add/remove) and opens the running-app picker for additions, so
/// the settings page stays compact even with a long list.
/// </summary>
public sealed class SelectionAppListDialogViewModel : ConventionViewModelBase
{
    private readonly DialogManager _dialogManager;
    private readonly SettingsSession _settings;
    private readonly IRunningProcessCatalog _runningProcesses;

    public SelectionAppListDialogViewModel(
        DialogManager dialogManager,
        SettingsSession settings,
        IRunningProcessCatalog runningProcesses)
    {
        _dialogManager = dialogManager;
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _runningProcesses = runningProcesses ?? throw new ArgumentNullException(nameof(runningProcesses));
        Apps = settings.SelectionTranslation.AppList;
        Apps.CollectionChanged += (_, _) =>
        {
            this.RaisePropertyChanged(nameof(HasApps));
            this.RaisePropertyChanged(nameof(HasNoApps));
        };
        AddRunningAppsCommand = ReactiveCommand.Create(AddRunningApps);
        RemoveAppCommand = ReactiveCommand.Create<SelectionAppItemState>(RemoveApp);
        CloseCommand = ReactiveCommand.Create(() => dialogManager.Close(this));
    }

    public ObservableCollection<SelectionAppItemState> Apps { get; }
    public bool HasApps => Apps.Count > 0;
    public bool HasNoApps => !HasApps;
    public ReactiveCommand<Unit, Unit> AddRunningAppsCommand { get; }
    public ReactiveCommand<SelectionAppItemState, Unit> RemoveAppCommand { get; }
    public ReactiveCommand<Unit, Unit> CloseCommand { get; }

    private void AddRunningApps()
    {
        var picker = new RunningAppPickerDialogViewModel(_dialogManager, _runningProcesses)
        {
            OnClose = descriptors =>
            {
                if (descriptors.Count == 0)
                    return;

                var existing = Apps
                    .Select(app => app.Identifier)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var descriptor in descriptors)
                {
                    if (existing.Add(descriptor.Identifier))
                    {
                        Apps.Add(new SelectionAppItemState(
                            descriptor.Identifier,
                            descriptor.Name,
                            descriptor.Description,
                            descriptor.IconPng));
                    }
                }
            }
        };
        _dialogManager.CreateDialog(picker).Show();
    }

    private void RemoveApp(SelectionAppItemState item) => Apps.Remove(item);
}
