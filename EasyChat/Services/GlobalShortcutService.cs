using System;
using System.Collections.Generic;
using System.Linq;
using System.ComponentModel;
using EasyChat.Models.Configuration;
using EasyChat.Services.Abstractions;
using EasyChat.Services.Shortcuts;
using Microsoft.Extensions.Logging;

namespace EasyChat.Services;

/// <summary>
/// Service for managing global keyboard shortcuts.
/// Registers/unregisters hotkeys based on configuration and dispatches actions to handlers.
/// </summary>
public class GlobalShortcutService : IDisposable
{
    private readonly IHotKeyManager _hotKeyManager;
    private readonly IConfigurationService _configurationService;
    private readonly Dictionary<string, IShortcutActionHandler> _handlers;
    private readonly List<IDisposable> _activeHotKeys = new();
    private readonly ILogger<GlobalShortcutService> _logger;

    public GlobalShortcutService(
        IHotKeyManager hotKeyManager,
        IConfigurationService configurationService,
        IEnumerable<IShortcutActionHandler> handlers,
        ILogger<GlobalShortcutService> logger)
    {
        _hotKeyManager = hotKeyManager;
        _configurationService = configurationService;
        _handlers = handlers.ToDictionary(h => h.ActionType, h => h, StringComparer.OrdinalIgnoreCase);
        _logger = logger;

        // Subscribe to configuration changes
        if (_configurationService.Shortcut?.Entries != null)
        {
            _configurationService.Shortcut.Entries.CollectionChanged += OnShortcutEntriesChanged;
            foreach (var entry in _configurationService.Shortcut.Entries)
                entry.PropertyChanged += OnShortcutEntryChanged;
        }

        // Initial Registration
        RegisterHotKeys();
    }

    private void OnShortcutEntriesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
            foreach (ShortcutEntry entry in e.OldItems)
                entry.PropertyChanged -= OnShortcutEntryChanged;
        if (e.NewItems != null)
            foreach (ShortcutEntry entry in e.NewItems)
                entry.PropertyChanged += OnShortcutEntryChanged;
        RegisterHotKeys();
    }

    private void OnShortcutEntryChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Dialogs normally replace entries, but this also covers direct edits
        // to an existing shortcut and keeps registration in sync immediately.
        RegisterHotKeys();
    }

    private void RegisterHotKeys()
    {
        // Dispose existing hotkeys
        foreach (var hotKey in _activeHotKeys)
        {
            hotKey.Dispose();
        }
        _activeHotKeys.Clear();

        if (_configurationService.Shortcut?.Entries == null)
        {
            _logger.LogWarning("No shortcut configuration entries were loaded.");
            return;
        }

        _logger.LogInformation("Registering {Count} configured global shortcuts.",
            _configurationService.Shortcut.Entries.Count);

        foreach (var entry in _configurationService.Shortcut.Entries)
        {
            if (!entry.IsEnabled || string.IsNullOrWhiteSpace(entry.KeyCombination))
            {
                _logger.LogDebug("Skipping disabled/empty shortcut entry {ActionType}.", entry.ActionType);
                continue;
            }

            var parsed = KeyCombinationParser.Parse(entry.KeyCombination);
            if (!parsed.HasValue)
            {
                _logger.LogWarning("Could not parse shortcut {Combination} for {ActionType}.",
                    entry.KeyCombination, entry.ActionType);
                continue;
            }

            // Find handler for this action type
            if (!_handlers.TryGetValue(entry.ActionType, out var handler))
            {
                _logger.LogWarning("No shortcut handler is registered for {ActionType}.", entry.ActionType);
                continue;
            }

            // Capture parameter for closure
            var parameter = entry.Parameter;

            var hotKey = _hotKeyManager.Register(
                parsed.Value.modifiers,
                parsed.Value.key,
                () =>
                {
                    // Debounce: skip if handler is already executing
                    if (handler.PreventConcurrentExecution && handler.IsExecuting)
                        return;
                    handler.Execute(parameter);
                });

            if (hotKey != null)
            {
                _activeHotKeys.Add(hotKey);
            }
            else
            {
                _logger.LogWarning("Hotkey manager rejected {Combination} for {ActionType}.",
                    entry.KeyCombination, entry.ActionType);
            }
        }
    }

    public void Dispose()
    {
        foreach (var hotKey in _activeHotKeys)
        {
            hotKey.Dispose();
        }
        _activeHotKeys.Clear();

        if (_configurationService.Shortcut?.Entries != null)
        {
            _configurationService.Shortcut.Entries.CollectionChanged -= OnShortcutEntriesChanged;
            foreach (var entry in _configurationService.Shortcut.Entries)
                entry.PropertyChanged -= OnShortcutEntryChanged;
        }
    }
}
