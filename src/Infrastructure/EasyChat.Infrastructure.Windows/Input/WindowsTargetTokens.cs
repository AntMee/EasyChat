using System.Globalization;
using EasyChat.Contracts.Platform;

namespace EasyChat.Infrastructure.Windows.Input;

internal static class WindowsTargetTokens
{
    private const string Prefix = "win32:";

    public static ExternalTargetToken FromHandle(IntPtr handle) =>
        handle == IntPtr.Zero
            ? ExternalTargetToken.None
            : new ExternalTargetToken($"{Prefix}{handle.ToInt64():X}");

    public static IntPtr GetHandle(ExternalTargetToken target)
    {
        if (!target.Value.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) ||
            !long.TryParse(
                target.Value.AsSpan(Prefix.Length),
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out var value))
        {
            throw new ArgumentException(
                "The target token was not created by the Windows infrastructure module.",
                nameof(target));
        }

        return new IntPtr(value);
    }
}
