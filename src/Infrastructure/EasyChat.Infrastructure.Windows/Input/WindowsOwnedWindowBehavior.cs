using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace EasyChat.Infrastructure.Windows.Input;

[SupportedOSPlatform("windows")]
public sealed class WindowsOwnedWindowBehavior(ILogger<WindowsOwnedWindowBehavior> logger)
{
    private readonly WindowsWindowStyleBackend _native = new();

    public void ConfigureNoActivate(nint window)
    {
        if (window == nint.Zero)
            throw new ArgumentException("A native window handle is required.", nameof(window));
        _native.ConfigureNoActivate(window, logger);
    }

    public void BringToFrontWithoutActivating(nint window)
    {
        if (window == nint.Zero)
            throw new ArgumentException("A native window handle is required.", nameof(window));
        _native.BringToFrontWithoutActivating(window, logger);
    }

    public void SetClickThrough(nint window, bool enabled)
    {
        if (window == nint.Zero)
            throw new ArgumentException("A native window handle is required.", nameof(window));
        _native.SetClickThrough(window, enabled);
    }

    public bool TrySetExcludedFromCapture(nint window, bool enabled)
    {
        if (window == nint.Zero)
            return false;
        return _native.TrySetExcludedFromCapture(window, enabled);
    }
}
