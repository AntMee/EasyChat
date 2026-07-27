using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
#if !BUNDLED_OCR_MODELS
using System.Net;
using System.Net.Http;
#endif
using EasyChat.Services.Abstractions;
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
using Bitmap = Avalonia.Media.Imaging.Bitmap;

namespace EasyChat.Services.Ocr;

public class PaddleOcrService : IOcrService, IDisposable
{
    private readonly ILogger<PaddleOcrService> _logger;
    private readonly ConcurrentDictionary<OcrLanguage, Lazy<PaddleOcrAll>> _engines = new();
#if !BUNDLED_OCR_MODELS
    private static readonly object DownloadProxyLock = new();
    private static string? _configuredDownloadProxy;
#endif

    public string ServiceName => "PaddleOCR";

#if BUNDLED_OCR_MODELS
    public bool CanDeleteModels => false;
#else
    public bool CanDeleteModels => true;
#endif

    public IReadOnlyList<OcrLanguage> SupportedLanguages { get; } =
    [
        OcrLanguage.ChineseSimplified,
        OcrLanguage.ChineseTraditional,
        OcrLanguage.English,
        OcrLanguage.Japanese,
        OcrLanguage.Korean,
        OcrLanguage.Arabic,
        OcrLanguage.Devanagari,
        OcrLanguage.Tamil,
        OcrLanguage.Telugu,
        OcrLanguage.Kannada
    ];

#if BUNDLED_OCR_MODELS
    private static readonly Dictionary<OcrLanguage, Func<FullOcrModel>> ModelFactories = new()
    {
        [OcrLanguage.ChineseSimplified] = () => LocalFullModels.ChineseV5,
        [OcrLanguage.ChineseTraditional] = () => LocalFullModels.TraditionalChineseV3,
        [OcrLanguage.English] = () => LocalFullModels.EnglishV4,
        [OcrLanguage.Japanese] = () => LocalFullModels.JapanV4,
        [OcrLanguage.Korean] = () => LocalFullModels.KoreanV4,
        [OcrLanguage.Arabic] = () => LocalFullModels.ArabicV4,
        [OcrLanguage.Devanagari] = () => LocalFullModels.DevanagariV4,
        [OcrLanguage.Tamil] = () => LocalFullModels.TamilV4,
        [OcrLanguage.Telugu] = () => LocalFullModels.TeluguV4,
        [OcrLanguage.Kannada] = () => LocalFullModels.KannadaV4
    };
#else
    private static readonly Dictionary<OcrLanguage, Func<OnlineFullModels>> ModelFactories = new()
    {
        [OcrLanguage.ChineseSimplified] = () => OnlineFullModels.ChineseV5,
        [OcrLanguage.ChineseTraditional] = () => OnlineFullModels.TraditionalChineseV3,
        [OcrLanguage.English] = () => OnlineFullModels.EnglishV4,
        [OcrLanguage.Japanese] = () => OnlineFullModels.JapanV4,
        [OcrLanguage.Korean] = () => OnlineFullModels.KoreanV4,
        [OcrLanguage.Arabic] = () => OnlineFullModels.ArabicV4,
        [OcrLanguage.Devanagari] = () => OnlineFullModels.DevanagariV4,
        [OcrLanguage.Tamil] = () => OnlineFullModels.TamilV4,
        [OcrLanguage.Telugu] = () => OnlineFullModels.TeluguV4,
        [OcrLanguage.Kannada] = () => OnlineFullModels.KannadaV4
    };
#endif

    public PaddleOcrService(ILogger<PaddleOcrService> logger)
    {
        _logger = logger;
#if !BUNDLED_OCR_MODELS
        Settings.GlobalModelDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EasyChat",
            "PaddleOcrModels");
#endif
    }

