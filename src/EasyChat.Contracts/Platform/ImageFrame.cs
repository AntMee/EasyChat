namespace EasyChat.Contracts.Platform;

public enum ImagePixelFormat
{
    Bgra32
}

public sealed class ImageFrame
{
    public ImageFrame(
        int width,
        int height,
        int stride,
        double dpiX,
        double dpiY,
        ReadOnlyMemory<byte> pixels,
        ImagePixelFormat pixelFormat = ImagePixelFormat.Bgra32)
    {
        if (width <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0)
            throw new ArgumentOutOfRangeException(nameof(height));
        if (stride < checked(width * 4))
            throw new ArgumentOutOfRangeException(nameof(stride));
        if (dpiX <= 0)
            throw new ArgumentOutOfRangeException(nameof(dpiX));
        if (dpiY <= 0)
            throw new ArgumentOutOfRangeException(nameof(dpiY));
        if (pixels.Length < checked(stride * height))
            throw new ArgumentException("The pixel buffer is smaller than the declared image frame.", nameof(pixels));

        Width = width;
        Height = height;
        Stride = stride;
        DpiX = dpiX;
        DpiY = dpiY;
        Pixels = pixels;
        PixelFormat = pixelFormat;
    }

    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public double DpiX { get; }
    public double DpiY { get; }
    public ReadOnlyMemory<byte> Pixels { get; }
    public ImagePixelFormat PixelFormat { get; }
}
