using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using EasyChat.Contracts.ImageTranslation;
using EasyChat.Contracts.Ocr;
using EasyChat.Contracts.Platform;
using OpenCvSharp;

namespace EasyChat.Infrastructure.Windows.ImageTranslation;

[SupportedOSPlatform("windows")]
public sealed class WindowsImageBackgroundCleaner : IImageBackgroundCleaner
{
    public ImageFrame RemoveText(
        ImageFrame source,
        IReadOnlyList<OcrTextRegion> regions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(regions);
        cancellationToken.ThrowIfCancellationRequested();

        if (source.PixelFormat != ImagePixelFormat.Bgra32)
            throw new NotSupportedException($"Pixel format '{source.PixelFormat}' is not supported.");
        if (regions.Count == 0)
            return source;

        var sourcePixels = source.Pixels.ToArray();
        using var bgra = Mat.FromPixelData(
            source.Height,
            source.Width,
            MatType.CV_8UC4,
            sourcePixels,
            source.Stride);
        using var bgr = new Mat();
        Cv2.CvtColor(bgra, bgr, ColorConversionCodes.BGRA2BGR);
        GC.KeepAlive(sourcePixels);

        using var mask = new Mat(bgr.Size(), MatType.CV_8UC1, Scalar.All(0));
        foreach (var region in regions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var polygon = region.Polygon
                .Select(point => new Point(
                    (int)Math.Round(point.X),
                    (int)Math.Round(point.Y)))
                .ToArray();
            if (polygon.Length >= 3)
                Cv2.FillPoly(mask, [polygon], Scalar.All(255));
        }

        var heights = regions
            .Select(GetHeight)
            .OrderBy(value => value)
            .ToArray();
        var medianHeight = heights[heights.Length / 2];
        var kernelSize = Math.Max(3, (int)Math.Round(medianHeight / 12d) * 2 + 1);
        using var kernel = Cv2.GetStructuringElement(
            MorphShapes.Ellipse,
            new Size(kernelSize, kernelSize));
        Cv2.Dilate(mask, mask, kernel);

        using var inpainted = new Mat();
        Cv2.Inpaint(
            bgr,
            mask,
            inpainted,
            Math.Max(3, medianHeight / 10d),
            InpaintMethod.Telea);
        cancellationToken.ThrowIfCancellationRequested();

        using var output = new Mat();
        Cv2.CvtColor(inpainted, output, ColorConversionCodes.BGR2BGRA);
        var stride = checked(source.Width * 4);
        var outputPixels = new byte[checked(stride * source.Height)];
        for (var row = 0; row < source.Height; row++)
        {
            Marshal.Copy(output.Data + row * (int)output.Step(), outputPixels, row * stride, stride);
        }

        return new ImageFrame(
            source.Width,
            source.Height,
            stride,
            source.DpiX,
            source.DpiY,
            outputPixels);
    }

    private static double GetHeight(OcrTextRegion region)
    {
        if (region.Polygon.Count == 0)
            return 0;

        var top = region.Polygon.Min(point => point.Y);
        var bottom = region.Polygon.Max(point => point.Y);
        return Math.Max(0, bottom - top);
    }
}
