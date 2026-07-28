using System;
using System.Threading.Tasks;
using System.Threading;
using AutoMapper;
using Avalonia.Threading;
using System.Collections.Generic;
using EasyChat.Models.Configuration;
using EasyChat.Services.Abstractions;
using EasyChat.Services.Languages;
using EasyChat.Services.Ocr;
using EasyChat.Services.Translation;
using EasyChat.Views.Result;
using Microsoft.Extensions.Logging;
using EasyChat.Services.Speech.Tts;
using SukiUI.Toasts;
using EasyChat.Models;
using EasyChat.Models.Translation;
using EasyChat.Lang;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using OpenCvSharp;
using Sdcb.PaddleOCR;
using System.Linq;
using Avalonia.Input.Platform;
using Microsoft.Extensions.DependencyInjection;
using EasyChat.ViewModels.Windows;
using EasyChat.Views.Windows;
using EasyChat.Models.Ocr;
using EasyChat.Services.ImageTranslation;
using Avalonia.Controls;

namespace EasyChat.Services.Shortcuts.Handlers;

/// <summary>
/// Handler for the Screenshot shortcut action.
/// Captures screen, performs OCR, translates text, and shows result.
/// </summary>
public class ScreenshotTranslateHandler : IShortcutActionHandler
{
    private readonly IScreenCaptureService _screenCaptureService;
    private readonly IOcrService _ocrService;
    private readonly ITranslationServiceFactory _translationServiceFactory;
    private readonly IConfigurationService _configurationService;
    private readonly ISukiToastManager _toastManager;
    private readonly IMapper _mapper;
    private readonly ILogger<ScreenshotTranslateHandler> _logger;
    private readonly ITtsService _ttsService;
    private readonly IAudioPlayer _audioPlayer;
    private readonly IPlatformService _platformService;
    private readonly IImageTranslationService _imageTranslationService;
    private CancellationTokenSource? _imageTranslationCancellation;

    private readonly IServiceProvider _serviceProvider;
    
    private volatile bool _isExecuting;

    public string ActionType => "Screenshot";
    public bool PreventConcurrentExecution => true;
    public bool IsExecuting => _isExecuting;

    public ScreenshotTranslateHandler(
        IScreenCaptureService screenCaptureService,
        IOcrService ocrService,
        ITranslationServiceFactory translationServiceFactory,
        IConfigurationService configurationService,
        ISukiToastManager toastManager,
        IMapper mapper,
        ILogger<ScreenshotTranslateHandler> logger,
        ITtsService ttsService,
        IAudioPlayer audioPlayer,
        IPlatformService platformService,
        IServiceProvider serviceProvider,
        IImageTranslationService imageTranslationService)
    {
        _screenCaptureService = screenCaptureService;
        _ocrService = ocrService;
        _translationServiceFactory = translationServiceFactory;
        _configurationService = configurationService;
        _toastManager = toastManager;
        _mapper = mapper;
        _logger = logger;
        _ttsService = ttsService;
        _audioPlayer = audioPlayer;
        _platformService = platformService;
        _serviceProvider = serviceProvider;
        _imageTranslationService = imageTranslationService;
    }

    public void Execute(ShortcutParameter? parameter = null)
    {
        _isExecuting = true;
        _logger.LogInformation("Screenshot shortcut executed.");
        Dispatcher.UIThread.Post(StartScreenCapture);
    }

    private void StartScreenCapture()
    {
        var mode = _configurationService.Screenshot?.Mode ?? Constants.Constant.ScreenshotMode.Precise;
        var session = new ScreenCapture.ScreenSelectionSession(_screenCaptureService, OnScreenCaptured, OnScreenCaptureCancelled, mode);
        session.Start();
    }
    
    private void OnScreenCaptureCancelled()
    {
        _imageTranslationCancellation?.Cancel();
        _isExecuting = false;
        _logger.LogInformation("Screenshot capture cancelled.");
    }

