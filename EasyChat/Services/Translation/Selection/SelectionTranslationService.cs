using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using EasyChat.Models;
using EasyChat.Models.Configuration;
using EasyChat.Services.Abstractions;
using EasyChat.Views.Windows;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using EasyChat.ViewModels.Windows;
using EasyChat.Services.TextAssist;

namespace EasyChat.Services.Translation.Selection;

public class SelectionTranslationService : IDisposable
{
    private readonly IMouseHookService _mouseHookService;
    private readonly IConfigurationService _configurationService;
    private readonly IPlatformService _platformService;
    private readonly IClipboardSnapshotService _clipboardSnapshotService;
    private readonly ILogger<SelectionTranslationService> _logger;
    private readonly ISelectedTextCaptureService _selectedTextCaptureService;

    private (int x, int y)? _downPoint;
    private IntPtr _foregroundWindowAtMouseDown;
    private IntPtr _focusedWindowAtMouseDown;
    
    // Thresholds
    private const int DragThreshold = 5; // pixels
    
    private SelectionIconWindowView? _iconWindow;
    private TranslationDictionaryWindowView? _currentTranslateWindow;
    private TextAssistResultWindowView? _currentResultWindow;
    private TranslationDictionaryWindowView? _prewarmedTranslateWindow;
    private int _lastIconX;
    private int _lastIconY;
    private string? _lastSelectedText;
    private bool _disposed;

    public SelectionTranslationService(
        IMouseHookService mouseHookService,
        IConfigurationService configurationService,
        IPlatformService platformService,
        ILogger<SelectionTranslationService> logger,
        ISelectedTextCaptureService selectedTextCaptureService,
        IClipboardSnapshotService clipboardSnapshotService)
    {
        _mouseHookService = mouseHookService;
        _configurationService = configurationService;
        _platformService = platformService;
        _clipboardSnapshotService = clipboardSnapshotService;
        _logger = logger;
        _selectedTextCaptureService = selectedTextCaptureService;

        _mouseHookService.MouseDown += OnMouseDown;
        _mouseHookService.MouseUp += OnMouseUp;
        _mouseHookService.MouseDoubleClick += OnMouseDoubleClick;
        
        // Reactive config monitoring
        if (_configurationService.SelectionTranslation != null)
        {
            // Flag to track if this is the initial subscription callback (app startup)
            bool isStartup = true;

            _configurationService.SelectionTranslation.WhenAnyValue(x => x.Enabled)
                .Subscribe(enabled =>
                {
                    if (enabled)
                    {
                        if (isStartup)
                        {
                            // Delay start strictly for startup to prevent lag
                            Task.Delay(3000).ContinueWith(_ =>
                            {
                                Dispatcher.UIThread.InvokeAsync(() =>
                                {
                                    // Re-check enabled state after delay in case it changed
                                    if (_configurationService.SelectionTranslation.Enabled)
                                    {
                                        StartHook();
                                    }
                                });
                            });
                        }
                        else
                        {
                            // Immediate start for runtime toggle
                            Dispatcher.UIThread.InvokeAsync(StartHook);
                        }
                    }
                    else
                    {
                        Dispatcher.UIThread.InvokeAsync(() =>
                        {
                             _mouseHookService.Stop();
                        });
                    }
                    
                    // After the first callback (immediate initial value), subsequent ones are runtime changes
                    isStartup = false;
                });
        }
        
        _logger.LogInformation("SelectionTranslationService initialized");
    }

    private void StartHook()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _mouseHookService.Start();

