using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Reactive;
using EasyChat.Contracts.Platform;
using EasyChat.Presentation.Lang;
using EasyChat.Presentation.Foundation.Navigation;
using ReactiveUI;
using ShadUI;

namespace EasyChat.Presentation.Features.Settings;

/// <summary>A running application row in the picker dialog.</summary>
public sealed class RunningAppItemViewModel(RunningProcessDescriptor descriptor) : ReactiveObject
{
    private bool _isSelected;

    public RunningProcessDescriptor Descriptor { get; } = descriptor;
    public string Name => Descriptor.Name;
    public string? WindowTitle => Descriptor.WindowTitle;

    /// <summary>
    /// Secondary line: the stable software description when available, falling back to the volatile
    /// window title so changing browser tabs do not become the primary identity of an entry.
    /// </summary>
    public string Subtitle => string.IsNullOrWhiteSpace(Descriptor.Description)
        ? (Descriptor.WindowTitle ?? string.Empty)
        : Descriptor.Description;

    public ReadOnlyMemory<byte> IconPng => Descriptor.IconPng;

    public bool IsSelected
    {
        get => _isSelected;
        set => this.RaiseAndSetIfChanged(ref _isSelected, value);
    }
}

/// <summary>
/// Dialog that lists interactive applications currently running so the user can pick the ones to
/// add to the selection blacklist/whitelist. Selection is a session snapshot; only the process
/// identifiers are handed back to the caller.
/// </summary>
public sealed class RunningAppPickerDialogViewModel : ConventionViewModelBase
{
    private readonly DialogManager _dialogManager;
    private readonly IRunningProcessCatalog _runningProcesses;
    private string _searchText = string.Empty;
    private bool _isLoading = true;
    private bool _hasSelection;
    private string? _errorMessage;

    public RunningAppPickerDialogViewModel(
        DialogManager dialogManager,
        IRunningProcessCatalog runningProcesses)
    {
        _dialogManager = dialogManager;
        _runningProcesses = runningProcesses ?? throw new ArgumentNullException(nameof(runningProcesses));
        AllApps = [];
        FilteredApps = [];
        AllApps.CollectionChanged += (_, _) => RefreshFilteredApps();
        FilteredApps.CollectionChanged += OnFilteredAppsChanged;
        ConfirmCommand = ReactiveCommand.Create(
            Confirm,
            this.WhenAnyValue(
                viewModel => viewModel.IsLoading,
                viewModel => viewModel.HasSelection,
                (loading, hasSelection) => !loading && hasSelection));
        CancelCommand = ReactiveCommand.Create(Cancel);
        ToggleAppSelectionCommand = ReactiveCommand.Create<RunningAppItemViewModel>(
            item => item.IsSelected = !item.IsSelected);
        _ = LoadAsync();
    }

    public ObservableCollection<RunningAppItemViewModel> AllApps { get; }
    public ObservableCollection<RunningAppItemViewModel> FilteredApps { get; }
    public ReactiveCommand<Unit, Unit> ConfirmCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }
    public ReactiveCommand<RunningAppItemViewModel, Unit> ToggleAppSelectionCommand { get; }
    public Action<IReadOnlyList<RunningProcessDescriptor>>? OnClose { get; init; }

    public bool IsLoading
    {
        get => _isLoading;
        private set => this.RaiseAndSetIfChanged(ref _isLoading, value);
    }

    public bool HasLoaded => !IsLoading && ErrorMessage is null;
    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            this.RaiseAndSetIfChanged(ref _errorMessage, value);
            this.RaisePropertyChanged(nameof(HasLoaded));
            this.RaisePropertyChanged(nameof(HasApps));
            this.RaisePropertyChanged(nameof(HasNoApps));
            this.RaisePropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => ErrorMessage is not null;
    public bool HasApps => AllApps.Count > 0;
    public bool HasNoApps => HasLoaded && AllApps.Count == 0;
    public bool HasFilteredApps => FilteredApps.Count > 0;
    public bool HasNoFilteredResults => HasLoaded && AllApps.Count > 0 && FilteredApps.Count == 0;

    public bool HasSelection
    {
        get => _hasSelection;
        private set => this.RaiseAndSetIfChanged(ref _hasSelection, value);
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (string.Equals(_searchText, value, StringComparison.Ordinal))
                return;
            this.RaiseAndSetIfChanged(ref _searchText, value);
            RefreshFilteredApps();
        }
    }

    private async Task LoadAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var apps = await _runningProcesses.GetRunningProcessesAsync().ConfigureAwait(true);
            foreach (var app in apps.OrderBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase))
                AllApps.Add(new RunningAppItemViewModel(app));
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void RefreshFilteredApps()
    {
        var query = SearchText.Trim();
        var matches = string.IsNullOrEmpty(query)
            ? AllApps
            : AllApps.Where(app => app.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                                   || (app.WindowTitle?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false));

        FilteredApps.Clear();
        foreach (var app in matches)
            FilteredApps.Add(app);

        this.RaisePropertyChanged(nameof(HasFilteredApps));
        this.RaisePropertyChanged(nameof(HasNoFilteredResults));
    }

    private void OnFilteredAppsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (RunningAppItemViewModel app in e.OldItems)
                app.PropertyChanged -= OnAppPropertyChanged;
        }

        if (e.NewItems is not null)
        {
            foreach (RunningAppItemViewModel app in e.NewItems)
                app.PropertyChanged += OnAppPropertyChanged;
        }

        RefreshHasSelection();
    }

    private void OnAppPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RunningAppItemViewModel.IsSelected))
            RefreshHasSelection();
    }

    private void RefreshHasSelection() =>
        HasSelection = FilteredApps.Any(app => app.IsSelected);

    private void Confirm()
    {
        var selected = FilteredApps
            .Where(app => app.IsSelected)
            .Select(app => app.Descriptor)
            .ToArray();
        OnClose?.Invoke(selected);
        _dialogManager.Close(this);
    }

    private void Cancel()
    {
        OnClose?.Invoke([]);
        _dialogManager.Close(this);
    }
}
