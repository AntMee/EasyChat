using System.Collections.Concurrent;
#if !BUNDLED_OCR_MODELS
using System.Net;
#endif
using System.Runtime.Versioning;
using EasyChat.Contracts.ApplicationData;
using EasyChat.Contracts.Ocr;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using Sdcb.PaddleInference;
using Sdcb.PaddleOCR;
using Sdcb.PaddleOCR.Models;
#if BUNDLED_OCR_MODELS
using Sdcb.PaddleOCR.Models.Local;
#else
using Sdcb.PaddleOCR.Models.Online;
#endif

namespace EasyChat.Infrastructure.Windows.Ocr;

[SupportedOSPlatform("windows")]
internal sealed class PaddleWindowsOcrBackend : IWindowsOcrBackend
{
    private readonly ConcurrentDictionary<WindowsOcrModel, Lazy<PaddleEngineHandle>> _engines = new();
    private readonly IApplicationDataPaths _applicationData;
    private readonly ILogger<WindowsPaddleOcr>? _logger;
#if !BUNDLED_OCR_MODELS
    private static readonly object DownloadProxyLock = new();
    private static string? _configuredDownloadProxy;
#endif

#if BUNDLED_OCR_MODELS
    private static readonly IReadOnlyDictionary<WindowsOcrModel, Func<FullOcrModel>> ModelFactories =
        new Dictionary<WindowsOcrModel, Func<FullOcrModel>>
        {
            [WindowsOcrModel.ChineseSimplified] = () => LocalFullModels.ChineseV5,
            [WindowsOcrModel.ChineseTraditional] = () => LocalFullModels.TraditionalChineseV3,
            [WindowsOcrModel.English] = () => LocalFullModels.EnglishV4,
            [WindowsOcrModel.Japanese] = () => LocalFullModels.JapanV4,
            [WindowsOcrModel.Korean] = () => LocalFullModels.KoreanV4,
            [WindowsOcrModel.Arabic] = () => LocalFullModels.ArabicV4,
            [WindowsOcrModel.Devanagari] = () => LocalFullModels.DevanagariV4,
            [WindowsOcrModel.Tamil] = () => LocalFullModels.TamilV4,
            [WindowsOcrModel.Telugu] = () => LocalFullModels.TeluguV4,
            [WindowsOcrModel.Kannada] = () => LocalFullModels.KannadaV4
        };
#else
    private static readonly IReadOnlyDictionary<WindowsOcrModel, Func<OnlineFullModels>> ModelFactories =
        new Dictionary<WindowsOcrModel, Func<OnlineFullModels>>
        {
            [WindowsOcrModel.ChineseSimplified] = () => OnlineFullModels.ChineseV5,
            [WindowsOcrModel.ChineseTraditional] = () => OnlineFullModels.TraditionalChineseV3,
            [WindowsOcrModel.English] = () => OnlineFullModels.EnglishV4,
            [WindowsOcrModel.Japanese] = () => OnlineFullModels.JapanV4,
            [WindowsOcrModel.Korean] = () => OnlineFullModels.KoreanV4,
            [WindowsOcrModel.Arabic] = () => OnlineFullModels.ArabicV4,
            [WindowsOcrModel.Devanagari] = () => OnlineFullModels.DevanagariV4,
            [WindowsOcrModel.Tamil] = () => OnlineFullModels.TamilV4,
            [WindowsOcrModel.Telugu] = () => OnlineFullModels.TeluguV4,
            [WindowsOcrModel.Kannada] = () => OnlineFullModels.KannadaV4
        };
#endif

    public PaddleWindowsOcrBackend(
        IApplicationDataPaths applicationData,
        ILogger<WindowsPaddleOcr>? logger)
    {
        _applicationData = applicationData ?? throw new ArgumentNullException(nameof(applicationData));
        _logger = logger;
#if !BUNDLED_OCR_MODELS
        ApplyModelDirectory();
#endif
        _applicationData.LocationChanged += OnApplicationDataLocationChanged;
    }

#if BUNDLED_OCR_MODELS
    public bool CanDeleteModels => false;
#else
    public bool CanDeleteModels => true;
#endif

    public bool IsModelAvailable(WindowsOcrLanguageSelection language)
    {
        ArgumentNullException.ThrowIfNull(language);
#if !BUNDLED_OCR_MODELS
        ApplyModelDirectory();
#endif
#if BUNDLED_OCR_MODELS
        return true;
#else
        var model = GetModel(language.Model);
        return File.Exists(Path.Combine(model.DetModel.RootDirectory, "inference.pdiparams"))
            && (model.ClsModel is null
                || File.Exists(Path.Combine(model.ClsModel.RootDirectory, "inference.pdiparams")))
            && File.Exists(Path.Combine(model.RecModel.RootDirectory, "inference.pdiparams"));
#endif
    }

    public async Task DownloadModelAsync(
        WindowsOcrLanguageSelection language,
        OcrModelDownloadOptions options,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(language);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
#if BUNDLED_OCR_MODELS
        progress?.Report(1);
        await Task.CompletedTask;
#else
        ApplyModelDirectory();
        ConfigureDownloadProxy(options.ProxyUrl, options.UseProxy);
        var model = GetModel(language.Model);
        _logger?.LogInformation(
            "Downloading OCR model for {Language}...",
            language.Language.DisplayName);

        progress?.Report(0);
        await model.DetModel.DownloadAsync(cancellationToken);
        progress?.Report(1d / 3d);
        if (model.ClsModel is { } clsModel)
            await clsModel.DownloadAsync(cancellationToken);
        progress?.Report(2d / 3d);
        await model.RecModel.DownloadAsync(cancellationToken);
        progress?.Report(1);
#endif
    }

