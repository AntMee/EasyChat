using EasyChat.Models.Configuration;
using EasyChat.Services.Abstractions;
using EasyChat.Services.Translation.Selection;
using System.Threading;
using System.Threading.Tasks;

namespace EasyChat.Services.Shortcuts.Handlers;

/// <summary>
/// Handler for the SelectionTranslate shortcut action.
/// Performs translation on the currently selected text.
/// </summary>
public class SelectionTranslateHandler : IShortcutActionHandler
{
    private readonly SelectionTranslationService _selectionTranslationService;
    private int _isExecuting;

    public string ActionType => "SelectionTranslate";
    public bool PreventConcurrentExecution => true;
    public bool IsExecuting => Volatile.Read(ref _isExecuting) != 0;
    
    public SelectionTranslateHandler(SelectionTranslationService selectionTranslationService)
    {
        _selectionTranslationService = selectionTranslationService;
    }

    public void Execute(ShortcutParameter? parameter = null)
    {
        if (Interlocked.CompareExchange(ref _isExecuting, 1, 0) != 0)
        {
            return;
        }

        _ = ExecuteAsync(parameter);
    }

    private async Task ExecuteAsync(ShortcutParameter? parameter)
    {
        try
        {
            if (parameter?.ShowSelectionToolbar == true)
            {
                await _selectionTranslationService.ShowToolbarForCurrentSelectionAsync();
            }
            else
            {
                await _selectionTranslationService.TranslateCurrentSelectionAsync();
            }
        }
        finally
        {
            Volatile.Write(ref _isExecuting, 0);
        }
    }
}
