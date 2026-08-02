using EasyChat.Contracts.Platform;
using EasyChat.Shared.Results;

namespace EasyChat.Application.Shortcuts;

internal static class ShortcutGestureParser
{
    public static Result<ShortcutGesture> Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Result<ShortcutGesture>.Failure(new Error(
                "shortcut.gesture-empty",
                "The shortcut key combination is empty."));
        }

        var parts = value.Split(
            ['+', ' '],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var modifiers = ShortcutModifiers.None;
        string? key = null;
        foreach (var part in parts)
        {
            if (TryParseModifier(part, out var modifier))
            {
                modifiers |= modifier;
                continue;
            }

            if (key is not null)
            {
                return Result<ShortcutGesture>.Failure(new Error(
                    "shortcut.gesture-invalid",
                    $"The shortcut '{value}' contains more than one key."));
            }

            key = part;
        }

        return string.IsNullOrWhiteSpace(key)
            ? Result<ShortcutGesture>.Failure(new Error(
                "shortcut.gesture-invalid",
                $"The shortcut '{value}' does not contain a key."))
            : Result<ShortcutGesture>.Success(new ShortcutGesture(key, modifiers));
    }

    private static bool TryParseModifier(string value, out ShortcutModifiers modifier)
    {
        modifier = value.ToUpperInvariant() switch
        {
            "CTRL" or "CONTROL" => ShortcutModifiers.Control,
            "ALT" => ShortcutModifiers.Alt,
            "SHIFT" => ShortcutModifiers.Shift,
            "WIN" or "WINDOWS" or "META" => ShortcutModifiers.Meta,
            _ => ShortcutModifiers.None
        };
        return modifier != ShortcutModifiers.None;
    }
}
