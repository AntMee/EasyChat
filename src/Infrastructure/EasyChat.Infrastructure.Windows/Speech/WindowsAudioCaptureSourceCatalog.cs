using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using EasyChat.Contracts.Platform;
using NAudio.CoreAudioApi;

namespace EasyChat.Infrastructure.Windows.Speech;

[SupportedOSPlatform("windows")]
public sealed class WindowsAudioCaptureSourceCatalog : IAudioCaptureSourceCatalog
{
    private const string ProcessTokenPrefix = "windows:process:";
    private const string CaptureDeviceTokenPrefix = "windows:capture:";
    internal static AudioCaptureSourceToken SystemOutputToken { get; } = new("windows:system-output");

    public async ValueTask<IReadOnlyList<AudioCaptureSourceDescriptor>> GetSourcesAsync(
        CancellationToken cancellationToken = default) =>
        await Task.Run(() => ReadSources(cancellationToken), cancellationToken).ConfigureAwait(false);

    public static AudioCaptureSourceToken FromProcessId(int processId) =>
        new($"{ProcessTokenPrefix}{processId}");

    public static bool TryGetProcessId(AudioCaptureSourceToken token, out int processId)
    {
        processId = 0;
        return token.Value.StartsWith(ProcessTokenPrefix, StringComparison.Ordinal)
               && int.TryParse(token.Value.AsSpan(ProcessTokenPrefix.Length), out processId)
               && processId > 0;
    }

    private static IReadOnlyList<AudioCaptureSourceDescriptor> ReadSources(
        CancellationToken cancellationToken)
    {
        var result = new List<AudioCaptureSourceDescriptor>
        {
            new(
                SystemOutputToken,
                AudioCaptureSourceKind.SystemOutput,
                "Global",
                "Global",
                "System Audio",
                ReadOnlyMemory<byte>.Empty)
        };
        ReadCaptureDevices(result, cancellationToken);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (process.Id == 0 || string.IsNullOrEmpty(process.MainWindowTitle))
                        continue;
                    result.Add(new AudioCaptureSourceDescriptor(
                        FromProcessId(process.Id),
                        AudioCaptureSourceKind.Application,
                        process.ProcessName,
                        $"[{process.Id}] {process.ProcessName}",
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

    internal static AudioCaptureSourceToken FromCaptureDeviceId(string deviceId) =>
        new($"{CaptureDeviceTokenPrefix}{Convert.ToBase64String(Encoding.UTF8.GetBytes(deviceId))}");

    internal static bool TryGetCaptureDeviceId(
        AudioCaptureSourceToken token,
        out string deviceId)
    {
        deviceId = string.Empty;
        if (!token.Value.StartsWith(CaptureDeviceTokenPrefix, StringComparison.Ordinal))
            return false;
        try
        {
            deviceId = Encoding.UTF8.GetString(Convert.FromBase64String(
                token.Value[CaptureDeviceTokenPrefix.Length..]));
            return !string.IsNullOrWhiteSpace(deviceId);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    internal static bool IsVirtualCableOutputName(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && name.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase);

    private static void ReadCaptureDevices(
        ICollection<AudioCaptureSourceDescriptor> result,
        CancellationToken cancellationToken)
    {
        using var enumerator = new MMDeviceEnumerator();
        string? defaultDeviceId = null;
        try
        {
            defaultDeviceId = enumerator
                .GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications)
                .ID;
        }
        catch
        {
        }

        var devices = enumerator
            .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .OrderByDescending(device => string.Equals(
                device.ID,
                defaultDeviceId,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        foreach (var device in devices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (device)
            {
                var isCable = IsVirtualCableOutputName(device.FriendlyName);
                result.Add(new AudioCaptureSourceDescriptor(
                    FromCaptureDeviceId(device.ID),
                    AudioCaptureSourceKind.Microphone,
                    device.FriendlyName,
                    device.FriendlyName,
                    isCable ? "VB-Audio Virtual Cable microphone" : "Microphone",
                    ReadOnlyMemory<byte>.Empty,
                    IsVirtualCable: isCable,
                    IsDefault: string.Equals(
                        device.ID,
                        defaultDeviceId,
                        StringComparison.OrdinalIgnoreCase)));
            }
        }
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
