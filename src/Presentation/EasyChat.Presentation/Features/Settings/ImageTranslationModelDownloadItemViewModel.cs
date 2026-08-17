using EasyChat.Contracts.ImageTranslation;
using EasyChat.Presentation.Lang;
using ReactiveUI;

namespace EasyChat.Presentation.Features.Settings;

public sealed class ImageTranslationModelDownloadItemViewModel : ReactiveObject
{
    private bool _isDownloaded;
    private bool _isDownloading;
    private bool _isFailed;
    private double _progress;
    private string? _errorMessage;
    private readonly string _displayName;
    private readonly string _description;

    public ImageTranslationModelDownloadItemViewModel(
        ImageTranslationModelPackage package,
        string displayName,
        string description,
        bool isDownloaded)
    {
        Package = package ?? throw new ArgumentNullException(nameof(package));
        _displayName = displayName ?? throw new ArgumentNullException(nameof(displayName));
        _description = description ?? throw new ArgumentNullException(nameof(description));
        _isDownloaded = isDownloaded;
        _progress = isDownloaded ? 1 : 0;
    }

    public ImageTranslationModelPackage Package { get; }
    public string DisplayName => _displayName;
    public string Description => _description;

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
    public bool IsProgressVisible => true;
    public bool IsProgressIndeterminate => IsDownloading && Progress <= 0;
    public string ProgressText => IsDownloading && Progress > 0 ? $"{Progress:P0}" : string.Empty;
    public string ActionText => IsFailed
        ? Text("ImageTranslationModelRetry", "Retry")
        : Text("ImageTranslationModelDownload", "Download");

    public string StatusText
    {
        get
        {
            if (IsDownloaded) return Text("ImageTranslationModelDownloaded", "Downloaded");
            if (IsDownloading) return Text("ImageTranslationModelDownloading", "Downloading");
            if (IsFailed) return Text("ImageTranslationModelDownloadFailed", "Download failed");
            return Text("ImageTranslationModelNotDownloaded", "Not downloaded");
        }
    }

    public void StartDownload()
    {
        IsFailed = false;
        ErrorMessage = null;
        Progress = 0;
        IsDownloading = true;
    }

    public void SetProgress(double value) => Progress = Math.Clamp(value, 0, 1);

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

    public void MarkDeleted()
    {
        IsDownloaded = false;
        IsFailed = false;
        ErrorMessage = null;
        Progress = 0;
    }

    public void FailDownload(string message)
    {
        IsDownloading = false;
        IsFailed = true;
        ErrorMessage = message;
    }

    private static string Text(string key, string fallback) =>
        Resources.ResourceManager.GetString(key, Resources.Culture) ?? fallback;
}
