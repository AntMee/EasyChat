using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using EasyChat.Contracts.Platform;
using EasyChat.Shared.Results;

namespace EasyChat.Infrastructure.Windows.Input;

/// <summary>
/// Enumerates running interactive applications and resolves a window target token to its owning
/// process identity. The identity is the executable file name (for example "chrome.exe") and is
/// stable across sessions, unlike the window handle encoded in <see cref="ExternalTargetToken"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsRunningProcessCatalog : IRunningProcessCatalog
{
    public async ValueTask<IReadOnlyList<RunningProcessDescriptor>> GetRunningProcessesAsync(
        CancellationToken cancellationToken = default) =>
        await Task.Run(() => ReadProcesses(cancellationToken), cancellationToken).ConfigureAwait(false);

    public ValueTask<Result<string>> ResolveProcessIdentifierAsync(
        ExternalTargetToken target,
        CancellationToken cancellationToken = default)
    {
        if (target.IsEmpty)
            return ValueTask.FromResult(Result<string>.Failure(new Error(
                "running-process.empty-target",
                "The target token is empty.")));

        IntPtr handle;
        try
        {
            handle = WindowsTargetTokens.GetHandle(target);
        }
        catch (ArgumentException exception)
        {
            return ValueTask.FromResult(Result<string>.Failure(new Error(
                "running-process.invalid-target",
                exception.Message)));
        }

        if (handle == IntPtr.Zero)
            return ValueTask.FromResult(Result<string>.Failure(new Error(
                "running-process.empty-target",
                "The target token does not reference a window.")));

        var processId = GetWindowProcessId(handle);
        if (processId == 0)
            return ValueTask.FromResult(Result<string>.Failure(new Error(
                "running-process.unavailable",
                "The window is not owned by a visible process.")));

        var identifier = ResolveIdentifier((int)processId);
        return string.IsNullOrWhiteSpace(identifier)
            ? ValueTask.FromResult(Result<string>.Failure(new Error(
                "running-process.unavailable",
                "Unable to resolve the process identity of the target window.")))
            : ValueTask.FromResult(Result<string>.Success(identifier));
    }

    private static IReadOnlyList<RunningProcessDescriptor> ReadProcesses(
        CancellationToken cancellationToken)
    {
        var result = new List<RunningProcessDescriptor>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (process.Id == 0 || string.IsNullOrEmpty(process.MainWindowTitle))
                        continue;

                    var executablePath = GetProcessPath(process.Id);
                    var identifier = ResolveIdentifier(process.Id, process.ProcessName);
                    if (string.IsNullOrWhiteSpace(identifier))
                        continue;

                    result.Add(new RunningProcessDescriptor(
                        identifier,
                        process.ProcessName,
                        ReadDescription(executablePath),
                        process.MainWindowTitle,
                        ReadIconPng(executablePath)));
                }
                catch
                {
                    // Processes may exit or deny access while the snapshot is taken.
                }
            }
        }

        return result;
    }

    private static string? ResolveIdentifier(int processId, string? fallbackProcessName = null)
    {
        var path = GetProcessPath(processId);
        var fileName = string.IsNullOrWhiteSpace(path)
            ? null
            : Path.GetFileName(path);
        if (!string.IsNullOrWhiteSpace(fileName))
            return fileName;

        return string.IsNullOrWhiteSpace(fallbackProcessName)
            ? null
            : $"{fallbackProcessName}.exe";
    }

    private static uint GetWindowProcessId(IntPtr window)
    {
        GetWindowThreadProcessId(window, out var processId);
        return processId;
    }

    private static string? GetProcessPath(int processId)
    {
        const int processQueryLimitedInformation = 0x1000;
        var process = OpenProcess(processQueryLimitedInformation, false, processId);
        if (process == IntPtr.Zero)
            return null;
        try
        {
            var path = new StringBuilder(1024);
            var length = path.Capacity;
            return QueryFullProcessImageName(process, 0, path, ref length)
                ? path.ToString()
                : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private static ReadOnlyMemory<byte> ReadIconPng(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            return ReadOnlyMemory<byte>.Empty;
        using var icon = Icon.ExtractAssociatedIcon(executablePath);
        if (icon is null)
            return ReadOnlyMemory<byte>.Empty;
        using var bitmap = icon.ToBitmap();
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    /// <summary>
    /// Stable software description (file description or product name) used instead of the volatile
    /// window title in selection lists. Falls back to <c>null</c> when the executable carries no
    /// version metadata.
    /// </summary>
    private static string? ReadDescription(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            return null;
        try
        {
            var info = FileVersionInfo.GetVersionInfo(executablePath);
            var description = info.FileDescription;
            if (string.IsNullOrWhiteSpace(description))
                description = info.ProductName;
            return string.IsNullOrWhiteSpace(description) ? null : description;
        }
        catch
        {
            return null;
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        IntPtr process,
        int flags,
        StringBuilder path,
        ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
