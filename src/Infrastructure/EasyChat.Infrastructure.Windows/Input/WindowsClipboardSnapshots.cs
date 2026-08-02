using System.Runtime.Versioning;
using EasyChat.Contracts.Platform;
using EasyChat.Shared.Results;
using Microsoft.Extensions.Logging;
using ContractSnapshot = EasyChat.Contracts.Platform.IClipboardSnapshot;

namespace EasyChat.Infrastructure.Windows.Input;

[SupportedOSPlatform("windows")]
public sealed class WindowsClipboardSnapshots : IClipboardSnapshots
{
    private readonly WindowsClipboardBackend _backend;
    private readonly ILogger? _logger;

    public WindowsClipboardSnapshots(
        ILogger<WindowsClipboardSnapshots>? logger = null)
        : this(new WindowsClipboardBackend(), logger)
    {
    }

    private WindowsClipboardSnapshots(
        WindowsClipboardBackend backend,
        ILogger? logger)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _logger = logger;
    }

    internal static WindowsClipboardSnapshots CreateForOperations(ILogger logger) =>
        new(new WindowsClipboardBackend(), logger);

    public ValueTask<Result<IClipboardChangeToken>> GetChangeTokenAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            return ValueTask.FromResult(Result<IClipboardChangeToken>.Success(
                new ChangeToken(_backend.GetChangeToken())));
        }
        catch (Exception ex)
        {
            return ValueTask.FromResult(Result<IClipboardChangeToken>.Failure(
                new Error("clipboard.change-token-failed", ex.Message)));
        }
    }

    public ValueTask<Result<bool>> IsChangeTokenCurrentAsync(
        IClipboardChangeToken changeToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changeToken);
        cancellationToken.ThrowIfCancellationRequested();

        if (changeToken is not ChangeToken token)
        {
            return ValueTask.FromResult(Result<bool>.Failure(new Error(
                "clipboard.change-token-invalid",
                "The clipboard change token was not created by this service.")));
        }

        try
        {
            return ValueTask.FromResult(Result<bool>.Success(
                _backend.GetChangeToken() == token.Value));
        }
        catch (Exception ex)
        {
            return ValueTask.FromResult(Result<bool>.Failure(
                new Error("clipboard.change-token-failed", ex.Message)));
        }
    }

    public ValueTask<Result<ContractSnapshot>> CaptureAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var snapshot = _backend.Backup(_logger);
        return snapshot is null
            ? ValueTask.FromResult(Result<ContractSnapshot>.Failure(new Error(
                "clipboard.capture-failed",
                "The clipboard could not be captured.")))
            : ValueTask.FromResult(Result<ContractSnapshot>.Success(
                new Snapshot(snapshot)));
    }

    public ValueTask<Result> RestoreAsync(
        ContractSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();

        return Restore(
            snapshot,
            value => _backend.Restore(value, _logger),
            "The clipboard snapshot was not created by this service.");
    }

    public ValueTask<Result> RestoreIfUnchangedAsync(
        ContractSnapshot snapshot,
        IClipboardChangeToken expectedChangeToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(expectedChangeToken);
        cancellationToken.ThrowIfCancellationRequested();

        if (expectedChangeToken is not ChangeToken token)
        {
            return ValueTask.FromResult(Result.Failure(new Error(
                "clipboard.snapshot-invalid",
                "The clipboard snapshot or change token was not created by this service.")));
        }

        return Restore(
            snapshot,
            value => _backend.RestoreIfUnchanged(value, token.Value, _logger),
            "The clipboard snapshot or change token was not created by this service.");
    }

    private static ValueTask<Result> Restore(
        ContractSnapshot snapshot,
        Action<WindowsClipboardBackend.ClipboardSnapshot> restore,
        string invalidMessage)
    {
        if (snapshot is not Snapshot owned || !owned.TryRestore(restore, out var failure))
        {
            return ValueTask.FromResult(Result.Failure(new Error(
                "clipboard.snapshot-invalid",
                invalidMessage)));
        }

        return failure is null
            ? ValueTask.FromResult(Result.Success())
            : ValueTask.FromResult(Result.Failure(
                new Error("clipboard.restore-failed", failure.Message)));
    }

    private sealed record ChangeToken(uint Value) : IClipboardChangeToken;

    private sealed class Snapshot(
        WindowsClipboardBackend.ClipboardSnapshot value) : ContractSnapshot
    {
        private readonly object _gate = new();
        private WindowsClipboardBackend.ClipboardSnapshot? _value = value;

        public bool TryRestore(
            Action<WindowsClipboardBackend.ClipboardSnapshot> restore,
            out Exception? failure)
        {
            lock (_gate)
            {
                if (_value is null)
                {
                    failure = null;
                    return false;
                }

                try
                {
                    restore(_value);
                    _value = null;
                    failure = null;
                }
                catch (Exception ex)
                {
                    failure = ex;
                }

                return true;
            }
        }

        public ValueTask DisposeAsync()
        {
            lock (_gate)
            {
                _value?.Dispose();
                _value = null;
            }

            return ValueTask.CompletedTask;
        }
    }
}
