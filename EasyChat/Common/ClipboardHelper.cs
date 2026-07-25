using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Microsoft.Extensions.Logging;

namespace EasyChat.Common;

public static class ClipboardHelper
{
    public static async Task<ClipboardSnapshot?> BackupClipboardAsync(ILogger? logger = null)
    {
        IAsyncDataTransfer? data = null;
        try
        {
            var clipboard = GetClipboard();
            if (clipboard == null)
            {
                return null;
            }

            data = await clipboard.TryGetDataAsync();
            if (data == null)
            {
                return null;
            }

            var items = new List<ClipboardSnapshotItem>();
            foreach (var item in data.Items)
            {
                var values = new Dictionary<DataFormat, object?>();
                foreach (var format in item.Formats)
                {
                    var value = await item.TryGetRawAsync(format);
                    values[format] = CloneValue(value);
                }

                if (values.Count > 0)
                {
                    items.Add(new ClipboardSnapshotItem(values));
                }
            }

            return new ClipboardSnapshot(items);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to back up clipboard");
            return null;
        }
        finally
        {
            data?.Dispose();
        }
    }

    public static async Task RestoreClipboardAsync(ClipboardSnapshot? backup, ILogger? logger = null)
    {
        if (backup == null) return;

        var transferred = false;
        try
        {
            var clipboard = GetClipboard();
            if (clipboard == null)
            {
                backup.Dispose();
                return;
            }

            // Use a new in-memory transfer. The original Windows OLE wrapper must
            // never be handed back to Avalonia, otherwise GetData recurses via Items.
            await clipboard.SetDataAsync(backup.CreateTransfer());
            transferred = true;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to restore clipboard data");
        }
        finally
        {
            // SetDataAsync takes ownership of the new transfer. Its values must stay
            // alive until the platform releases that transfer.
            if (!transferred)
            {
                backup.Dispose();
            }
        }
    }

    public static async Task RestoreClipboardIfUnchangedAsync(
        ClipboardSnapshot? backup,
        uint expectedClipboardSequence,
        ILogger? logger = null)
    {
        if (backup == null) return;

        // A real user copy wins over restoring the clipboard snapshot. This is
        // especially important now that restoration is deferred until after the
        // selection icon has been made visible.
        if (GetClipboardSequenceNumber() != expectedClipboardSequence)
        {
            logger?.LogDebug("Skipping clipboard restoration because clipboard changed after selection capture");
            backup.Dispose();
            return;
        }

        await RestoreClipboardAsync(backup, logger);
    }

    public static uint GetClipboardSequenceNumber()
    {
        return GetClipboardSequenceNumberNative();
    }

    [DllImport("user32.dll", EntryPoint = "GetClipboardSequenceNumber")]
    private static extern uint GetClipboardSequenceNumberNative();

    private static object? CloneValue(object? value)
    {
        switch (value)
        {
            case byte[] bytes:
                return bytes.ToArray();
            case Array array:
                return array.Clone();
            case ReadOnlyMemory<byte> memory:
                return memory.ToArray();
            case Memory<byte> memory:
                return memory.ToArray();
            case Stream stream:
            {
                var position = stream.CanSeek ? stream.Position : 0;
                using var copy = new MemoryStream();
                if (stream.CanSeek)
                {
                    stream.Position = 0;
                }

                stream.CopyTo(copy);
                if (stream.CanSeek)
                {
                    stream.Position = position;
                }

                return new MemoryStream(copy.ToArray(), writable: false);
            }
            case Bitmap bitmap:
            {
                using var stream = new MemoryStream();
                bitmap.Save(stream);
                stream.Position = 0;
                return new Bitmap(stream);
            }
            case ICloneable cloneable:
                return cloneable.Clone();
            default:
                return value;
        }
    }

    private static IClipboard? GetClipboard()
    {
        return (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow?.Clipboard;
    }

    public sealed class ClipboardSnapshot : IDisposable
    {
        private readonly IReadOnlyList<ClipboardSnapshotItem> _items;
        private bool _disposed;

        internal ClipboardSnapshot(IReadOnlyList<ClipboardSnapshotItem> items)
        {
            _items = items;
        }

        internal IAsyncDataTransfer CreateTransfer()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(ClipboardSnapshot));
            }

            return new SnapshotDataTransfer(_items);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            foreach (var item in _items)
            {
                foreach (var value in item.Values.Values)
                {
                    if (value is Stream or Bitmap)
                    {
                        (value as IDisposable)?.Dispose();
                    }
                }
            }
        }
    }

    internal sealed class ClipboardSnapshotItem
    {
        public ClipboardSnapshotItem(IReadOnlyDictionary<DataFormat, object?> values)
        {
            Values = values;
        }

        public IReadOnlyDictionary<DataFormat, object?> Values { get; }
    }

    private sealed class SnapshotDataTransfer : IAsyncDataTransfer, IDataTransfer, IDisposable
    {
        private readonly IReadOnlyList<SnapshotDataTransferItem> _items;

        public SnapshotDataTransfer(IReadOnlyList<ClipboardSnapshotItem> items)
        {
            _items = items.Select(item => new SnapshotDataTransferItem(item.Values)).ToArray();
        }

        public IReadOnlyList<DataFormat> Formats => _items
            .SelectMany(item => item.Formats)
            .Distinct()
            .ToArray();

        public IReadOnlyList<IAsyncDataTransferItem> Items => _items;

        IReadOnlyList<IDataTransferItem> IDataTransfer.Items => _items;

        public void Dispose()
        {
            foreach (var item in _items)
            {
                item.Dispose();
            }
        }
    }

    private sealed class SnapshotDataTransferItem : IAsyncDataTransferItem, IDataTransferItem, IDisposable
    {
        private readonly IReadOnlyDictionary<DataFormat, object?> _values;

        public SnapshotDataTransferItem(IReadOnlyDictionary<DataFormat, object?> values)
        {
            _values = values;
        }

        public IReadOnlyList<DataFormat> Formats => _values.Keys.ToArray();

        public object? TryGetRaw(DataFormat format)
        {
            _values.TryGetValue(format, out var value);
            return value;
        }

        public Task<object?> TryGetRawAsync(DataFormat format)
        {
            return Task.FromResult(TryGetRaw(format));
        }

        public void Dispose()
        {
            foreach (var value in _values.Values)
            {
                if (value is Stream or Bitmap)
                {
                    (value as IDisposable)?.Dispose();
                }
            }
        }
    }
}
