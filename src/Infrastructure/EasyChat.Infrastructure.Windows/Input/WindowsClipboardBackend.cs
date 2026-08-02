using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace EasyChat.Infrastructure.Windows.Input;

/// <summary>
/// Owns the Win32, OLE and worker-process implementation used by the platform
/// clipboard contract.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsClipboardBackend
{
    private const uint GmemMoveable = 0x0002;
    private const int MaxFormatBytes = 64 * 1024 * 1024;
    private static readonly Lazy<ClipboardWorkerClient> Worker =
        new(() => new ClipboardWorkerClient(), LazyThreadSafetyMode.ExecutionAndPublication);

    internal static void RunWorker(string pipeName)
    {
        using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.None);
        server.WaitForConnection();

        var worker = new ClipboardWorker();
        using var reader = new BinaryReader(server);
        using var writer = new BinaryWriter(server);
        try
        {
            while (true)
            {
                var command = reader.ReadByte();
                switch (command)
                {
                    case 1: // backup
                        {
                            var snapshot = worker.Backup(null);
                            writer.Write(snapshot != null);
                            if (snapshot != null)
                            {
                                writer.Write(snapshot.Token.ToByteArray());
                            }

                            writer.Flush();
                            break;
                        }
                    case 2: // restore
                        {
                            var token = new Guid(ReadGuid(reader));
                            worker.Restore(token, null);
                            writer.Write(true);
                            writer.Flush();
                            break;
                        }
                    case 3: // release
                        {
                            var token = new Guid(ReadGuid(reader));
                            worker.Release(token);
                            writer.Write(true);
                            writer.Flush();
                            break;
                        }
                    case 4: // quit
                        return;
                    default:
                        return;
                }
            }
        }
        catch (EndOfStreamException)
        {
            // Parent process exited or disconnected.
        }
    }

    internal ClipboardSnapshot? Backup(ILogger? logger = null)
    {
        try
        {
            return Worker.Value.Backup(logger);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Unable to back up the native clipboard.");
            return null;
        }
    }

    internal void Restore(ClipboardSnapshot? snapshot, ILogger? logger = null)
    {
        if (snapshot == null)
        {
            return;
        }

        try
        {
            Worker.Value.Restore(snapshot.Token, logger);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Unable to restore the native clipboard.");
        }
        finally
        {
            snapshot.Dispose();
        }
    }

    internal void RestoreIfUnchanged(
        ClipboardSnapshot? snapshot,
        uint expectedChangeToken,
        ILogger? logger = null)
    {
        if (snapshot == null)
        {
            return;
        }

        if (GetChangeToken() != expectedChangeToken)
        {
            logger?.LogDebug("Skipping clipboard restoration because the clipboard changed after selection capture");
            snapshot.Dispose();
            return;
        }

        Restore(snapshot, logger);
    }

    internal uint GetChangeToken() => GetCurrentChangeToken();

    internal static uint GetCurrentChangeToken() => GetClipboardSequenceNumberNative();

    internal sealed class ClipboardSnapshot : IDisposable
    {
        internal ClipboardSnapshot(Guid token)
        {
            Token = token;
        }

        internal Guid Token { get; }
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Worker.Value.Release(Token);
        }
    }

    private sealed class ClipboardWorker
    {
        private readonly BlockingCollection<Action> _queue = new();
        private readonly Dictionary<Guid, SnapshotData> _snapshots = new();
        private readonly Thread _thread;
        private object? _activeOleObject;

        public ClipboardWorker()
        {
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "EasyChat Clipboard STA"
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        public ClipboardSnapshot? Backup(ILogger? logger)
        {
            return Invoke(() =>
            {
                var native = CaptureNativeFormats(logger);
                if (native.Count > 0)
                {
                    var nativeToken = Guid.NewGuid();
                    _snapshots[nativeToken] = new SnapshotData(native, null);
                    return new ClipboardSnapshot(nativeToken);
                }

                // OLE is only used when the native clipboard has no readable
                // formats. Keep the provider's object intact; do not enumerate
                // or manually clone STGMEDIUM/GDI handles in-process.
                var hr = OleGetClipboard(out var oleObject);
                if (hr < 0 || oleObject == null)
                {
                    return null;
                }

                var token = Guid.NewGuid();
                _snapshots[token] = new SnapshotData(Array.Empty<ClipboardEntry>(), oleObject);
                return new ClipboardSnapshot(token);
            });
        }

        public void Restore(Guid token, ILogger? logger)
        {
            Invoke(() =>
            {
                if (!_snapshots.Remove(token, out var snapshot))
                {
                    return;
                }

                if (snapshot.NativeEntries.Count > 0)
                {
                    RestoreNativeFormats(snapshot.NativeEntries, logger);
                    snapshot.ReleaseOleObject();
                    return;
                }

                if (snapshot.OleObject == null)
                {
                    return;
                }

                try
                {
                    var hr = OleSetClipboard(snapshot.OleObject);
                    if (hr < 0)
                    {
                        logger?.LogWarning("OLE clipboard restore failed. OleSetClipboard=0x{HResult:X8}", hr);
                        snapshot.ReleaseOleObject();
                        return;
                    }

                    if (_activeOleObject != null)
                    {
                        ReleaseComObject(_activeOleObject);
                    }

                    // Keep the provider object alive for delayed rendering.
                    _activeOleObject = snapshot.OleObject;
                    snapshot.OleObject = null;
                }
                catch
                {
                    snapshot.ReleaseOleObject();
                    throw;
                }
            });
        }

        public void Release(Guid token)
        {
            try
            {
                Invoke(() =>
                {
                    if (_snapshots.Remove(token, out var snapshot))
                    {
                        snapshot.ReleaseOleObject();
                    }
                });
            }
            catch
            {
                // The process may be shutting down.
            }
        }

        private List<ClipboardEntry> CaptureNativeFormats(ILogger? logger)
        {
            var entries = new List<ClipboardEntry>();
            if (!OpenClipboardWithRetry())
            {
                return entries;
            }

            try
            {
                uint format = 0;
                while ((format = EnumClipboardFormats(format)) != 0)
                {
                    // These formats contain GDI handles, not HGLOBAL memory.
                    // Never call GlobalSize/GlobalLock on them.
                    if (!IsHGlobalFormat(format))
                    {
                        continue;
                    }

                    var handle = GetClipboardData(format);
                    var bytes = CopyGlobalMemory(handle);
                    if (bytes != null)
                    {
                        entries.Add(new ClipboardEntry(format, bytes));
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "Unable to enumerate native clipboard formats");
            }
            finally
            {
                CloseClipboard();
            }

            return entries;
        }

        private static void RestoreNativeFormats(
            IReadOnlyList<ClipboardEntry> entries,
            ILogger? logger)
        {
            if (!OpenClipboardWithRetry())
            {
                logger?.LogDebug("Unable to open the native clipboard for restore");
                return;
            }

            try
            {
                if (!EmptyClipboard())
                {
                    logger?.LogDebug("Unable to empty the native clipboard before restore");
                    return;
                }

                foreach (var entry in entries)
                {
                    var handle = CreateGlobalMemory(entry.Data);
                    if (SetClipboardData(entry.Format, handle) == IntPtr.Zero)
                    {
                        GlobalFree(handle);
                    }
                }
            }
            finally
            {
                CloseClipboard();
            }
        }

        private T Invoke<T>(Func<T> action)
        {
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Add(() =>
            {
                try
                {
                    completion.SetResult(action());
                }
                catch (Exception ex)
                {
                    completion.SetException(ex);
                }
            });
            return completion.Task.GetAwaiter().GetResult();
        }

        private void Invoke(Action action)
        {
            Invoke(() =>
            {
                action();
                return true;
            });
        }

        private void Run()
        {
            var oleResult = OleInitialize(IntPtr.Zero);
            try
            {
                while (true)
                {
                    while (_queue.TryTake(out var action))
                    {
                        action();
                    }

                    MsgWaitForMultipleObjectsEx(0, IntPtr.Zero, 10, QsAllInput, MwmoInputAvailable);
                    while (PeekMessage(out var message, IntPtr.Zero, 0, 0, PmRemove))
                    {
                        TranslateMessage(ref message);
                        DispatchMessage(ref message);
                    }
                }
            }
            finally
            {
                if (oleResult >= 0)
                {
                    OleUninitialize();
                }
            }
        }
    }

    private sealed class ClipboardWorkerClient
    {
        private readonly object _gate = new();
        private Process? _process;
        private NamedPipeClientStream? _pipe;
        private BinaryReader? _reader;
        private BinaryWriter? _writer;

        public ClipboardSnapshot? Backup(ILogger? logger)
        {
            lock (_gate)
            {
                try
                {
                    EnsureConnected();
                    _writer!.Write((byte)1);
                    _writer.Flush();
                    if (!_reader!.ReadBoolean())
                    {
                        return null;
                    }

                    var token = new Guid(ReadGuid(_reader));
                    return new ClipboardSnapshot(token);
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "Clipboard worker backup failed.");
                    Disconnect();
                    return null;
                }
            }
        }

        public void Restore(Guid token, ILogger? logger)
        {
            lock (_gate)
            {
                try
                {
                    EnsureConnected();
                    _writer!.Write((byte)2);
                    _writer.Write(token.ToByteArray());
                    _writer.Flush();
                    _reader!.ReadBoolean();
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "Clipboard worker restore failed.");
                    Disconnect();
                }
            }
        }

        public void Release(Guid token)
        {
            lock (_gate)
            {
                try
                {
                    EnsureConnected();
                    _writer!.Write((byte)3);
                    _writer.Write(token.ToByteArray());
                    _writer.Flush();
                    _reader!.ReadBoolean();
                }
                catch
                {
                    Disconnect();
                }
            }
        }

        private void EnsureConnected()
        {
            if (_pipe?.IsConnected == true)
            {
                return;
            }

            Disconnect();
            var pipeName = "EasyChat.Clipboard." + Guid.NewGuid().ToString("N");
            var executable = Environment.ProcessPath
                             ?? throw new InvalidOperationException("Unable to locate EasyChat executable.");
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                Arguments = "--clipboard-worker " + pipeName,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            }) ?? throw new InvalidOperationException("Unable to start clipboard worker process.");

            var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.None);
            try
            {
                pipe.Connect(3000);
            }
            catch
            {
                pipe.Dispose();
                process.Dispose();
                throw;
            }

            _process = process;
            _pipe = pipe;
            _reader = new BinaryReader(pipe);
            _writer = new BinaryWriter(pipe);
        }

        private void Disconnect()
        {
            try
            {
                _pipe?.Dispose();
            }
            catch
            {
                // ignored
            }

            try
            {
                if (_process is { HasExited: false })
                {
                    _process.Kill(entireProcessTree: true);
                }
                _process?.Dispose();
            }
            catch
            {
                // ignored
            }

            _reader = null;
            _writer = null;
            _pipe = null;
            _process = null;
        }
    }

    private static byte[] ReadGuid(BinaryReader reader)
    {
        var bytes = reader.ReadBytes(16);
        if (bytes.Length != 16)
        {
            throw new EndOfStreamException("Clipboard worker returned an incomplete token.");
        }

        return bytes;
    }

    private sealed class SnapshotData
    {
        public SnapshotData(IReadOnlyList<ClipboardEntry> nativeEntries, object? oleObject)
        {
            NativeEntries = nativeEntries;
            OleObject = oleObject;
        }

        public IReadOnlyList<ClipboardEntry> NativeEntries { get; }
        public object? OleObject { get; set; }

        public void ReleaseOleObject()
        {
            if (OleObject != null)
            {
                ReleaseComObject(OleObject);
                OleObject = null;
            }
        }
    }

    private sealed record ClipboardEntry(uint Format, byte[] Data);

    private static bool IsHGlobalFormat(uint format)
    {
        return format switch
        {
            2 => false,  // CF_BITMAP
            3 => false,  // CF_METAFILEPICT
            9 => false,  // CF_PALETTE
            14 => false, // CF_ENHMETAFILE
            _ => true
        };
    }

    private static byte[]? CopyGlobalMemory(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        var sizeValue = GlobalSize(handle).ToUInt64();
        if (sizeValue == 0 || sizeValue > MaxFormatBytes)
        {
            return null;
        }

        var source = GlobalLock(handle);
        if (source == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var bytes = new byte[checked((int)sizeValue)];
            Marshal.Copy(source, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    private static IntPtr CreateGlobalMemory(byte[] bytes)
    {
        var handle = GlobalAlloc(GmemMoveable, new UIntPtr((uint)Math.Max(1, bytes.Length)));
        if (handle == IntPtr.Zero)
        {
            throw new OutOfMemoryException("Unable to allocate clipboard memory");
        }

        var target = GlobalLock(handle);
        if (target == IntPtr.Zero)
        {
            GlobalFree(handle);
            throw new OutOfMemoryException("Unable to lock clipboard memory");
        }

        try
        {
            if (bytes.Length > 0)
            {
                Marshal.Copy(bytes, 0, target, bytes.Length);
            }
        }
        finally
        {
            GlobalUnlock(handle);
        }

        return handle;
    }

    private static bool OpenClipboardWithRetry()
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                return true;
            }

            Thread.Sleep(10);
        }

        return false;
    }

    private static void ReleaseComObject(object value)
    {
        if (Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }

    private const uint QsAllInput = 0x04FF;
    private const uint MwmoInputAvailable = 0x0004;
    private const uint PmRemove = 0x0001;

    [DllImport("ole32.dll")]
    private static extern int OleInitialize(IntPtr reserved);

    [DllImport("ole32.dll")]
    private static extern void OleUninitialize();

    [DllImport("ole32.dll")]
    private static extern int OleGetClipboard([MarshalAs(UnmanagedType.Interface)] out object? dataObject);

    [DllImport("ole32.dll")]
    private static extern int OleSetClipboard([MarshalAs(UnmanagedType.Interface)] object dataObject);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint EnumClipboardFormats(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr data);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern UIntPtr GlobalSize(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr memory);

    [DllImport("user32.dll")]
    private static extern uint MsgWaitForMultipleObjectsEx(uint count, IntPtr handles, uint milliseconds, uint wakeMask, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out NativeMessage message, IntPtr window, uint minMessage, uint maxMessage, uint removeMessage);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref NativeMessage message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref NativeMessage message);

    [DllImport("user32.dll", EntryPoint = "GetClipboardSequenceNumber")]
    private static extern uint GetClipboardSequenceNumberNative();

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr HWnd;
        public uint Message;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int PointX;
        public int PointY;
    }
}
public static class WindowsClipboardWorker
{
    public static void Run(string pipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        WindowsClipboardBackend.RunWorker(pipeName);
    }
}
