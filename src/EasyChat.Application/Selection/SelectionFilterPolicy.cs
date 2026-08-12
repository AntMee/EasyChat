using EasyChat.Contracts.Settings;

namespace EasyChat.Application.Selection;

/// <summary>
/// Pure policy deciding whether the selection workflow may act inside the application that owns
/// the focused window. Whitelist allows only listed applications; blacklist blocks only listed
/// applications; a null identifier is treated as "not listed" (allowed in blacklist mode,
/// blocked in whitelist mode).
/// </summary>
public static class SelectionFilterPolicy
{
    public static bool IsAllowed(
        SelectionFilterMode mode,
        IReadOnlyList<SelectionAppEntrySettings>? appList,
        string? processIdentifier)
    {
        var listed = processIdentifier is not null
                     && (appList?.Any(entry => string.Equals(
                             entry.Identifier,
                             processIdentifier,
                             StringComparison.OrdinalIgnoreCase)) ?? false);
        return mode switch
        {
            SelectionFilterMode.Whitelist => listed,
            SelectionFilterMode.Blacklist => !listed,
            _ => true
        };
    }
}
