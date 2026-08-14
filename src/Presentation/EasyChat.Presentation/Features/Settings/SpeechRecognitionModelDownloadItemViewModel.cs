using EasyChat.Contracts.Speech;
using EasyChat.Presentation.Lang;
using ReactiveUI;

namespace EasyChat.Presentation.Features.Settings;

public sealed class SpeechRecognitionModelDownloadItemViewModel : ReactiveObject
{
    private bool _isDownloaded;
    private bool _isDownloading;
    private bool _isFailed;
    private double _progress;
    private string? _errorMessage;

    public SpeechRecognitionModelDownloadItemViewModel(
        SpeechRecognitionModelDownloadPackage package,
        bool isDownloaded)
    {
        Package = package ?? throw new ArgumentNullException(nameof(package));
        Model = package.Model;
        _isDownloaded = isDownloaded;
        _progress = isDownloaded ? 1 : 0;
    }

    public SpeechRecognitionModelDownloadPackage Package { get; }
    public SpeechRecognitionModel Model { get; }
    public string DisplayName => Model.DisplayName;
    public string Id => Package.Id;

    public bool IsDownloaded
    {
        get => _isDownloaded;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isDownloaded, value);
            this.RaisePropertyChanged(nameof(IsActionVisible));
            this.RaisePropertyChanged(nameof(IsDeleteVisible));
            this.RaisePropertyChanged(nameof(StatusText));
        }
    }

    public bool IsDownloading
    {
        get => _isDownloading;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isDownloading, value);
            this.RaisePropertyChanged(nameof(IsActionVisible));
            this.RaisePropertyChanged(nameof(IsCancelVisible));
            this.RaisePropertyChanged(nameof(IsDeleteVisible));
            this.RaisePropertyChanged(nameof(IsProgressIndeterminate));
            this.RaisePropertyChanged(nameof(ProgressText));
            this.RaisePropertyChanged(nameof(StatusText));
        }
    }

    public bool IsFailed
    {
        get => _isFailed;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isFailed, value);
            this.RaisePropertyChanged(nameof(ActionText));
            this.RaisePropertyChanged(nameof(StatusText));
        }
    }

    public double Progress
    {
        get => _progress;
        private set
        {
            this.RaiseAndSetIfChanged(ref _progress, value);
            this.RaisePropertyChanged(nameof(ProgressText));
            this.RaisePropertyChanged(nameof(IsProgressIndeterminate));
        }
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => this.RaiseAndSetIfChanged(ref _errorMessage, value);
    }

    public bool IsActionVisible => !IsDownloading && !IsDownloaded;
    public bool IsCancelVisible => IsDownloading;
    public bool IsDeleteVisible => IsDownloaded && !IsDownloading;
    public bool IsProgressIndeterminate => IsDownloading && Progress <= 0;
    public string ProgressText => IsDownloading && Progress > 0 ? $"{Progress:P0}" : string.Empty;
    public string ActionText => IsFailed ? Resources.RetryAsrModel : Resources.DownloadAsrModel;

    public string StatusText =>
        IsDownloaded ? Resources.AsrModelDownloaded :
        IsDownloading ? Resources.AsrModelDownloading :
        IsFailed ? Resources.AsrModelDownloadFailed :
        Resources.AsrModelNotDownloaded;

    public void StartDownload()
    {
        IsFailed = false;
        ErrorMessage = null;
        Progress = 0;
        IsDownloading = true;
    }

    public void SetProgress(double value) => Progress = value;

    public void CompleteDownload()
    {
        Progress = 1;
        IsDownloaded = true;
        IsDownloading = false;
        IsFailed = false;
    }

    public void CancelDownload()
    {
        IsDownloading = false;
        Progress = 0;
    }

    public void SyncDownloaded(bool isDownloaded)
    {
        if (IsDownloading)
            return;

        IsDownloaded = isDownloaded;
        if (isDownloaded)
        {
            Progress = 1;
            IsFailed = false;
            ErrorMessage = null;
        }
        else
        {
            Progress = 0;
        }
    }

    public void FailDownload(string message)
    {
        IsDownloading = false;
        IsFailed = true;
        ErrorMessage = message;
    }
}