            // Construct the first-use windows while the app is idle. Creating an
            // Avalonia window loads XAML and creates a native handle; doing that
            // from the first mouse interaction causes a visible input hitch.
            Dispatcher.UIThread.Post(PrewarmSelectionWindows, DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start mouse hook service");
        }
    }

    private void PrewarmSelectionWindows()
    {
        if (_disposed)
        {
            return;
        }

        var iconWindow = EnsureIconWindow();

        // Create the native window and renderer during idle time so first use
        // does not need to load XAML or allocate the platform window.
        try
        {
            if (!iconWindow.IsVisible)
            {
                var opacity = iconWindow.Opacity;
                iconWindow.Opacity = 0;
                iconWindow.Position = new PixelPoint(-10000, -10000);
                iconWindow.Show();
                iconWindow.Topmost = true;
                iconWindow.Hide();
                iconWindow.Opacity = opacity;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to prewarm selection icon window");
        }

        if (_prewarmedTranslateWindow != null)
        {
            return;
        }

        try
        {
            var window = new TranslationDictionaryWindowView();
            var opacity = window.Opacity;
            window.Opacity = 0;
            window.Position = new PixelPoint(-10000, -10000);
            window.Show();
            window.Hide();
            window.Opacity = opacity;
            _prewarmedTranslateWindow = window;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to prewarm translation window");
        }
    }

    private bool HandlesDragSelection()
    {
        var mode = _configurationService.SelectionTranslation?.TriggerMode ?? SelectionTriggerMode.All;
        return mode is SelectionTriggerMode.DragSelection or SelectionTriggerMode.All;
    }

    private bool HandlesDoubleClick()
    {
        var mode = _configurationService.SelectionTranslation?.TriggerMode ?? SelectionTriggerMode.All;
        return mode is SelectionTriggerMode.DoubleClick or SelectionTriggerMode.All;
    }

    private SelectionIconWindowView EnsureIconWindow()
    {
        if (_iconWindow != null)
        {
            return _iconWindow;
        }

        _iconWindow = new SelectionIconWindowView();
        _iconWindow.TranslateClicked += OnTranslateClicked;
        _iconWindow.CorrectionClicked += (_, _) => OnTextAssistClicked(TextAssistOperation.Correction);
        _iconWindow.PolishClicked += (_, _) => OnTextAssistClicked(TextAssistOperation.Polish);
        _iconWindow.SummaryClicked += (_, _) => OnTextAssistClicked(TextAssistOperation.Summary);
        return _iconWindow;
    }

    private void OnMouseDown(object? sender, SimpleMouseEventArgs e)
    {
        if (_configurationService.SelectionTranslation?.Enabled != true)
        {
            return;
        }

        // MouseDoubleClick is raised by the low-level hook before the second
        // MouseDown. Keep the snapshot at the start of MouseDown so a double
        // click can compare against the first click's source window.
        _foregroundWindowAtMouseDown = _platformService.GetForegroundWindowHandle();
        _focusedWindowAtMouseDown = _platformService.GetFocusedWindowHandle();

        // The global hook sees the mouse-down before the icon window does. Keep
        // this guard before the trigger-mode branches so clicking the icon is
        // delivered to its own handler instead of hiding it first.
        if (_iconWindow?.IsVisible == true)
        {
            if (IsPointInsideWindow(_iconWindow, e.X, e.Y))
            {
                _logger.LogDebug("Click is on icon window, not hiding");
                return;
            }
        }

        // Double-click mode does not use drag selection, but it still needs a
        // mouse-down handler so a later single click can dismiss the icon.
        if (!HandlesDragSelection())
        {
            if ((DateTime.Now - _lastDoubleClickTime).TotalMilliseconds < 500)
            {
                var distance = Math.Sqrt(Math.Pow(e.X - _lastIconX, 2) + Math.Pow(e.Y - _lastIconY, 2));
                if (distance < 40)
                {
                    return;
                }
            }

            if (_iconWindow?.IsVisible == true)
            {
                UpdateGeneration();
                HideIcon();
            }

            return;
        }
        
        _logger.LogDebug("Mouse Down at {X}, {Y}", e.X, e.Y);
        
        _downPoint = (e.X, e.Y);
        
        // Check if click is inside the Translation Window (if open)
        if (_currentTranslateWindow != null && _currentTranslateWindow.IsVisible)
        {
            var screenPoint = new PixelPoint(e.X, e.Y);
            var clientPoint = _currentTranslateWindow.PointToClient(screenPoint);
            var bounds = new Rect(0, 0, _currentTranslateWindow.Bounds.Width, _currentTranslateWindow.Bounds.Height);
            
            if (bounds.Contains(clientPoint))
            {
                 _logger.LogDebug("Click is inside Translation Window, ignoring.");
                 return;
            }
            
            // Click is outside -> Close Window
            Dispatcher.UIThread.Post(() => 
            {
                try { _currentTranslateWindow?.Close(); }
                catch
                {
                    // ignored
                }
            });
        }

        if (_currentResultWindow?.IsVisible == true)
        {
            var screenPoint = new PixelPoint(e.X, e.Y);
            var clientPoint = _currentResultWindow.PointToClient(screenPoint);
            if (new Rect(0, 0, _currentResultWindow.Bounds.Width, _currentResultWindow.Bounds.Height).Contains(clientPoint))
            {
                _downPoint = null;
                _logger.LogDebug("Click is inside text assist result window, ignoring selection hook");
                return;
            }
            else
                Dispatcher.UIThread.Post(() => _currentResultWindow?.Close());
        }
        
        // Hide icon on any click elsewhere (start of new interaction)
        
        // Check if this MouseDown is part of a recent Double Click sequence.
        // If so, we should IGNORE it to avoid cancelling the Double Click operation.
        if ((DateTime.Now - _lastDoubleClickTime).TotalMilliseconds < 500)
        {
             // Check distance - if close, it's likely the 2nd click of the double click or a ghost click
             var dist = Math.Sqrt(Math.Pow(e.X - _lastIconX, 2) + Math.Pow(e.Y - _lastIconY, 2));
             if (dist < 40) // generous tolerance for double click movement
             {
                  _logger.LogDebug("Ignoring MouseDown near DoubleClick (Time: {Time}ms, Dist: {Dist})", 
                      (DateTime.Now - _lastDoubleClickTime).TotalMilliseconds, dist);
                  return; 
             }
        }

        UpdateGeneration();
        Dispatcher.UIThread.Post(() => HideIcon());
    }

    private long _interactionGeneration;
    private DateTime _lastDoubleClickTime;

    private void UpdateGeneration()
    {
        System.Threading.Interlocked.Increment(ref _interactionGeneration);
    }

    private void HideIcon()
    {
        try
        {
            _iconWindow?.Hide();
        }
        catch { /* Ignore */ }
    }

    private void OnMouseUp(object? sender, SimpleMouseEventArgs e)
    {
        if (_configurationService.SelectionTranslation?.Enabled != true || !HandlesDragSelection()) return;

        // Releasing the mouse after dragging the translation window must not start
        // another selection capture or show the selection icon.
        if (_currentTranslateWindow?.IsVisible == true || _currentResultWindow?.IsVisible == true)
        {
            _downPoint = null;
            _logger.LogDebug("Ignoring MouseUp while an application result window is open");
            return;
        }

        if (_downPoint == null) return;

        var (x1, y1) = _downPoint.Value;
        var x2 = e.X;
        var y2 = e.Y;

        var distance = Math.Sqrt(Math.Pow(x2 - x1, 2) + Math.Pow(y2 - y1, 2));
        
        _logger.LogDebug("Mouse Up at {X}, {Y}. Distance: {Distance}", x2, y2, distance);

        _downPoint = null;

        if (distance > DragThreshold)
        {
            _logger.LogInformation("Drag detected, getting selected text...");
            _lastIconX = x2;
            _lastIconY = y2;
            _lastSelectedText = null;

            // A screenshot overlay also looks like a drag selection to the global
            // mouse hook. Snapshot the external state before the overlay handles
            // mouse-up so we can avoid racing its clipboard/focus cleanup below.
            var foregroundWindowAtMouseDown = _foregroundWindowAtMouseDown;
            var focusedWindowAtMouseDown = _focusedWindowAtMouseDown;
            var foregroundWindowAtMouseUp = _platformService.GetForegroundWindowHandle();
            var clipboardSequenceAtMouseUp = _clipboardSnapshotService.GetChangeToken();
            
            // Capture current generation
            var gen = System.Threading.Interlocked.Read(ref _interactionGeneration);

            Task.Run(async () =>
            {
                IClipboardSnapshot? backup = null;
                uint? clipboardSequenceAfterCopy = null;
                try
                {
                    // Wait for potential selection finalization
                    await Task.Delay(50);
                    
                    // Check if canceled
                    if (gen != System.Threading.Interlocked.Read(ref _interactionGeneration)) return;

                    if (HasSelectionContextChanged(foregroundWindowAtMouseUp, clipboardSequenceAtMouseUp))
                    {
                        _logger.LogDebug("Skipping drag selection capture because another application changed focus or clipboard state");
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (gen == System.Threading.Interlocked.Read(ref _interactionGeneration)) HideIcon();
                        }, DispatcherPriority.Input);
                        return;
                    }

                    // The translation window may have opened while this capture was
                    // waiting; do not continue after the user interacted with it.
                    if (await Dispatcher.UIThread.InvokeAsync(() => _currentTranslateWindow?.IsVisible == true))
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (gen == System.Threading.Interlocked.Read(ref _interactionGeneration)) HideIcon();
                        }, DispatcherPriority.Input);
                        return;
                    }
                    
                    // Prefer the clipboard-free native edit-control path. Some
                    // applications expose no native selection text, so use the
                    // complete OLE snapshot around the copy fallback.
                    var text = await _platformService.GetSelectedTextDirectAsync(
                        expectedForegroundWindow: foregroundWindowAtMouseDown,
                        expectedFocusedWindow: focusedWindowAtMouseDown);
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        backup = _clipboardSnapshotService.Backup(_logger);
                        if (backup == null)
                        {
                            _logger.LogWarning("Skipping selection capture because a complete clipboard snapshot was unavailable.");
                            return;
                        }

                        text = await _platformService.GetSelectedTextAsync(
                            x2,
                            y2,
                            copyOnly: true,
                            expectedForegroundWindow: foregroundWindowAtMouseDown,
                            expectedFocusedWindow: focusedWindowAtMouseDown);
                        clipboardSequenceAfterCopy = _clipboardSnapshotService.GetChangeToken();
                    }
                    _lastSelectedText = text;

                    // Check if canceled again before showing
                    if (gen != System.Threading.Interlocked.Read(ref _interactionGeneration))
                    {
                        _clipboardSnapshotService.RestoreIfUnchanged(
                            backup,
                            clipboardSequenceAfterCopy ?? _clipboardSnapshotService.GetChangeToken(),
                            _logger);
                        backup = null;
                        return;
                    }

                    if (await Dispatcher.UIThread.InvokeAsync(() => _currentTranslateWindow?.IsVisible == true))
                    {
                        _clipboardSnapshotService.RestoreIfUnchanged(
                            backup,
                            clipboardSequenceAfterCopy ?? _clipboardSnapshotService.GetChangeToken(),
                            _logger);
                        backup = null;
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (gen == System.Threading.Interlocked.Read(ref _interactionGeneration)) HideIcon();
                        }, DispatcherPriority.Input);
                        return;
                    }

                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        _logger.LogInformation(
                            "Selected text captured using {Method}: {Length} chars",
                            _platformService.LastSelectedTextCaptureMethod ?? "Unknown",
                            text.Length);
                        await Dispatcher.UIThread.InvokeAsync(() => 
                        {
                            if (gen == System.Threading.Interlocked.Read(ref _interactionGeneration))
                            {
                                ShowIcon(x2, y2);
                                _iconWindow?.HideLoading();
                            }
                        });
                        _clipboardSnapshotService.RestoreIfUnchanged(
                            backup,
                            clipboardSequenceAfterCopy ?? _clipboardSnapshotService.GetChangeToken(),
                            _logger);
                        backup = null;
                    }
                    else
                    {
                        await Dispatcher.UIThread.InvokeAsync(HideIcon, DispatcherPriority.Input);
                        _clipboardSnapshotService.RestoreIfUnchanged(
                            backup,
                            clipboardSequenceAfterCopy ?? _clipboardSnapshotService.GetChangeToken(),
                            _logger);
                        backup = null;
                        _logger.LogDebug("No text selected (or extraction failed)");
                    }
                }
                catch (Exception ex)
                {
                    _clipboardSnapshotService.RestoreIfUnchanged(
                        backup,
                        clipboardSequenceAfterCopy ?? _clipboardSnapshotService.GetChangeToken(),
                        _logger);
                    _logger.LogError(ex, "Error getting selected text");
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (gen == System.Threading.Interlocked.Read(ref _interactionGeneration))
                            HideIcon();
                    }, DispatcherPriority.Input);
                }
            });
        }
    }

    private void OnMouseDoubleClick(object? sender, SimpleMouseEventArgs e)
    {
        if (_configurationService.SelectionTranslation?.Enabled != true || !HandlesDoubleClick()) return;
        if (IsPointInsideWindow(_currentTranslateWindow, e.X, e.Y) ||
            IsPointInsideWindow(_currentResultWindow, e.X, e.Y)) return;
        
        _logger.LogInformation("Double Click detected at {X}, {Y}", e.X, e.Y);
        
        _lastDoubleClickTime = DateTime.Now;
        _lastIconX = e.X;
        _lastIconY = e.Y;
        _lastSelectedText = null;

        // Screenshot tools may use a double-click to accept a window/region and
        // then update the clipboard or close their overlay asynchronously.
        // The hook raises MouseDoubleClick before the second MouseDown, so the
        // stored values still represent the first click's source window.
        var foregroundWindowAtDoubleClick = _foregroundWindowAtMouseDown;
        var focusedWindowAtDoubleClick = _focusedWindowAtMouseDown;
        var clipboardSequenceAtDoubleClick = _clipboardSnapshotService.GetChangeToken();
            
        var gen = System.Threading.Interlocked.Read(ref _interactionGeneration);

        Task.Run(async () =>
        {
            IClipboardSnapshot? backup = null;
            uint? clipboardSequenceAfterCopy = null;
            try
            {
                // Wait for potential selection finalization (double click selects word)
                // Increased delay to ensure OS highlights text
                await Task.Delay(150);
                
                var currentGen = System.Threading.Interlocked.Read(ref _interactionGeneration);
                if (gen != currentGen) 
                {
                    _logger.LogDebug("Double Click cancelled. Gen mismatch: {Captured} != {Current}", gen, currentGen);
                    return;
                }

                if (HasSelectionContextChanged(foregroundWindowAtDoubleClick, clipboardSequenceAtDoubleClick))
                {
                    _logger.LogDebug("Skipping double-click selection capture because another application changed focus or clipboard state");
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (gen == System.Threading.Interlocked.Read(ref _interactionGeneration)) HideIcon();
                    }, DispatcherPriority.Input);
                    return;
                }
                    
                var text = await _platformService.GetSelectedTextDirectAsync(
                    expectedForegroundWindow: foregroundWindowAtDoubleClick,
                    expectedFocusedWindow: focusedWindowAtDoubleClick);
                if (string.IsNullOrWhiteSpace(text))
                {
                    backup = _clipboardSnapshotService.Backup(_logger);
                    if (backup == null)
                    {
                        _logger.LogWarning("Skipping double-click capture because a complete clipboard snapshot was unavailable.");
                        return;
                    }

                    text = await _platformService.GetSelectedTextAsync(
                        e.X,
                        e.Y,
                        copyOnly: true,
                        expectedForegroundWindow: foregroundWindowAtDoubleClick,
                        expectedFocusedWindow: focusedWindowAtDoubleClick);
                    clipboardSequenceAfterCopy = _clipboardSnapshotService.GetChangeToken();
                }
                _lastSelectedText = text;

                if (gen != System.Threading.Interlocked.Read(ref _interactionGeneration))
                {
                    _clipboardSnapshotService.RestoreIfUnchanged(
                        backup,
                        clipboardSequenceAfterCopy ?? _clipboardSnapshotService.GetChangeToken(),
                        _logger);
                    backup = null;
                    return;
                }

                if (!string.IsNullOrWhiteSpace(text))
                {
                    _logger.LogInformation(
                        "Selected text captured using {Method}: {Length} chars",
                        _platformService.LastSelectedTextCaptureMethod ?? "Unknown",
                        text.Length);
                    await Dispatcher.UIThread.InvokeAsync(() => 
                    {
                        if (gen == System.Threading.Interlocked.Read(ref _interactionGeneration))
                        {
                            ShowIcon(e.X, e.Y);
                            _iconWindow?.HideLoading();
                        }
                    });
                    _clipboardSnapshotService.RestoreIfUnchanged(
                        backup,
                        clipboardSequenceAfterCopy ?? _clipboardSnapshotService.GetChangeToken(),
                        _logger);
                    backup = null;
                }
                else
                {
                    await Dispatcher.UIThread.InvokeAsync(HideIcon, DispatcherPriority.Input);
                    _clipboardSnapshotService.RestoreIfUnchanged(
                        backup,
                        clipboardSequenceAfterCopy ?? _clipboardSnapshotService.GetChangeToken(),
                        _logger);
                    backup = null;
                    _logger.LogDebug("No text selected (Double Click) - Text was empty");
                }
            }
            catch (Exception ex)
            {
                _clipboardSnapshotService.RestoreIfUnchanged(
                    backup,
                    clipboardSequenceAfterCopy ?? _clipboardSnapshotService.GetChangeToken(),
                    _logger);
                _logger.LogError(ex, "Error getting selected text (Double Click)");
                Dispatcher.UIThread.Post(() =>
                {
                    if (gen == System.Threading.Interlocked.Read(ref _interactionGeneration))
                        HideIcon();
                }, DispatcherPriority.Input);
            }
        });
    }

    private bool HasSelectionContextChanged(IntPtr foregroundWindowAtTrigger, uint clipboardSequenceAtTrigger)
    {
        var currentForegroundWindow = _platformService.GetForegroundWindowHandle();
        var foregroundWindowChanged = foregroundWindowAtTrigger != IntPtr.Zero &&
                                      currentForegroundWindow != IntPtr.Zero &&
                                      foregroundWindowAtTrigger != currentForegroundWindow;

        return foregroundWindowChanged ||
               clipboardSequenceAtTrigger != _clipboardSnapshotService.GetChangeToken();
    }

    private void ShowIcon(int x, int y)
    {
        _logger.LogDebug("Showing icon at {X}, {Y}", x, y);

        var iconWindow = EnsureIconWindow();
        var config = _configurationService.SelectionTranslation;
        if (config == null || (!config.TranslationEnabled && !config.CorrectionEnabled && !config.PolishEnabled && !config.SummaryEnabled))
        {
            HideIcon();
            return;
        }
        iconWindow.ApplyConfiguration(config);
        
        // Ensure window is usable (in case it was closed externally)
        try 
        {
            PositionToolbarWindow(iconWindow, x, y);
            iconWindow.Show();
            iconWindow.Topmost = true;
            // DO NOT Activate() to avoid stealing focus
        }
        catch
        {
            // Recreate if failed (e.g. invalid handle)
            _iconWindow = new SelectionIconWindowView();
            _iconWindow.TranslateClicked += OnTranslateClicked;
            _iconWindow.CorrectionClicked += (_, _) => OnTextAssistClicked(TextAssistOperation.Correction);
            _iconWindow.PolishClicked += (_, _) => OnTextAssistClicked(TextAssistOperation.Polish);
            _iconWindow.SummaryClicked += (_, _) => OnTextAssistClicked(TextAssistOperation.Summary);
            _iconWindow.ApplyConfiguration(config);
            PositionToolbarWindow(_iconWindow, x, y);
            _iconWindow.Show();
            _iconWindow.Topmost = true;
        }
        
        _logger.LogDebug("Icon window shown");
    }

    private static void PositionToolbarWindow(Window window, int x, int y)
    {
        var screen = window.Screens.ScreenFromPoint(new PixelPoint(x, y)) ?? window.Screens.Primary;
        if (screen == null)
        {
            window.Position = new PixelPoint(x + 6, y + 6);
            return;
        }

        var area = screen.WorkingArea;
        var scale = screen.Scaling;
        var offset = Math.Max(4, (int)Math.Ceiling(6 * scale));
        var width = Math.Max(1, (int)Math.Ceiling(window.Width * scale));
        var height = Math.Max(1, (int)Math.Ceiling(window.Height * scale));
        var left = x + offset;
        var top = y + offset;

        if (left + width > area.Right) left = x - width - offset;
        if (top + height > area.Bottom) top = y - height - offset;

        left = Math.Clamp(left, area.X, Math.Max(area.X, area.Right - width));
        top = Math.Clamp(top, area.Y, Math.Max(area.Y, area.Bottom - height));
        window.Position = new PixelPoint(left, top);
    }

    private void OnTranslateClicked(object? sender, EventArgs e)
    {
        _logger.LogInformation("Translate icon clicked! Opening dialog...");
        
        // Get position and text before any async operation
        var x = _lastIconX;
        var y = _lastIconY;
        var text = _lastSelectedText;
        
        var gen = System.Threading.Interlocked.Read(ref _interactionGeneration);

        // Immediately show loading spinner on UI thread
        Dispatcher.UIThread.Post(() => _iconWindow?.ShowLoading());
        
        // Run the preparation asynchronously to avoid blocking
        Task.Run(async () =>
        {
            try
            {
                // Check cancellation
                if (gen != System.Threading.Interlocked.Read(ref _interactionGeneration)) return;

                // Create the dialog on the UI thread. The first instance is
                // normally prewarmed during idle time.
                TranslationDictionaryWindowView? dialog = null;
                
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (gen != System.Threading.Interlocked.Read(ref _interactionGeneration)) return;

                    _logger.LogInformation("Attempting to open translate dialog at {X}, {Y}", x, y);
                    
                    // Close existing window if any (Singleton behavior)
                    try { _currentTranslateWindow?.Close(); } catch { /* Ignore if already closing */ }

                     dialog = _prewarmedTranslateWindow ?? new TranslationDictionaryWindowView();
                    _prewarmedTranslateWindow = null;
                    _currentTranslateWindow = dialog;

                    if (dialog.DataContext is TranslationDictionaryWindowViewModel viewModel)
                    {
                        viewModel.ShowCloseButton = true;
                    }
                    
                    // Handle cleanup when closed manually
                    dialog.Closed += (_, _) => 
                    {
                        if (_currentTranslateWindow == dialog)
                        {
                            _currentTranslateWindow = null;
                        }

                        // Keep the next dialog ready after the current native
                        // window is released, so later selections avoid a first
                        // use XAML/window-creation pause as well.
                        Dispatcher.UIThread.Post(PrewarmSelectionWindows, DispatcherPriority.Background);
                    };
                });
                
                if (gen != System.Threading.Interlocked.Read(ref _interactionGeneration)) return;

                if (dialog == null || string.IsNullOrEmpty(text))
                {
                    // Fallback: show dialog without async init
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (gen != System.Threading.Interlocked.Read(ref _interactionGeneration)) return;

                        if (dialog != null && !string.IsNullOrEmpty(text))
                        {
                            dialog.SetSourceText(text);
                        }
                        ShowDialogAtPosition(dialog, x, y);
                        HideIconAndLoading();
                    });
                    return;
                }
                
                // Show the shell before starting translation. Waiting for the
                // provider here made the first fully-populated layout arrive as
                // one large UI update and briefly block mouse input.
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (gen != System.Threading.Interlocked.Read(ref _interactionGeneration)) return;

                    dialog.SetSourceText(text);
                    ShowDialogAtPosition(dialog, x, y);
                    HideIconAndLoading();
                });

                // Translation and result binding happen asynchronously while the
                // already-visible window displays its loading/empty state.
                await dialog.InitializeAsync(text);

                if (gen != System.Threading.Interlocked.Read(ref _interactionGeneration)) return;

                _logger.LogInformation("SelectionTranslateDialog opened successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to open translate dialog. StackTrace: {StackTrace}", ex.StackTrace);
                await Dispatcher.UIThread.InvokeAsync(HideIconAndLoading);
            }
        });
    }

    private static bool IsPointInsideWindow(Window? window, int x, int y)
    {
        if (window?.IsVisible != true) return false;
        var clientPoint = window.PointToClient(new PixelPoint(x, y));
        return new Rect(0, 0, window.Bounds.Width, window.Bounds.Height).Contains(clientPoint);
    }

    private void OnTextAssistClicked(TextAssistOperation operation)
    {
        var text = _lastSelectedText;
        var x = _lastIconX;
        var y = _lastIconY;
        var generation = System.Threading.Interlocked.Read(ref _interactionGeneration);
        if (string.IsNullOrWhiteSpace(text)) return;
        Dispatcher.UIThread.Post(() => _iconWindow?.ShowLoading());
        Task.Run(async () =>
        {
            try
            {
                if (generation != System.Threading.Interlocked.Read(ref _interactionGeneration)) return;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    try { _currentResultWindow?.Close(); } catch { }
                    var window = new TextAssistResultWindowView();
                    _currentResultWindow = window;
                    window.Closed += (_, _) =>
                    {
                        if (_currentResultWindow == window) _currentResultWindow = null;
                    };
                    PositionResultWindow(window, x, y);
                    window.Show();
                    HideIconAndLoading();
                    _ = window.InitializeAsync(text, operation);
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to open selection text assist window");
                await Dispatcher.UIThread.InvokeAsync(HideIconAndLoading);
            }
        });
    }

    private static void PositionResultWindow(Window window, int x, int y)
    {
        var screen = window.Screens.ScreenFromPoint(new PixelPoint(x, y)) ?? window.Screens.Primary;
        if (screen == null) { window.Position = new PixelPoint(x + 16, y + 16); return; }
        var area = screen.WorkingArea;
        var scale = screen.Scaling;
        var width = Math.Max(1, (int)Math.Ceiling(window.Width * scale));
        var height = Math.Max(1, (int)Math.Ceiling(window.Height * scale));
        var offset = Math.Max(8, (int)Math.Ceiling(16 * scale));
        var left = x + offset;
        var top = y + offset;
        if (left + width > area.Right) left = x - width - offset;
        if (top + height > area.Bottom) top = y - height - offset;
        left = Math.Clamp(left, area.X, Math.Max(area.X, area.Right - width));
        top = Math.Clamp(top, area.Y, Math.Max(area.Y, area.Bottom - height));
        window.Position = new PixelPoint(left, top);
    }
    
    private void ShowDialogAtPosition(TranslationDictionaryWindowView? dialog, int x, int y)
    {
        if (dialog == null) return;
        
        // Window dimensions (approximate or max)
        const int windowWidth = 450;
        const int estimatedHeight = 350; // Use a reasonable estimate or the max height
        
        // Offset from cursor
        const int offset = 20;

        // Default Position: Bottom-Right
        var finalX = x + offset;
        var finalY = y + offset;
        
        var screen = dialog.Screens.ScreenFromPoint(new PixelPoint(x, y)) ?? dialog.Screens.Primary;
        if (screen != null)
        {
            var screenRect = screen.WorkingArea;
            
            // --- Horizontal Logic ---
            // Check if Right overflow
            if (finalX + windowWidth > screenRect.Right)
            {
                // Try Left: Cursor X - Width - Offset
                var leftX = x - windowWidth - offset;
                
                // If Left fits, use it
                if (leftX >= screenRect.X)
                {
                    finalX = leftX;
                }
                else
                {
                    // Neither fits perfectly. Choose the side with MORE space? 
                    // Or just clamp the Right version. 
                    // Let's stick to Clamping the Right version for now, as it's safer.
                    finalX = screenRect.Right - windowWidth - 10; // 10px padding from edge
                }
            }

            // --- Vertical Logic ---
            // Check if Bottom overflow
            if (finalY + estimatedHeight > screenRect.Bottom)
            {
                // Try Top: Cursor Y - Height - Offset
                var topY = y - estimatedHeight - offset;
                
                // If Top fits, use it
                if (topY >= screenRect.Y)
                {
                    finalY = topY;
                }
                else
                {
                    // Vertical Clamp
                    finalY = screenRect.Bottom - estimatedHeight - 10;
                }
            }
            
            // Final Safety Clamp (Absolute Bounds)
            if (finalX < screenRect.X) finalX = screenRect.X;
            if (finalY < screenRect.Y) finalY = screenRect.Y;
        }

        dialog.Position = new PixelPoint(finalX, finalY);
        dialog.Show();
        // Don't activate to prevent focus theft
        // dialog.Activate();
    }
    
    private void HideIconAndLoading()
    {
        _iconWindow?.HideLoading();
        HideIcon();
    }

    public async Task TranslateCurrentSelectionAsync()
    {
        TranslationDictionaryWindowView? dialog = null;
        try
        {
            var (x, y) = _platformService.GetCursorPosition();

            // The dictionary window is no-activate, so it can render a loading
            // shell now without redirecting the synthetic Ctrl+C from the app
            // that owns the selection.
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                try { _currentTranslateWindow?.Close(); } catch { /* Ignore */ }

                dialog = _prewarmedTranslateWindow ?? new TranslationDictionaryWindowView();
                _prewarmedTranslateWindow = null;
                if (dialog.DataContext is TranslationDictionaryWindowViewModel vm)
                {
                    vm.ShowCloseButton = true;
                }

                _currentTranslateWindow = dialog;
                dialog.Closed += (_, _) =>
                {
                    if (_currentTranslateWindow == dialog) _currentTranslateWindow = null;
                    Dispatcher.UIThread.Post(PrewarmSelectionWindows, DispatcherPriority.Background);
                };
                dialog.ShowInputCaptureLoading();
                ShowDialogAtPosition(dialog, x, y);
            });

            var openedDialog = dialog
                               ?? throw new InvalidOperationException("Translation window was not created.");

            var snapshot = await _selectedTextCaptureService.CaptureAsync();
            if (snapshot == null)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (_currentTranslateWindow != openedDialog) return;
                    openedDialog.HideInputCaptureLoading();
                    openedDialog.Close();
                });
                return;
            }
            var text = snapshot.Text;

            _logger.LogInformation(
                "Selected text captured using {Method}: {Length} chars",
                _platformService.LastSelectedTextCaptureMethod ?? "Unknown",
                text.Length);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_currentTranslateWindow != openedDialog || !openedDialog.IsVisible) return;
                openedDialog.SetSourceText(text);
                _ = openedDialog.InitializeAsync(text);
                _logger.LogInformation("Opened translation window via shortcut");
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to translate current selection from shortcut");
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (dialog == null || _currentTranslateWindow != dialog) return;
                dialog.HideInputCaptureLoading();
            });
        }
    }

    public async Task ShowToolbarForCurrentSelectionAsync()
    {
        try
        {
            var (x, y) = _platformService.GetCursorPosition();
            UpdateGeneration();
            var generation = System.Threading.Interlocked.Read(ref _interactionGeneration);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                try { _currentTranslateWindow?.Close(); } catch { /* Ignore */ }
                try { _currentResultWindow?.Close(); } catch { /* Ignore */ }
                HideIcon();
            });

            // Prefer direct extraction, then use the capture service's complete
            // OLE snapshot around the copy fallback for apps without native text.
            var text = await _platformService.GetSelectedTextDirectAsync();
            if (string.IsNullOrWhiteSpace(text))
            {
                var snapshot = await _selectedTextCaptureService.CaptureViaCopyAsync();
                text = snapshot?.Text;
            }

            if (string.IsNullOrWhiteSpace(text) ||
                generation != System.Threading.Interlocked.Read(ref _interactionGeneration))
                return;

            _lastSelectedText = text;
            _lastIconX = x;
            _lastIconY = y;
            _logger.LogInformation(
                "Selected text captured for shortcut toolbar using {Method}: {Length} chars",
                _platformService.LastSelectedTextCaptureMethod ?? "Unknown",
                text.Length);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != System.Threading.Interlocked.Read(ref _interactionGeneration)) return;
                ShowIcon(x, y);
                _iconWindow?.HideLoading();
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to show selection toolbar from shortcut");
            await Dispatcher.UIThread.InvokeAsync(HideIconAndLoading);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _mouseHookService.MouseDown -= OnMouseDown;
        _mouseHookService.MouseUp -= OnMouseUp;
        _mouseHookService.MouseDoubleClick -= OnMouseDoubleClick;
        _mouseHookService.Stop();
        if (_iconWindow != null)
        {
            _iconWindow.TranslateClicked -= OnTranslateClicked;
            _iconWindow.Close();
            _iconWindow = null;
        }

        _prewarmedTranslateWindow?.Close();
    }
}
