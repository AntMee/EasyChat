using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using EasyChat.Contracts.Platform;

namespace EasyChat.Infrastructure.Windows.Processes;

[SupportedOSPlatform("windows")]
public sealed class WindowsProcessCatalog : IProcessCatalog
{
    public async ValueTask<IReadOnlyList<ProcessDescriptor>> GetProcessesAsync(
        CancellationToken cancellationToken = default) =>
        await Task.Run(() => ReadProcesses(cancellationToken), cancellationToken).ConfigureAwait(false);

    private static IReadOnlyList<ProcessDescriptor> ReadProcesses(CancellationToken cancellationToken)
    {
        var result = new List<ProcessDescriptor>
        {
            new(0, "Global", "System Audio", ReadOnlyMemory<byte>.Empty)
        };
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (process.Id == 0 || string.IsNullOrEmpty(process.MainWindowTitle))
                        continue;
                    result.Add(new ProcessDescriptor(
                        process.Id,
                        process.ProcessName,
                        process.MainWindowTitle,
                        ReadIcon(GetProcessPath(process.Id))));
                }
                catch
                {
                }
            }
        }

        return result;
    }

    private static ReadOnlyMemory<byte> ReadIcon(string? executablePath)
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
