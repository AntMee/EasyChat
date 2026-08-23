using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using EasyChat.Contracts.Platform;

namespace EasyChat.Presentation.ImageTranslation;

public static class AvaloniaImageFrames
{
    public static void CopyToFramebuffer(
        ImageFrame frame,
        IntPtr destination,
        int destinationRowBytes)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.PixelFormat != ImagePixelFormat.Bgra32)
            throw new NotSupportedException($"Pixel format '{frame.PixelFormat}' is not supported.");

        var rowBytes = checked(frame.Width * 4);
        if (frame.Stride < rowBytes)
            throw new ArgumentException("The source stride is smaller than a pixel row.", nameof(frame));
        if (destinationRowBytes < rowBytes)
            throw new ArgumentOutOfRangeException(
                nameof(destinationRowBytes),
                "The destination stride is smaller than a pixel row.");

        if (MemoryMarshal.TryGetArray(frame.Pixels, out var segment) && segment.Array is not null)
        {
            for (var row = 0; row < frame.Height; row++)
                Marshal.Copy(
                    segment.Array,
                    segment.Offset + row * frame.Stride,
                    destination + row * destinationRowBytes,
                    rowBytes);
            return;
        }

        var source = frame.Pixels.Span;
        for (var row = 0; row < frame.Height; row++)
            Marshal.Copy(
                source.Slice(row * frame.Stride, rowBytes).ToArray(),
                0,
                destination + row * destinationRowBytes,
                rowBytes);
    }

    public static Bitmap ToBitmap(ImageFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.PixelFormat != ImagePixelFormat.Bgra32)
            throw new NotSupportedException($"Pixel format '{frame.PixelFormat}' is not supported.");

        var bitmap = new WriteableBitmap(
            new PixelSize(frame.Width, frame.Height),
            new Vector(frame.DpiX, frame.DpiY),
            PixelFormat.Bgra8888,
            AlphaFormat.Opaque);

        using var locked = bitmap.Lock();
        CopyToFramebuffer(frame, locked.Address, locked.RowBytes);

        return bitmap;
    }

    public static ImageFrame ToImageFrame(Bitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);

        var pixelSize = bitmap.PixelSize;
        var dpiX = bitmap.Dpi.X > 0 ? bitmap.Dpi.X : 96d;
        var dpiY = bitmap.Dpi.Y > 0 ? bitmap.Dpi.Y : 96d;
        using var writeable = new WriteableBitmap(
            pixelSize,
            new Vector(dpiX, dpiY),
            PixelFormat.Bgra8888,
            AlphaFormat.Opaque);
        using var locked = writeable.Lock();
        bitmap.CopyPixels(locked);
        var pixels = new byte[checked(locked.RowBytes * pixelSize.Height)];
        for (var row = 0; row < pixelSize.Height; row++)
        {
            Marshal.Copy(
                locked.Address + row * locked.RowBytes,
                pixels,
                row * locked.RowBytes,
                locked.RowBytes);
        }

        return new ImageFrame(
            pixelSize.Width,
            pixelSize.Height,
            locked.RowBytes,
            dpiX,
            dpiY,
            pixels,
            ImagePixelFormat.Bgra32);
    }
}