#if !BUNDLED_OCR_MODELS
    public async Task DownloadModelsAsync(string? proxyUrl, bool useProxy, CancellationToken cancellationToken = default)
    {
        foreach (var language in SupportedLanguages)
        {
            await DownloadModelAsync(language, proxyUrl, useProxy, null, cancellationToken);
        }
    }

    public async Task DownloadModelAsync(
        OcrLanguage language,
        string? proxyUrl,
        bool useProxy,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ConfigureDownloadProxy(proxyUrl, useProxy);

        var model = GetModel(language);
        _logger.LogInformation("Downloading OCR model for {Language}...", language.DisplayName);

        progress?.Report(0);
        await model.DetModel.DownloadAsync(cancellationToken);
        progress?.Report(1d / 3d);
        if (model.ClsModel is { } clsModel)
            await clsModel.DownloadAsync(cancellationToken);
        progress?.Report(2d / 3d);
        await model.RecModel.DownloadAsync(cancellationToken);
        progress?.Report(1);
    }

    public bool IsModelDownloaded(OcrLanguage language)
    {
        var model = GetModel(language);
        return File.Exists(Path.Combine(model.DetModel.RootDirectory, "inference.pdiparams"))
            && (model.ClsModel is null
                || File.Exists(Path.Combine(model.ClsModel.RootDirectory, "inference.pdiparams")))
            && File.Exists(Path.Combine(model.RecModel.RootDirectory, "inference.pdiparams"));
    }

    public void DeleteModel(OcrLanguage language)
    {
        if (_engines.TryRemove(language, out var lazyEngine) && lazyEngine.IsValueCreated)
            lazyEngine.Value.Dispose();

        var model = GetModel(language);
        foreach (var root in GetModelRoots(model))
        {
            if (!Directory.Exists(root) || IsModelRootShared(root, language))
                continue;

            Directory.Delete(root, recursive: true);
        }
    }

    private bool IsModelRootShared(string root, OcrLanguage excludedLanguage)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        foreach (var language in SupportedLanguages)
        {
            if (language == excludedLanguage)
                continue;

            var otherModel = GetModel(language);
            if (GetModelRoots(otherModel).Any(otherRoot =>
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
#else
    public Task DownloadModelsAsync(string? proxyUrl, bool useProxy, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task DownloadModelAsync(
        OcrLanguage language,
        string? proxyUrl,
        bool useProxy,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(1);
        return Task.CompletedTask;
    }

    public bool IsModelDownloaded(OcrLanguage language) => true;

    public void DeleteModel(OcrLanguage language)
    {
    }
#endif

#if !BUNDLED_OCR_MODELS
    private static void ConfigureDownloadProxy(string? proxyUrl, bool useProxy)
    {
        var key = useProxy && !string.IsNullOrWhiteSpace(proxyUrl)
            ? $"proxy:{proxyUrl}"
            : "direct";

        lock (DownloadProxyLock)
        {
            if (_configuredDownloadProxy == key)
                return;

            // PaddleOCR's downloader reads HttpClient.DefaultProxy. Only guard
            // this short global configuration step so model downloads can run
            // concurrently after they have the same proxy configuration.
            HttpClient.DefaultProxy = key == "direct"
                ? new WebProxy()
                : new WebProxy(proxyUrl!);
            _configuredDownloadProxy = key;
        }
    }
#endif

    public void Dispose()
    {
        foreach (var kvp in _engines)
        {
            if (kvp.Value.IsValueCreated)
            {
                kvp.Value.Value.Dispose();
            }
        }
        _engines.Clear();
        _logger.LogDebug("PaddleOcrService disposed");
    }

    public string RecognizeText(Bitmap bitmap, OcrLanguage? language = null)
    {
        var lang = language ?? OcrLanguage.ChineseSimplified;
        _logger.LogDebug("Starting OCR recognition with language: {Language}", lang.DisplayName);
        
        var engine = GetOrCreateEngine(lang);
        using var src = BitmapToMat(bitmap);
        var result = engine.Run(src);
        
        _logger.LogInformation("OCR ({Language}): {CharCount} characters recognized", 
            lang.DisplayName, result.Text.Length);
        GC.Collect();
        return result.Text;
    }

    public (Bitmap AnnotatedImage, string Text) RecognizeTextWithAnnotation(Bitmap bitmap, OcrLanguage? language = null)
    {
        var lang = language ?? OcrLanguage.ChineseSimplified;
        _logger.LogDebug("Starting OCR recognition with annotation, language: {Language}", lang.DisplayName);
        
        var engine = GetOrCreateEngine(lang);
        using var src = BitmapToMat(bitmap);
        var result = engine.Run(src);

        // Annotate logic (drawing rectangles)
        foreach (var region in result.Regions)
            src.Rectangle(region.Rect.BoundingRect().TopLeft, region.Rect.BoundingRect().BottomRight, Scalar.Red, 2);

        var annotatedBitmap = MatToBitmap(src);
        _logger.LogInformation("OCR with annotation ({Language}): {CharCount} characters, {RegionCount} regions", 
            lang.DisplayName, result.Text.Length, result.Regions.Length);
        return (annotatedBitmap, result.Text);
    }

    public PaddleOcrResult RecognizeTextRaw(Bitmap bitmap, OcrLanguage? language = null, bool enableRotation = false)
    {
        var lang = language ?? OcrLanguage.ChineseSimplified;
        _logger.LogDebug("Starting raw OCR recognition with language: {Language}, Rotation: {Rotation}", lang.DisplayName, enableRotation);
        
        var engine = GetOrCreateEngine(lang);
        
        // Save previous state
        var oldRotate = engine.AllowRotateDetection;
        var old180 = engine.Enable180Classification;

        if (enableRotation)
        {
            engine.AllowRotateDetection = true;
            // engine.Enable180Classification = true; // Causing crash
        }

        try
        {
            using var src = BitmapToMat(bitmap);
            return engine.Run(src);
        }
        finally
        {
            // Restore state
            if (enableRotation)
            {
                engine.AllowRotateDetection = oldRotate;
                // engine.Enable180Classification = old180;
            }
        }
    }

    private PaddleOcrAll GetOrCreateEngine(OcrLanguage language)
    {
        var resolvedLanguage = ResolveLanguage(language);
        var lazyEngine = _engines.GetOrAdd(resolvedLanguage, lang => new Lazy<PaddleOcrAll>(() =>
        {
            _logger.LogInformation("Initializing PaddleOCR engine for {Language}...", lang.DisplayName);
#if BUNDLED_OCR_MODELS
            var model = GetModel(lang);
#else
            if (!IsModelDownloaded(lang))
                throw new OcrModelNotDownloadedException(lang);

            var model = GetModel(lang).DownloadAsync().GetAwaiter().GetResult();
#endif
            var engine = new PaddleOcrAll(model, PaddleDevice.Onnx())
            {
                AllowRotateDetection = false,
                Enable180Classification = false
            };
            _logger.LogInformation("PaddleOCR engine for {Language} initialized successfully", lang.DisplayName);
            return engine;
        }));

        try
        {
            return lazyEngine.Value;
        }
        catch
        {
            // A missing model can be downloaded from Settings later. Do not
            // keep a faulted Lazy instance that would make the error permanent.
            _engines.TryRemove(resolvedLanguage, out _);
            throw;
        }
    }

    private OcrLanguage ResolveLanguage(OcrLanguage language)
    {
        if (language != OcrLanguage.Auto)
            return language;

#if BUNDLED_OCR_MODELS
        return OcrLanguage.ChineseSimplified;
#else
        if (IsModelDownloaded(OcrLanguage.ChineseSimplified))
            return OcrLanguage.ChineseSimplified;
        if (IsModelDownloaded(OcrLanguage.English))
            return OcrLanguage.English;

        // Keep the exception meaningful while preserving Chinese as the
        // default once a model is downloaded later.
        return OcrLanguage.ChineseSimplified;
#endif
    }

#if BUNDLED_OCR_MODELS
    private static FullOcrModel GetModel(OcrLanguage language)
        => ModelFactories.TryGetValue(language, out var factory) ? factory() : LocalFullModels.ChineseV4;
#else
    private static OnlineFullModels GetModel(OcrLanguage language)
        => ModelFactories.TryGetValue(language, out var factory) ? factory() : OnlineFullModels.ChineseV4;
#endif

    private static Mat BitmapToMat(Bitmap bitmap)
    {
        using var memoryStream = new MemoryStream();
        bitmap.Save(memoryStream);
        memoryStream.Seek(0, SeekOrigin.Begin);
        return Mat.FromStream(memoryStream, ImreadModes.Color);
    }

    private static Bitmap MatToBitmap(Mat mat)
    {
        using var memoryStream = new MemoryStream();
        mat.WriteToStream(memoryStream, ".jpg");
        memoryStream.Seek(0, SeekOrigin.Begin);
        return new Bitmap(memoryStream);
    }
}