    public void DeleteModel(WindowsOcrLanguageSelection language)
    {
        ArgumentNullException.ThrowIfNull(language);
#if !BUNDLED_OCR_MODELS
        ApplyModelDirectory();
        if (_engines.TryRemove(language.Model, out var lazyEngine) && lazyEngine.IsValueCreated)
            lazyEngine.Value.Dispose();

        var model = GetModel(language.Model);
        foreach (var root in GetModelRoots(model))
        {
            if (!Directory.Exists(root) || IsModelRootShared(root, language.Model))
                continue;

            Directory.Delete(root, recursive: true);
        }
#endif
    }

    public IReadOnlyList<WindowsOcrBackendRegion> Recognize(
        Mat image,
        WindowsOcrLanguageSelection language,
        bool enableRotation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(language);
        cancellationToken.ThrowIfCancellationRequested();

        var handle = GetOrCreateEngine(language);
        PaddleOcrResult result;
        lock (handle.Gate)
        {
            var oldRotate = handle.Engine.AllowRotateDetection;
            if (enableRotation)
                handle.Engine.AllowRotateDetection = true;

            try
            {
                result = handle.Engine.Run(image);
            }
            finally
            {
                if (enableRotation)
                    handle.Engine.AllowRotateDetection = oldRotate;
            }
        }

        return result.Regions
            .Select(region => new WindowsOcrBackendRegion(
                region.Text,
                region.Rect.Points()
                    .Select(point => new WindowsOcrPoint(point.X, point.Y))
                    .ToArray(),
                region.Rect.Angle))
            .ToArray();
    }

    public void Dispose()
    {
        _applicationData.LocationChanged -= OnApplicationDataLocationChanged;
        DisposeEngines();
        _logger?.LogDebug("Windows Paddle OCR backend disposed.");
    }

    private void DisposeEngines()
    {
        foreach (var lazyEngine in _engines.Values)
        {
            if (lazyEngine.IsValueCreated)
                lazyEngine.Value.Dispose();
        }

        _engines.Clear();
    }

    private void OnApplicationDataLocationChanged(
        object? sender,
        ApplicationDataLocationChangedEventArgs args)
    {
        DisposeEngines();
#if !BUNDLED_OCR_MODELS
        ApplyModelDirectory();
#endif
    }

#if !BUNDLED_OCR_MODELS
    private void ApplyModelDirectory() =>
        Settings.GlobalModelDirectory = _applicationData.OcrModelsDirectory;
#endif

    private PaddleEngineHandle GetOrCreateEngine(WindowsOcrLanguageSelection language)
    {
        var lazyEngine = _engines.GetOrAdd(
            language.Model,
            model => new Lazy<PaddleEngineHandle>(
                () => CreateEngine(model, language),
                LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            return lazyEngine.Value;
        }
        catch
        {
            _engines.TryRemove(language.Model, out _);
            throw;
        }
    }

#if !BUNDLED_OCR_MODELS
    private bool IsModelRootShared(string root, WindowsOcrModel excludedModel)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        foreach (var language in OcrLanguages.Supported)
        {
            var model = WindowsOcrLanguageCatalog.Resolve(language).Model;
            if (model == excludedModel)
                continue;

            if (GetModelRoots(GetModel(model)).Any(otherRoot =>
                    string.Equals(
                        normalizedRoot,
                        Path.GetFullPath(otherRoot).TrimEnd(Path.DirectorySeparatorChar),
                        StringComparison.OrdinalIgnoreCase)))
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
#endif

    private PaddleEngineHandle CreateEngine(
        WindowsOcrModel modelId,
        WindowsOcrLanguageSelection language)
    {
        _logger?.LogInformation(
            "Initializing Windows Paddle OCR engine for {Language}...",
            language.Language.DisplayName);

#if BUNDLED_OCR_MODELS
        var model = GetModel(modelId);
#else
        if (!IsModelAvailable(language))
            throw new OcrModelNotDownloadedException(language.Language);

        var model = GetModel(modelId).DownloadAsync().GetAwaiter().GetResult();
#endif

        var engine = new PaddleOcrAll(model, PaddleDevice.Onnx())
        {
            AllowRotateDetection = true,
            Enable180Classification = true
        };
        return new PaddleEngineHandle(engine);
    }

#if BUNDLED_OCR_MODELS
    private static FullOcrModel GetModel(WindowsOcrModel model) =>
        ModelFactories.TryGetValue(model, out var factory)
            ? factory()
            : LocalFullModels.ChineseV4;
#else
    private static OnlineFullModels GetModel(WindowsOcrModel model) =>
        ModelFactories.TryGetValue(model, out var factory)
            ? factory()
            : OnlineFullModels.ChineseV4;
#endif

    private sealed class PaddleEngineHandle(PaddleOcrAll engine) : IDisposable
    {
        internal object Gate { get; } = new();
        internal PaddleOcrAll Engine { get; } = engine;
        public void Dispose() => Engine.Dispose();
    }
}
