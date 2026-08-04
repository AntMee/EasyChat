using System.Collections.Concurrent;
using System.Net;
using System.Runtime.Versioning;
using EasyChat.Contracts.ApplicationData;
using EasyChat.Contracts.Ocr;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using Sdcb.OpenVINO;
using Sdcb.OpenVINO.PaddleOCR;
using Sdcb.OpenVINO.PaddleOCR.Models;
using Sdcb.OpenVINO.PaddleOCR.Models.Online;

namespace EasyChat.Infrastructure.Windows.Ocr;

[SupportedOSPlatform("windows")]
internal sealed class OpenVinoWindowsOcrBackend : IWindowsOcrBackend
{
    private static readonly SemaphoreSlim ModelMutationGate = new(1, 1);
    private static readonly object DownloadProxyLock = new();
    private static string? _configuredDownloadProxy;

    private readonly ConcurrentDictionary<string, Lazy<PaddleEngineHandle>> _engines =
        new(StringComparer.Ordinal);
    private readonly IApplicationDataPaths _applicationData;
    private readonly ILogger<WindowsOpenVinoOcr>? _logger;
    private bool _disposed;

    public OpenVinoWindowsOcrBackend(
        IApplicationDataPaths applicationData,
        ILogger<WindowsOpenVinoOcr>? logger)
    {
        _applicationData = applicationData ?? throw new ArgumentNullException(nameof(applicationData));
        _logger = logger;
        ApplyModelDirectory();
        _applicationData.LocationChanged += OnApplicationDataLocationChanged;
    }

    public bool IsModelAvailable(OpenVinoOcrModelPackageSpec package)
    {
        ArgumentNullException.ThrowIfNull(package);
        ObjectDisposedException.ThrowIf(_disposed, this);
        ModelMutationGate.Wait();
        try
        {
            ApplyModelDirectory();
            return IsModelAvailableCore(package);
        }
        finally
        {
            ModelMutationGate.Release();
        }
    }