    private void OnScreenCaptured(Bitmap bitmap, CaptureIntent intent)
    {
        // Screenshot captured successfully, allow new captures immediately
        _isExecuting = false;
        
        var sourceLang = _configurationService.General?.SourceLanguage;
        var ocrLanguage = _mapper.Map<OcrLanguage?>(sourceLang?.Id ?? LanguageKeys.ChineseSimplifiedId);
        
        // Log OCR start
        _logger.LogInformation("Starting OCR with language: {Language}", ocrLanguage);

        // Run OCR in background to avoid UI blocking
        Task.Run(() =>
        {
            try
            {
                if (intent == CaptureIntent.CopyImageTranslated)
                {
                     _imageTranslationCancellation?.Cancel();
                     var cancellation = new CancellationTokenSource();
                     _imageTranslationCancellation = cancellation;
                     var result = _ocrService.RecognizeDetailed(bitmap, ocrLanguage, enableRotation: true);
                     Dispatcher.UIThread.Post(() => ProcessImageTranslation(bitmap, result, cancellation));
                }
                else
                {
                    var ocrResult = _ocrService.RecognizeText(bitmap, ocrLanguage);
                    _logger.LogDebug("OCR Result Length: {Length}", ocrResult.Length);
                    Dispatcher.UIThread.Post(() => ProcessOcrResult(ocrResult, intent));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OCR processing failed.");
                if (ex is OcrModelNotDownloadedException)
                {
                    Dispatcher.UIThread.Post(() => ShowError(
                        Resources.OcrModelRequiredTitle,
                        Resources.OcrModelRequiredMessage));
                }
                else
                {
                    Dispatcher.UIThread.Post(() => ShowError("OCR Error", ex.Message));
                }
            }
        });
    }
    
    private void ProcessOcrResult(string ocrResult, CaptureIntent intent)
    {
        if (string.IsNullOrWhiteSpace(ocrResult))
        {
            ShowError("OCR Warning", "No text detected.");
            return;
        }

        if (intent == CaptureIntent.CopyOriginal)
        {
            CopyToClipboard(ocrResult);
            // Continue to show ResultView as requested
        }

        try
        {
            var resultMode = _configurationService.Result?.ScreenshotResultMode ?? ResultWindowMode.Classic;

            if (resultMode == ResultWindowMode.Dictionary)
            {
                 Dispatcher.UIThread.Post(async void () =>
                 {
                     try 
                     {
                         var viewModel = _serviceProvider.GetRequiredService<TranslationDictionaryWindowViewModel>();
                         viewModel.IsScreenshotMode = true;
                         viewModel.ShowCloseButton = true; 
                         
                         var view = new TranslationDictionaryWindowView 
                         { 
                             DataContext = viewModel
                         };
                         
                        view.WindowStartupLocation = WindowStartupLocation.Manual;
                        
                        try 
                        {
                            var pos = _platformService.GetCursorPosition();
                            // Offset slightly so it doesn't cover the text immediately
                            view.Position = new PixelPoint(pos.X + 15, pos.Y + 15);
                        }
                        catch
                        {
                             view.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                        }
                         
                         view.Show();
                         view.Activate();
                         
                         // Initialize with OCR result
                         await viewModel.InitializeAsync(ocrResult);
                     }
                     catch (Exception ex)
                     {
                         _logger.LogError(ex, "Failed to open Dictionary Window");
                         ShowError("Error", ex.Message);
                     }
                 });
                 return;
            }

            // Classic Mode Logic
            var translator = _translationServiceFactory.CreateCurrentService();
            
            // Always show ResultView
            var resultWindow = new ResultView();
            var fontSize = _configurationService.Result?.FontSize;
            resultWindow.SetFontSize(fontSize ?? 14);
            var isWindowClosed = false;
            
            resultWindow.ShowLoading(); // Show loading initially
            resultWindow.Closed += (_, _) => isWindowClosed = true;
            resultWindow.Show();

            Task.Run(async () => await TranslateAndDisplayAsync(
                ocrResult, translator, resultWindow, () => isWindowClosed, intent));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize translation service or window.");
            ShowError("Service Error", ex.Message);
        }
    }

    private async Task TranslateAndDisplayAsync(
        string text,
        ITranslation translator,
        ResultView resultWindow,
        Func<bool> isClosedCheck,
        CaptureIntent intent)
    {
        try
        {
            var sourceLang = _configurationService.General?.SourceLanguage;
            var targetLang = _configurationService.General?.TargetLanguage;

            _logger.LogInformation("Starting translation: {Source} -> {Target}", sourceLang?.Id, targetLang?.Id);

            string? finalTranslation;

            finalTranslation = await TranslateStreamingAsync(translator, text, sourceLang, targetLang, resultWindow, isClosedCheck, intent);

            // Read Aloud Logic
            var readMode = _configurationService.Result?.ReadAloudMode ?? ResultReadAloudMode.None;
            var isClosed = isClosedCheck();
            var hasTranslation = !string.IsNullOrEmpty(finalTranslation);

            _logger.LogInformation("Read Aloud Check Code Reached. Valid: {Valid}, Closed: {Closed}, HasTranslation: {HasTranslation} (Len: {Len}), Mode: {Mode}", 
                (!isClosed && hasTranslation), isClosed, hasTranslation, finalTranslation?.Length ?? 0, readMode);

            if (!isClosed && hasTranslation && readMode != ResultReadAloudMode.None)
            {
                 _ = Task.Run(async () => 
                 {
                     try 
                     {
                         var currentProvider = _configurationService.Tts?.Provider;
                         _logger.LogInformation("Read Aloud Task Started. Provider: {Provider}", currentProvider);
                         
                         if (string.IsNullOrEmpty(currentProvider)) return;

                         // Helper to play audio
                         async Task PlayAudio(string? content, string? languageId)
                         {
                             _logger.LogInformation("PlayAudio called for Lang: {Lang}, ContentLen: {Len}", languageId, content?.Length ?? 0);
                             
                             if (string.IsNullOrEmpty(content) || languageId == null) return;
                             
                             var voiceId = TtsHelper.GetPreferredVoiceId(_ttsService, _configurationService, languageId);
                             _logger.LogInformation("Resolved Voice ID: {VoiceId}", voiceId);

                             if (!string.IsNullOrEmpty(voiceId))
                             {
                                 try 
                                 {
                                     var stream = await _ttsService.StreamAsync(content, voiceId);
                                     if (stream != null)
                                     {
                                         _logger.LogInformation("Stream received, enqueuing.");
                                         _audioPlayer.Enqueue(stream);
                                     }
                                     else
                                     {
                                         _logger.LogWarning("Stream was null.");
                                     }
                                 }
                                 catch (Exception argEx)
                                 {
                                     _logger.LogWarning(argEx, "Error during TTS StreamAsync.");
                                 }
                             }
                             else
                             {
                                 _logger.LogWarning("No voice ID found.");
                             }
                         }

                         if (readMode == ResultReadAloudMode.Source || readMode == ResultReadAloudMode.Both)
                         {
                             await PlayAudio(text, sourceLang?.Id);
                         }
                         
                         if (readMode == ResultReadAloudMode.Target || readMode == ResultReadAloudMode.Both)
                         {
                             await PlayAudio(finalTranslation!, targetLang?.Id);
                         }
                     }
                     catch (Exception ex)
                     {
                         _logger.LogError(ex, "Read Aloud failed.");
                     }
                 });
            }

            // Auto-close after configured delay
            if (!isClosedCheck())
            {
                var delay = _configurationService.Result?.AutoCloseDelay;
                if (_configurationService.Result is { EnableAutoReadDelay: true })
                {
                    var length = finalTranslation?.Length ?? text.Length;
                    var msPerChar = _configurationService.Result.MsPerChar;
                    delay = Math.Max(2000, length * msPerChar); // Minimum 2 seconds
                }
                
                resultWindow.CloseAfterDelay(delay ?? 5000);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Translation process failed.");
            
            if (!isClosedCheck())
            {
                // Unbox generic tool types like TypeInitializationException to reveal the real cause
                var errorMsg = ex.InnerException != null 
                    ? $"{ex.Message} -> {ex.InnerException.Message}" 
                    : ex.Message;

                Dispatcher.UIThread.Post(() =>
                {
                    if (!isClosedCheck())
                    {
                        ShowError("Translation Error", errorMsg);
                        resultWindow.Close();
                    }
                });
            }
        }
    }

    private async Task<string?> TranslateStreamingAsync(
        ITranslation translator,
        string text,
        LanguageDefinition? sourceLang,
        LanguageDefinition? targetLang,
        ResultView resultWindow,
        Func<bool> isClosedCheck,
        CaptureIntent intent = CaptureIntent.Translation)
    {
        var isFirstChunk = true;
        var fullTranslation = new System.Text.StringBuilder();
        
        await foreach (var item in translator.StreamTranslateEventsAsync(text, sourceLang, targetLang))
        {
            if (item is not TranslationDeltaEvent delta || string.IsNullOrEmpty(delta.Text))
                continue;
            var chunk = delta.Text;
            if (isClosedCheck()) break;

            if (isFirstChunk && !string.IsNullOrEmpty(chunk))
            {
                isFirstChunk = false;
                Dispatcher.UIThread.Post(() =>
                {
                    if (!isClosedCheck() && !resultWindow.IsVisible)
                    {
                        resultWindow.ShowResult(); // Switch to result view on first chunk
                        resultWindow.IsVisible = true;
                    }
                });
            }

            if (isClosedCheck()) break;

            Dispatcher.UIThread.Post(() =>
            {
                if (!isClosedCheck())
                {
                    resultWindow.AppendText(chunk);
                }
            });
            
            fullTranslation.Append(chunk);
        }
        
        if (intent != CaptureIntent.Translation && !isClosedCheck())
        {
            var translation = fullTranslation.ToString();
            var finalText = intent == CaptureIntent.CopyBilingual 
                 ? $"{text}\n\n{translation}" 
                 : translation;
            
            // Only copy if not CopyOriginal (handled early), or if intent is explicitly CopyTranslated/Bilingual
            if (intent == CaptureIntent.CopyTranslated || intent == CaptureIntent.CopyBilingual)
            {
                 Dispatcher.UIThread.Post(() => CopyToClipboard(finalText));
            }
        }

        return fullTranslation.ToString();
    }

    private void CopyToClipboard(string text)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } window })
        {
            var clipboard = window.Clipboard;
            if (clipboard != null)
            {
                clipboard.SetTextAsync(text);
                _toastManager.CreateSimpleInfoToast()
                    .WithTitle("Copied")
                    .WithContent("Text copied to clipboard.")
                    .Queue();
            }
        }
    }

    private void ShowError(string title, string message)
    {
        _toastManager.CreateSimpleInfoToast()
            .WithTitle(title)
            .WithContent(message)
            .Queue();
    }
    private async void ProcessImageTranslation(
        Bitmap bitmap,
        OcrRecognitionResult recognition,
        CancellationTokenSource cancellation)
    {
        try
        {
            if (recognition.Regions.Count == 0)
            {
                ShowError("OCR Warning", "No text detected.");
                return;
            }

            var sourceLang = _configurationService.General?.SourceLanguage;
            var targetLang = _configurationService.General?.TargetLanguage;
            var result = await Task.Run(() => _imageTranslationService.TranslateAsync(
                bitmap, recognition, sourceLang, targetLang, cancellation.Token), cancellation.Token);

            if (result.TranslatedBlockCount == 0)
            {
                ShowError("Image Translation", result.Warnings.Count > 0
                    ? result.Warnings[0]
                    : "No text could be translated.");
                return;
            }

            var view = new ImageTranslationResultWindow(result.Bitmap, result.Warnings.ToArray());
            view.Show();
        }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException)
                return;
            _logger.LogError(ex, "Image Translation failed");
            ShowError("Image Translate Error", ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_imageTranslationCancellation, cancellation))
            {
                _imageTranslationCancellation = null;
                cancellation.Dispose();
            }
        }
    }
}
