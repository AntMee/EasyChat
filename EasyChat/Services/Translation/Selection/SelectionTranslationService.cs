using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Threading;
using EasyChat.Common;
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
    private readonly ILogger<SelectionTranslationService> _logger;
    private readonly ISelectedTextCaptureService _selectedTextCaptureService;

    private (int x, int y)? _downPoint;
    
    // Thresholds
    private const int DragThreshold = 5; // pixels
    
    private SelectionIconWindowView? _iconWindow;
    private TranslationDictionaryWindowView? _currentTranslateWindow;
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
        ISelectedTextCaptureService selectedTextCaptureService)
    {
        _mouseHookService = mouseHookService;
        _configurationService = configurationService;
        _platformService = platformService;
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
        return _iconWindow;
    }

    private void OnMouseDown(object? sender, SimpleMouseEventArgs e)
    {
        if (_configurationService.SelectionTranslation?.Enabled != true)
        {
            return;
        }

        // The global hook sees the mouse-down before the icon window does. Keep
        // this guard before the trigger-mode branches so clicking the icon is
        // delivered to its own handler instead of hiding it first.
        if (_iconWindow?.IsVisible == true)
        {
            var iconPos = _iconWindow.Position;
            var iconBounds = new Rect(iconPos.X, iconPos.Y, 40, 40);
            if (iconBounds.Contains(new Point(e.X, e.Y)))
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
        if (_currentTranslateWindow?.IsVisible == true)
        {
            _downPoint = null;
            _logger.LogDebug("Ignoring MouseUp while translation window is open");
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
            
            // Capture current generation
            var gen = System.Threading.Interlocked.Read(ref _interactionGeneration);

            // Do not show an icon until a non-empty text selection is confirmed.
            Task.Run(async () =>
            {
                try
                {
                    // Wait for potential selection finalization
                    await Task.Delay(50);
                    
                    // Check if canceled
                    if (gen != System.Threading.Interlocked.Read(ref _interactionGeneration)) return;

                    // The translation window may have opened while this capture was
                    // waiting; do not continue after the user interacted with it.
                    if (await Dispatcher.UIThread.InvokeAsync(() => _currentTranslateWindow?.IsVisible == true))
                        return;
                    
                    // Avalonia's Win32 clipboard implementation is UI-thread
                    // affine. The snapshot itself must therefore be created on
                    // the dispatcher.
                    // Let the pending icon render and input events run before
                    // enumerating the clipboard formats on the UI thread.
                    var backup = await Dispatcher.UIThread.InvokeAsync(
                        () => ClipboardHelper.BackupClipboardAsync(_logger),
                        DispatcherPriority.Background);
                    
                    var text = await _platformService.GetSelectedTextAsync(x2, y2);
                    _lastSelectedText = text;

                    var selectionClipboardSequence = ClipboardHelper.GetClipboardSequenceNumber();
                    
                    // Check if canceled again before showing
                    if (gen != System.Threading.Interlocked.Read(ref _interactionGeneration))
                    {
                        await Dispatcher.UIThread.InvokeAsync(() => ClipboardHelper.RestoreClipboardIfUnchangedAsync(backup, selectionClipboardSequence, _logger), DispatcherPriority.Background);
                        return;
                    }

                    if (await Dispatcher.UIThread.InvokeAsync(() => _currentTranslateWindow?.IsVisible == true))
                    {
                        await Dispatcher.UIThread.InvokeAsync(() => ClipboardHelper.RestoreClipboardIfUnchangedAsync(backup, selectionClipboardSequence, _logger), DispatcherPriority.Background);
                        return;
                    }

                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        _logger.LogInformation(
                            "Selected text captured using {Method}: {Length} chars",
                            _platformService.LastSelectedTextCaptureMethod ?? "Unknown",
                            text.Length);
                        // Show icon only if text is found
                        await Dispatcher.UIThread.InvokeAsync(() => 
                        {
                            if (gen == System.Threading.Interlocked.Read(ref _interactionGeneration))
                            {
                                ShowIcon(x2, y2);
                                _iconWindow?.HideLoading();
                            }
                        });

                        // Restoring all formats can block in the OLE clipboard
                        // implementation. Do it after the icon's first frame.
                        await Dispatcher.UIThread.InvokeAsync(() => ClipboardHelper.RestoreClipboardIfUnchangedAsync(backup, selectionClipboardSequence, _logger), DispatcherPriority.Background);
                    }
                    else
                    {
                        await Dispatcher.UIThread.InvokeAsync(HideIcon, DispatcherPriority.Input);
                        await Dispatcher.UIThread.InvokeAsync(() => ClipboardHelper.RestoreClipboardAsync(backup, _logger), DispatcherPriority.Background);
                        _logger.LogDebug("No text selected (or extraction failed)");
                    }
                }
                catch (Exception ex)
                {
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
        
        _logger.LogInformation("Double Click detected at {X}, {Y}", e.X, e.Y);
        
        _lastDoubleClickTime = DateTime.Now;
        _lastIconX = e.X;
        _lastIconY = e.Y;
        _lastSelectedText = null;
            
        var gen = System.Threading.Interlocked.Read(ref _interactionGeneration);

        // Do not show an icon until a non-empty text selection is confirmed.
        Task.Run(async () =>
        {
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
                    
                var backup = await Dispatcher.UIThread.InvokeAsync(
                    () => ClipboardHelper.BackupClipboardAsync(_logger),
                    DispatcherPriority.Background);
                    
                var text = await _platformService.GetSelectedTextAsync(e.X, e.Y);
                _lastSelectedText = text;

                var selectionClipboardSequence = ClipboardHelper.GetClipboardSequenceNumber();
                    
                if (gen != System.Threading.Interlocked.Read(ref _interactionGeneration))
                {
                    await Dispatcher.UIThread.InvokeAsync(() => ClipboardHelper.RestoreClipboardIfUnchangedAsync(backup, selectionClipboardSequence, _logger), DispatcherPriority.Background);
                    return;
                }

                if (!string.IsNullOrWhiteSpace(text))
                {
                    _logger.LogInformation(
                        "Selected text captured using {Method}: {Length} chars",
                        _platformService.LastSelectedTextCaptureMethod ?? "Unknown",
                        text.Length);
                    // Show icon only if text is found
                    await Dispatcher.UIThread.InvokeAsync(() => 
                    {
                        if (gen == System.Threading.Interlocked.Read(ref _interactionGeneration))
                        {
                            ShowIcon(e.X, e.Y);
                            _iconWindow?.HideLoading();
                        }
                    });

                    await Dispatcher.UIThread.InvokeAsync(() => ClipboardHelper.RestoreClipboardIfUnchangedAsync(backup, selectionClipboardSequence, _logger), DispatcherPriority.Background);
                }
                else
                {
                    await Dispatcher.UIThread.InvokeAsync(HideIcon, DispatcherPriority.Input);
                    await Dispatcher.UIThread.InvokeAsync(() => ClipboardHelper.RestoreClipboardAsync(backup, _logger), DispatcherPriority.Background);
                    _logger.LogDebug("No text selected (Double Click) - Text was empty");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting selected text (Double Click)");
                Dispatcher.UIThread.Post(() =>
                {
                    if (gen == System.Threading.Interlocked.Read(ref _interactionGeneration))
                        HideIcon();
                }, DispatcherPriority.Input);
            }
        });
    }

    private void ShowIcon(int x, int y)
    {
        _logger.LogDebug("Showing icon at {X}, {Y}", x, y);

        var iconWindow = EnsureIconWindow();
        
        // Ensure window is usable (in case it was closed externally)
        try 
        {
            // Set position
            var pixelPoint = new PixelPoint(x + 10, y + 10);
            iconWindow.Position = pixelPoint;
            iconWindow.Show();
            iconWindow.Topmost = true;
            // DO NOT Activate() to avoid stealing focus
        }
        catch
        {
            // Recreate if failed (e.g. invalid handle)
            _iconWindow = new SelectionIconWindowView();
            _iconWindow.TranslateClicked += OnTranslateClicked;
            _iconWindow.Position = new PixelPoint(x + 10, y + 10);
            _iconWindow.Show();
            _iconWindow.Topmost = true;
        }
        
        _logger.LogDebug("Icon window shown");
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
        try
        {
            var snapshot = await _selectedTextCaptureService.CaptureAsync();
            if (snapshot == null) return;
            var text = snapshot.Text;
            var x = snapshot.X;
            var y = snapshot.Y;

            _logger.LogInformation(
                "Selected text captured using {Method}: {Length} chars",
                _platformService.LastSelectedTextCaptureMethod ?? "Unknown",
                text.Length);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
             // Close existing (Singleton)
            try { _currentTranslateWindow?.Close(); } catch { /* Ignore */ }

            var dialog = new TranslationDictionaryWindowView();
            // Enable close button for shortcut-triggered window
            if (dialog.DataContext is TranslationDictionaryWindowViewModel vm)
            {
                vm.ShowCloseButton = true;
            }
            _currentTranslateWindow = dialog;
            
            dialog.Closed += (_, _) => 
            {
                if (_currentTranslateWindow == dialog) _currentTranslateWindow = null;
            };

            // Start initialization (loading state)
            // We just fire off the task, the VM handles the async translation and updates UI
            _ = dialog.InitializeAsync(text);
            
            ShowDialogAtPosition(dialog, x, y);
            
            _logger.LogInformation("Opened translation window via shortcut");
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to translate current selection from shortcut");
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