    public async Task DownloadModelAsync(
        OpenVinoOcrModelPackageSpec package,
        OcrModelDownloadOptions options,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(options);
        ObjectDisposedException.ThrowIf(_disposed, this);
        await ModelMutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ApplyModelDirectory();
            ConfigureDownloadProxy(options.ProxyUrl, options.UseProxy);
            if (IsModelAvailableCore(package))
            {
                progress?.Report(1);
                return;
            }

            var model = package.CreateOnlineModel();
            _logger?.LogInformation("Downloading OCR model package {PackageId}...", package.Package.Id);
            progress?.Report(0);
            await DownloadComponentAsync(
                package,
                model.DetModel.RootDirectory,
                requireYaml: false,
                async token => await model.DetModel.DownloadAsync(token).ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);
            progress?.Report(1d / 3d);
            if (model.ClsModel is { } clsModel)
            {
                await DownloadComponentAsync(
                    package,
                    clsModel.RootDirectory,
                    requireYaml: false,
                    async token => await clsModel.DownloadAsync(token).ConfigureAwait(false),
                    cancellationToken).ConfigureAwait(false);
            }
            progress?.Report(2d / 3d);
            await DownloadComponentAsync(
                package,
                model.RecModel.RootDirectory,
                requireYaml: package.Format == OpenVinoOcrModelFormat.OnnxV6,
                async token => await model.RecModel.DownloadAsync(token).ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);

            if (!IsModelAvailableCore(package))
                throw new InvalidDataException($"OCR model package '{package.Package.Id}' is incomplete.");
            progress?.Report(1);
        }
        finally
        {
            ModelMutationGate.Release();
        }
    }

    public void DeleteModel(OpenVinoOcrModelPackageSpec package)
    {
        ArgumentNullException.ThrowIfNull(package);
        ObjectDisposedException.ThrowIf(_disposed, this);
        ModelMutationGate.Wait();
        try
        {
            ApplyModelDirectory();
            RemoveEngineCore(package.Package.Id);
            var model = package.CreateOnlineModel();
            foreach (var root in GetModelRoots(model).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(root) || IsRootUsedByAnotherInstalledPackage(root, package.Package.Id))
                    continue;

                EnsureModelRoot(root);
                Directory.Delete(root, recursive: true);
            }
        }
        finally
        {
            ModelMutationGate.Release();
        }
    }

    public IReadOnlyList<WindowsOcrBackendRegion> Recognize(
        Mat image,
        WindowsOcrLanguageSelection language,
        bool enableRotation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(language);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        PaddleEngineHandle handle;
        ModelMutationGate.Wait(cancellationToken);
        try
        {
            ApplyModelDirectory();
            handle = GetOrCreateEngineCore(language);
            Monitor.Enter(handle.Gate);
        }
        finally
        {
            ModelMutationGate.Release();
        }

        try
        {
            var oldRotate = handle.Engine.AllowRotateDetection;
            handle.Engine.AllowRotateDetection = enableRotation;
            try
            {
                var result = handle.Engine.Run(image);
                cancellationToken.ThrowIfCancellationRequested();
                return result.Regions
                    .Select(region => new WindowsOcrBackendRegion(
                        region.Text,
                        region.Rect.Points()
                            .Select(point => new WindowsOcrPoint(point.X, point.Y))
                            .ToArray(),
                        region.Rect.Angle,
                        region.Score))
                    .ToArray();
            }
            finally
            {
                handle.Engine.AllowRotateDetection = oldRotate;
            }
        }
        finally
        {
            Monitor.Exit(handle.Gate);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _applicationData.LocationChanged -= OnApplicationDataLocationChanged;
        ModelMutationGate.Wait();
        try
        {
            if (_disposed)
                return;
            _disposed = true;
            DisposeEnginesCore();
        }
        finally
        {
            ModelMutationGate.Release();
        }

        _logger?.LogDebug("Windows OpenVINO PaddleOCR backend disposed.");
    }

    internal static bool IsPaddleModelComplete(string rootDirectory) =>
        IsNonEmptyFile(Path.Combine(rootDirectory, "inference.pdiparams"))
        && (IsNonEmptyFile(Path.Combine(rootDirectory, "inference.pdmodel"))
            || IsNonEmptyFile(Path.Combine(rootDirectory, "inference.json")));

    internal static bool IsOnnxModelComplete(string rootDirectory, bool requireYaml = false) =>
        IsNonEmptyFile(Path.Combine(rootDirectory, "inference.onnx"))
        && (!requireYaml || IsNonEmptyFile(Path.Combine(rootDirectory, "inference.yml")));

    private void OnApplicationDataLocationChanged(
        object? sender,
        ApplicationDataLocationChangedEventArgs args)
    {
        ModelMutationGate.Wait();
        try
        {
            if (_disposed)
                return;
            DisposeEnginesCore();
            ApplyModelDirectory();
        }
        finally
        {
            ModelMutationGate.Release();
        }
    }

    private void ApplyModelDirectory() =>
        Settings.GlobalModelDirectory = _applicationData.OcrModelsDirectory;

    private bool IsModelAvailableCore(OpenVinoOcrModelPackageSpec package)
    {
        var model = package.CreateOnlineModel();
        if (package.Format == OpenVinoOcrModelFormat.OnnxV6)
        {
            return IsOnnxModelComplete(model.DetModel.RootDirectory)
                && (model.ClsModel is null || IsOnnxModelComplete(model.ClsModel.RootDirectory))
                && IsOnnxModelComplete(model.RecModel.RootDirectory, requireYaml: true);
        }

        return IsPaddleModelComplete(model.DetModel.RootDirectory)
            && (model.ClsModel is null || IsPaddleModelComplete(model.ClsModel.RootDirectory))
            && IsPaddleModelComplete(model.RecModel.RootDirectory);
    }

    internal async Task DownloadComponentAsync(
        OpenVinoOcrModelPackageSpec package,
        string rootDirectory,
        bool requireYaml,
        Func<CancellationToken, Task> download,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsComponentComplete(package.Format, rootDirectory, requireYaml))
            return;

        if (Directory.Exists(rootDirectory))
        {
            EnsureModelRoot(rootDirectory);
            Directory.Delete(rootDirectory, recursive: true);
        }

        await download(cancellationToken).ConfigureAwait(false);
        if (!IsComponentComplete(package.Format, rootDirectory, requireYaml))
            throw new InvalidDataException($"Downloaded OCR model component is incomplete: '{rootDirectory}'.");
    }

    private static bool IsComponentComplete(
        OpenVinoOcrModelFormat format,
        string rootDirectory,
        bool requireYaml) =>
        format == OpenVinoOcrModelFormat.OnnxV6
            ? IsOnnxModelComplete(rootDirectory, requireYaml)
            : IsPaddleModelComplete(rootDirectory);

    private static bool IsNonEmptyFile(string path) =>
        File.Exists(path) && new FileInfo(path).Length > 0;

    private PaddleEngineHandle GetOrCreateEngineCore(WindowsOcrLanguageSelection language)
    {
        var packageId = language.Package.Package.Id;
        var lazyEngine = _engines.GetOrAdd(
            packageId,
            _ => new Lazy<PaddleEngineHandle>(
                () => CreateEngine(language),
                LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return lazyEngine.Value;
        }
        catch
        {
            _engines.TryRemove(packageId, out _);
            throw;
        }
    }

    private PaddleEngineHandle CreateEngine(WindowsOcrLanguageSelection language)
    {
        var package = language.Package;
        _logger?.LogInformation(
            "Initializing Windows OpenVINO PaddleOCR package {PackageId} for {Language}...",
            package.Package.Id,
            language.Language.DisplayName);
        if (!IsModelAvailableCore(package))
            throw new OcrModelNotDownloadedException(language.Language);

        var online = package.CreateOnlineModel();
        var detection = online.DetModel.DownloadAsync().GetAwaiter().GetResult();
        var classification = online.ClsModel?.DownloadAsync().GetAwaiter().GetResult();
        var recognition = online.RecModel.DownloadAsync().GetAwaiter().GetResult();
        var model = classification is null
            ? new FullOcrModel(detection, recognition)
            : new FullOcrModel(detection, classification, recognition);

        var engine = new PaddleOcrAll(model, new PaddleOcrOptions(new DeviceOptions("CPU")))
        {
            AllowRotateDetection = false,
            Enable180Classification = true
        };
        return new PaddleEngineHandle(engine);
    }

    private bool IsRootUsedByAnotherInstalledPackage(string root, string excludedPackageId)
    {
        var normalizedRoot = NormalizePath(root);
        foreach (var other in OpenVinoOcrModelCatalog.Specs)
        {
            if (string.Equals(other.Package.Id, excludedPackageId, StringComparison.Ordinal)
                || !IsModelAvailableCore(other))
            {
                continue;
            }

            if (GetModelRoots(other.CreateOnlineModel())
                .Select(NormalizePath)
                .Contains(normalizedRoot, StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> GetModelRoots(OnlineFullModels model)
    {
        yield return model.DetModel.RootDirectory;
        if (model.ClsModel is { } clsModel)
            yield return clsModel.RootDirectory;
        yield return model.RecModel.RootDirectory;
    }

    private void EnsureModelRoot(string root)
    {
        var modelDirectory = NormalizePath(_applicationData.OcrModelsDirectory);
        var candidate = NormalizePath(root);
        if (!candidate.StartsWith(
                modelDirectory + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"OCR model root is outside the configured directory: {root}");
        }
    }

    private static string NormalizePath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static void ConfigureDownloadProxy(string? proxyUrl, bool useProxy)
    {
        var key = useProxy && !string.IsNullOrWhiteSpace(proxyUrl)
            ? $"proxy:{proxyUrl}"
            : "direct";
        lock (DownloadProxyLock)
        {
            if (_configuredDownloadProxy == key)
                return;
            HttpClient.DefaultProxy = key == "direct"
                ? new WebProxy()
                : new WebProxy(proxyUrl!);
            _configuredDownloadProxy = key;
        }
    }

    private void RemoveEngineCore(string packageId)
    {
        if (!_engines.TryRemove(packageId, out var lazyEngine) || !lazyEngine.IsValueCreated)
            return;
        lock (lazyEngine.Value.Gate)
            lazyEngine.Value.Dispose();
    }

    private void DisposeEnginesCore()
    {
        foreach (var packageId in _engines.Keys.ToArray())
            RemoveEngineCore(packageId);
        _engines.Clear();
    }

    private sealed class PaddleEngineHandle(PaddleOcrAll engine) : IDisposable
    {
        internal object Gate { get; } = new();
        internal PaddleOcrAll Engine { get; } = engine;
        public void Dispose() => Engine.Dispose();
    }
}
