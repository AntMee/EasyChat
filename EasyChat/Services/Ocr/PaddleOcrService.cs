using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
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

    public string ServiceName => "PaddleOCR";

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
        IWebProxy? previousProxy = HttpClient.DefaultProxy;
        // The downloader uses HttpClient.DefaultProxy internally. Use an empty
        // WebProxy when the setting is disabled so the download is direct,
        // rather than silently falling back to the machine's system proxy.
        HttpClient.DefaultProxy = useProxy && !string.IsNullOrWhiteSpace(proxyUrl)
            ? new WebProxy(proxyUrl)
            : new WebProxy();

        try
        {
            foreach (var language in SupportedLanguages)
            {
                _logger.LogInformation("Downloading OCR model for {Language}...", language.DisplayName);
                await GetModel(language).DownloadAsync(cancellationToken);
            }
        }
        finally
        {
            HttpClient.DefaultProxy = previousProxy;
        }
    }

    public bool IsModelDownloaded(OcrLanguage language)
    {
        var model = GetModel(language);
        return File.Exists(Path.Combine(model.DetModel.RootDirectory, "inference.pdiparams"))
            && File.Exists(Path.Combine(model.RecModel.RootDirectory, "inference.pdiparams"));
    }
#else
    public Task DownloadModelsAsync(string? proxyUrl, bool useProxy, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
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
        var lazyEngine = _engines.GetOrAdd(language, lang => new Lazy<PaddleOcrAll>(() =>
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
            _engines.TryRemove(language, out _);
            throw;
        }
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
