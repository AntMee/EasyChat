using System.Runtime.Versioning;
using EasyChat.Contracts.Platform;
using EasyChat.Shared.Results;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EasyChat.Infrastructure.Windows.Input;

[SupportedOSPlatform("windows")]
public sealed class WindowsWindowFocus : IWindowFocus
{
    private readonly WindowsNativeInputBackend _native = new();
    private readonly WindowsWindowStyleBackend _windowStyle = new();
    private readonly ILogger<WindowsWindowFocus> _logger;

    public WindowsWindowFocus(ILogger<WindowsWindowFocus>? logger = null)
    {
        _logger = logger ?? NullLogger<WindowsWindowFocus>.Instance;
    }

    public ValueTask<Result<ExternalTargetToken>> GetForegroundTargetAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Result<ExternalTargetToken>.Success(
            WindowsTargetTokens.FromHandle(_native.GetForegroundWindow())));
    }

    public ValueTask<Result<ExternalTargetToken>> GetFocusedTargetAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Result<ExternalTargetToken>.Success(
            WindowsTargetTokens.FromHandle(WindowsWindowQuery.GetFocusedWindow())));
    }

    public async ValueTask<Result> EnsureFocusedAsync(
        ExternalTargetToken target,
        CancellationToken cancellationToken = default)
    {
        IntPtr handle;
        try
        {
            handle = WindowsTargetTokens.GetHandle(target);
        }
        catch (ArgumentException exception)
        {
            return Result.Failure(new Error("window.target-invalid", exception.Message));
        }

        for (var attempt = 0; attempt < 5; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_native.GetForegroundWindow() == handle)
                return Result.Success();

            _native.ActivateWindow(handle, _logger);
            await Task.Delay(50, cancellationToken);
        }

        return _native.GetForegroundWindow() == handle
            ? Result.Success()
            : Result.Failure(new Error(
                "window.focus-failed",
                "The window could not be focused."));
    }

    public ValueTask<Result> ConfigureNoActivateAsync(
        ExternalTargetToken target,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            _windowStyle.ConfigureNoActivate(WindowsTargetTokens.GetHandle(target), _logger);
            return ValueTask.FromResult(Result.Success());
        }
        catch (Exception exception)
        {
            return ValueTask.FromResult(Result.Failure(
                new Error("window.no-activate-failed", exception.Message)));
        }
    }
}

[SupportedOSPlatform("windows")]
public sealed class WindowsWindowInputTransparency : IWindowInputTransparency
{
    private readonly WindowsWindowStyleBackend _native = new();

    public ValueTask<Result> SetClickThroughAsync(
        ExternalTargetToken target,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            _native.SetClickThrough(WindowsTargetTokens.GetHandle(target), enabled);
            return ValueTask.FromResult(Result.Success());
        }
        catch (Exception exception)
        {
            return ValueTask.FromResult(Result.Failure(
                new Error("window.click-through-failed", exception.Message)));
        }
    }
}
