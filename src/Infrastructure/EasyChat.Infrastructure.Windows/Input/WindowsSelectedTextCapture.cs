using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using EasyChat.Contracts.Platform;
using EasyChat.Shared.Results;
using Microsoft.Extensions.Logging;

namespace EasyChat.Infrastructure.Windows.Input;

[SupportedOSPlatform("windows")]
public sealed class WindowsSelectedTextCapture : ISelectedTextCapture
{
    private const uint EditGetSelection = 0x00B0;
    private const uint WindowGetText = 0x000D;
    private const uint WindowGetTextLength = 0x000E;
    private const int MaxWindowTextLength = 1_000_000;

    private readonly IClipboardSnapshots _clipboardSnapshots;
    private readonly IClipboardText _clipboardText;
    private readonly IPointerPosition _pointerPosition;
    private readonly IKeyboardState _keyboardState;
    private readonly ITextSelection _textSelection;
    private readonly WindowsNativeInputBackend _native = new();
    private readonly ILogger<WindowsSelectedTextCapture> _logger;

    public WindowsSelectedTextCapture(
        IClipboardSnapshots clipboardSnapshots,
        IClipboardText clipboardText,
        IPointerPosition pointerPosition,
        IKeyboardState keyboardState,
        ITextSelection textSelection,
        ILogger<WindowsSelectedTextCapture> logger)
    {
        _clipboardSnapshots = clipboardSnapshots ?? throw new ArgumentNullException(nameof(clipboardSnapshots));
        _clipboardText = clipboardText ?? throw new ArgumentNullException(nameof(clipboardText));
        _pointerPosition = pointerPosition ?? throw new ArgumentNullException(nameof(pointerPosition));
        _keyboardState = keyboardState ?? throw new ArgumentNullException(nameof(keyboardState));
        _textSelection = textSelection ?? throw new ArgumentNullException(nameof(textSelection));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async ValueTask<Result<SelectedText>> CaptureAsync(
        SelectionCaptureRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await Task.Run(
            () => CaptureCoreAsync(request, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<Result<SelectedText>> CaptureCoreAsync(
        SelectionCaptureRequest request,
        CancellationToken cancellationToken)
    {
        IntPtr? expectedForeground;
        IntPtr? expectedFocused;
        try
        {
            expectedForeground = DecodeOptionalTarget(request.ExpectedForegroundTarget);
            expectedFocused = DecodeOptionalTarget(request.ExpectedFocusedTarget);
        }
        catch (ArgumentException exception)
        {
            return Result<SelectedText>.Failure(new Error("selection.target-invalid", exception.Message));
        }

        if (!HasExpectedWindowContext(expectedForeground, expectedFocused))
            return ContextChanged();

        if (request.CaptureAll)
        {
            var selected = await _textSelection.SelectAllAsync(cancellationToken).ConfigureAwait(false);
            if (selected.IsFailure || !IsCompleteSelection(selected.Value))
            {
                if (HasPressedCopyKey())
                    return Result<SelectedText>.Failure(new Error(
                        "selection.keyboard-busy",
                        "Selection capture cannot inject input while shortcut keys are pressed."));
                _native.SendInputs(WindowsKeyCombination.Parse("Ctrl + A"));
            }
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }

        var position = request.PointerPosition ?? _pointerPosition.GetCurrent();
        var source = WindowsTargetTokens.FromHandle(expectedForeground ?? _native.GetForegroundWindow());
        if (!request.CopyOnly)
        {
            var direct = TryGetSelectedTextFromFocusedControl();
            if (!string.IsNullOrWhiteSpace(direct))
            {
                return Result<SelectedText>.Success(new SelectedText(
                    direct,
                    source,
                    "EM_GETSEL/WM_GETTEXT",
                    position));
            }
        }

        if (request.DirectOnly)
            return EmptySelection();

        IClipboardSnapshot? snapshot = null;
        IClipboardChangeToken? expectedRestoreToken = null;
        try
        {
            if (request.PreserveClipboard)
            {
                var captured = await _clipboardSnapshots.CaptureAsync(cancellationToken).ConfigureAwait(false);
                if (captured.IsFailure)
                    return Result<SelectedText>.Failure(captured.Error);
                snapshot = captured.Value;
            }

            if (HasPressedCopyKey())
            {
                return Result<SelectedText>.Failure(new Error(
                    "selection.keyboard-busy",
                    "Selection capture cannot inject input while shortcut keys are pressed."));
            }

            if (!await ClearClipboardAsync(cancellationToken).ConfigureAwait(false))
                _logger.LogWarning("Failed to clear the clipboard before selected-text capture.");

            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
            if (HasPressedCopyKey())
            {
                return Result<SelectedText>.Failure(new Error(
                    "selection.keyboard-busy",
                    "Selection capture cannot inject input while shortcut keys are pressed."));
            }
            if (!HasExpectedWindowContext(expectedForeground, expectedFocused))
                return ContextChanged();

            string? text = null;
            string? copySource = null;
            var copyInputsSent = false;
            uint lastSent = 0;
            var lastInputCount = 0;
            foreach (var (combination, sourceName) in new[]
                     {
                         ("Ctrl + Insert", "Ctrl+Insert"),
                         ("Ctrl + C", "Ctrl+C")
                     })
            {
                var copyInputs = WindowsKeyCombination.Parse(combination);
                var sent = _native.SendInputs(copyInputs);
                lastSent = sent;
                lastInputCount = copyInputs.Count;
                if (sent != copyInputs.Count)
                    continue;

                copyInputsSent = true;
                for (var attempt = 0; attempt < 20; attempt++)
                {
                    await Task.Delay(10, cancellationToken).ConfigureAwait(false);
                    var read = await _clipboardText.ReadAsync(cancellationToken).ConfigureAwait(false);
                    if (read.IsSuccess && !string.IsNullOrEmpty(read.Value))
                    {
                        text = read.Value;
                        copySource = sourceName;
                        break;
                    }
                }

                if (text is not null)
                    break;
            }

            if (!copyInputsSent && text is null)
            {
                return Result<SelectedText>.Failure(new Error(
                    "selection.copy-failed",
                    $"Only {lastSent} of {lastInputCount} copy key events were sent."));
            }

            if (snapshot is not null)
            {
                var token = await _clipboardSnapshots.GetChangeTokenAsync(cancellationToken).ConfigureAwait(false);
                if (token.IsSuccess)
                    expectedRestoreToken = token.Value;
            }

            return string.IsNullOrWhiteSpace(text)
                ? EmptySelection()
                : Result<SelectedText>.Success(new SelectedText(text, source, copySource!, position));
        }
        finally
        {
            if (snapshot is not null)
            {
                Result restored;
                if (expectedRestoreToken is not null)
                {
                    restored = await _clipboardSnapshots.RestoreIfUnchangedAsync(
                        snapshot,
                        expectedRestoreToken,
                        CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    restored = await _clipboardSnapshots.RestoreAsync(
                        snapshot,
                        CancellationToken.None).ConfigureAwait(false);
                }

                if (restored.IsFailure)
                    _logger.LogWarning("Unable to restore the clipboard after selection capture: {Error}", restored.Error.Message);
                await snapshot.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private string? TryGetSelectedTextFromFocusedControl()
    {
        var focused = WindowsWindowQuery.GetFocusedWindow();
        if (focused == IntPtr.Zero)
            return null;

        SendMessage(focused, EditGetSelection, out var start, out var end);
        if (end <= start)
            return null;

        var textLength = SendMessage(focused, WindowGetTextLength, IntPtr.Zero, IntPtr.Zero).ToInt64();
        if (textLength <= 0 || textLength > MaxWindowTextLength)
            return null;

        var buffer = new StringBuilder((int)textLength + 1);
        SendMessage(focused, WindowGetText, buffer.Capacity, buffer);
        var value = buffer.ToString();
        if (start < 0 || start >= value.Length)
            return null;

        var selectedLength = Math.Min(end, value.Length) - start;
        return selectedLength > 0 ? value.Substring(start, selectedLength) : null;
    }

    private bool HasExpectedWindowContext(IntPtr? foreground, IntPtr? focused)
    {
        var currentForeground = _native.GetForegroundWindow();
        var currentFocused = WindowsWindowQuery.GetFocusedWindow();
        return (!foreground.HasValue || foreground.Value == IntPtr.Zero || foreground.Value == currentForeground)
               && (!focused.HasValue || focused.Value == IntPtr.Zero || focused.Value == currentFocused);
    }

    private bool HasPressedCopyKey() =>
        _keyboardState.IsPressed(KeyboardKey.Control)
        || _keyboardState.IsPressed(KeyboardKey.Alt)
        || _keyboardState.IsPressed(KeyboardKey.Shift)
        || _keyboardState.IsPressed(KeyboardKey.LeftMeta)
        || _keyboardState.IsPressed(KeyboardKey.RightMeta)
        || _keyboardState.IsPressed(KeyboardKey.C);

    private static IntPtr? DecodeOptionalTarget(ExternalTargetToken target) =>
        target.IsEmpty ? null : WindowsTargetTokens.GetHandle(target);

    private static bool IsCompleteSelection(TextSelectionRange selection) =>
        selection.HasFocusedControl && selection.Start == 0 && selection.End > selection.Start;

    private static Result<SelectedText> ContextChanged() => Result<SelectedText>.Failure(
        new Error("selection.context-changed", "The source window changed before text could be captured."));

    private static Result<SelectedText> EmptySelection() => Result<SelectedText>.Failure(
        new Error("selection.empty", "No selected text was available."));

    private static async Task<bool> ClearClipboardAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    return EmptyClipboard();
                }
                finally
                {
                    CloseClipboard();
                }
            }
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }
        return false;
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr window, uint message, out int wParam, out int lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int SendMessage(IntPtr window, uint message, int wParam, StringBuilder lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();
}
