using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text;
using EasyChat.Contracts.ImageTranslation;
using EasyChat.Contracts.Ocr;
using EasyChat.Contracts.Platform;
using EasyChat.Infrastructure.Windows.Workers;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using OpenCvSharp.XPhoto;

namespace EasyChat.Infrastructure.Windows.ImageTranslation;

[SupportedOSPlatform("windows")]
internal static class WindowsImageBackgroundCleanerWorkerClient
{
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ProcessingTimeout = TimeSpan.FromMinutes(2);

    internal static ImageFrame RemoveText(
        ImageFrame source,
        IReadOnlyList<OcrTextRegion> regions,
        ImageTextEraseMode mode,
        string modelDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(regions);
        cancellationToken.ThrowIfCancellationRequested();

        var pipeName = "EasyChat.ImageCleaner." + Guid.NewGuid().ToString("N");
        using var process = WindowsWorkerProcess.Start("--image-cleaner-worker", pipeName);
        using var cancellationRegistration = cancellationToken.Register(
            static state => WindowsWorkerProcess.TryTerminate((Process)state!),
            process);
        using var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        try
        {
            pipe.ConnectAsync(cancellationToken)
                .WaitAsync(ConnectionTimeout, cancellationToken)
                .GetAwaiter()
                .GetResult();
            using var reader = new BinaryReader(pipe, Encoding.UTF8, leaveOpen: true);
            using var writer = new BinaryWriter(pipe, Encoding.UTF8, leaveOpen: true);
            ImageCleanerWorkerProtocol.WriteRequest(
                writer,
                new ImageCleanerWorkerRequest(source, regions, mode, modelDirectory));
            var response = Task.Run(() => ImageCleanerWorkerProtocol.ReadResponse(reader))
                .WaitAsync(ProcessingTimeout, cancellationToken)
                .GetAwaiter()
                .GetResult();
            if (response.Status != ImageCleanerWorkerStatus.Success || response.Image is null)
                throw new InvalidOperationException(
                    $"Image cleaner worker failed: {response.ErrorMessage}");
            return response.Image;
        }
        catch (OperationCanceledException)
        {
            WindowsWorkerProcess.TryTerminate(process);
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
        catch (TimeoutException exception)
        {
            WindowsWorkerProcess.TryTerminate(process);
            throw new TimeoutException("Image cleaner worker did not complete in time.", exception);
        }
        finally
        {
            pipe.Dispose();
            if (!WindowsWorkerProcess.TryWaitForExit(process, milliseconds: 5000))
                WindowsWorkerProcess.TryTerminate(process);
        }
    }
}

[SupportedOSPlatform("windows")]
public static class WindowsImageBackgroundCleanerWorker
{
    public static void Run(string pipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.None);
        server.WaitForConnection();
        using var reader = new BinaryReader(server, Encoding.UTF8, leaveOpen: true);
        var writer = new BinaryWriter(server, Encoding.UTF8, leaveOpen: true);

        ImageCleanerWorkerResponse response;
        try
        {
            var request = ImageCleanerWorkerProtocol.ReadRequest(reader);
            response = ImageCleanerWorkerResponse.Success(
                WindowsOpenCvImageBackgroundCleaner.RemoveText(
                    request.Source,
                    request.Regions,
                    request.Mode,
                    request.ModelDirectory));
        }
        catch (Exception exception)
        {
            response = ImageCleanerWorkerResponse.Failure(exception.Message);
        }

        try
        {
            ImageCleanerWorkerProtocol.WriteResponse(writer, response);
        }
        catch (IOException)
        {
            // Parent process exited or canceled processing.
        }
        finally
        {
            try
            {
                writer.Dispose();
            }
            catch (IOException)
            {
                // The parent disconnected before the final flush.
            }
            catch (ObjectDisposedException)
            {
                // The parent disconnected before the final flush.
            }
        }
    }
}

internal static class WindowsOpenCvImageBackgroundCleaner
{
    private const int AotGanInputSize = 512;
    private const double TexturedBackgroundEdgeDensity = 0.035;
    private const int FsrBestMaximumPixels = 4_096;
    private const int FsrFastMaximumPixels = 65_536;

    internal static ImageFrame RemoveText(
        ImageFrame source,
        IReadOnlyList<OcrTextRegion> regions,
        ImageTextEraseMode mode,
        string modelDirectory)
    {
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

        using var inpainted = mode switch
        {
            ImageTextEraseMode.Fast => InpaintFast(bgr, mask, medianHeight),
            ImageTextEraseMode.Precise => InpaintAotGan(bgr, mask, modelDirectory, medianHeight),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown image erase mode.")
        };

        var stride = checked(source.Width * 4);
        var outputPixels = sourcePixels.ToArray();
        for (var row = 0; row < source.Height; row++)
        for (var column = 0; column < source.Width; column++)
        {
            if (mask.At<byte>(row, column) == 0)
                continue;

            var pixel = inpainted.At<Vec3b>(row, column);
            var offset = row * stride + column * 4;
            outputPixels[offset] = pixel.Item0;
            outputPixels[offset + 1] = pixel.Item1;
            outputPixels[offset + 2] = pixel.Item2;
        }

        return new ImageFrame(
            source.Width,
            source.Height,
            stride,
            source.DpiX,
            source.DpiY,
            outputPixels);
    }

    private static Mat InpaintFast(Mat bgr, Mat mask, double medianHeight)
    {
        var inpainted = bgr.Clone();
        var contextPadding = Math.Clamp((int)Math.Round(medianHeight * 1.5), 18, 64);
        foreach (var region in GetInpaintRegions(mask, contextPadding))
        {
            using var sourceRegion = new Mat(bgr, region);
            using var maskedRegion = new Mat(mask, region);
            using var reconstructed = new Mat();
            var strategy = SelectFastInpaintStrategy(
                sourceRegion,
                maskedRegion,
                region.Width * region.Height);

            switch (strategy)
            {
                case FastInpaintStrategy.Telea:
                    Cv2.Inpaint(
                        sourceRegion,
                        maskedRegion,
                        reconstructed,
                        Math.Max(3, medianHeight / 10d),
                        InpaintMethod.Telea);
                    break;

                case FastInpaintStrategy.FsrFast:
                    InpaintFsr(
                        sourceRegion,
                        maskedRegion,
                        reconstructed,
                        InpaintTypes.FSR_FAST,
                        FsrFastMaximumPixels);
                    break;

                case FastInpaintStrategy.FsrBest:
                    InpaintFsr(
                        sourceRegion,
                        maskedRegion,
                        reconstructed,
                        InpaintTypes.FSR_BEST,
                        FsrBestMaximumPixels);
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(strategy), strategy, "Unknown fast inpaint strategy.");
            }

            using var outputRegion = new Mat(inpainted, region);
            reconstructed.CopyTo(outputRegion, maskedRegion);
        }

        return inpainted;
    }

    private static void InpaintFsr(
        Mat source,
        Mat erasedMask,
        Mat destination,
        InpaintTypes inpaintType,
        int maximumPixels)
    {
        if (source.Rows * source.Cols <= maximumPixels)
        {
            InpaintFsrAtNativeSize(source, erasedMask, destination, inpaintType);
            return;
        }

        var scale = Math.Sqrt(maximumPixels / (double)(source.Rows * source.Cols));
        var scaledSize = new Size(
            Math.Max(1, (int)Math.Round(source.Width * scale)),
            Math.Max(1, (int)Math.Round(source.Height * scale)));
        using var scaledSource = new Mat();
        using var scaledMask = new Mat();
        using var scaledResult = new Mat();
        Cv2.Resize(source, scaledSource, scaledSize, 0, 0, InterpolationFlags.Area);
        Cv2.Resize(erasedMask, scaledMask, scaledSize, 0, 0, InterpolationFlags.Nearest);
        InpaintFsrAtNativeSize(scaledSource, scaledMask, scaledResult, inpaintType);
        Cv2.Resize(scaledResult, destination, source.Size(), 0, 0, InterpolationFlags.Linear);
    }

    private static void InpaintFsrAtNativeSize(
        Mat source,
        Mat erasedMask,
        Mat destination,
        InpaintTypes inpaintType)
    {
        // xphoto uses the inverse convention: non-zero pixels are valid.
        using var validRegion = new Mat();
        Cv2.BitwiseNot(erasedMask, validRegion);
        CvXPhoto.Inpaint(source, validRegion, destination, inpaintType);
    }

    internal static FastInpaintStrategy SelectFastInpaintStrategy(
        Mat source,
        Mat erasedMask,
        int pixelCount)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(erasedMask);

        using var gray = new Mat();
        Cv2.CvtColor(source, gray, ColorConversionCodes.BGR2GRAY);
        using var edges = new Mat();
        Cv2.Canny(gray, edges, 48, 144);
        using var valid = new Mat();
        Cv2.BitwiseNot(erasedMask, valid);
        Cv2.BitwiseAnd(edges, valid, edges);
        var validPixels = Math.Max(1, Cv2.CountNonZero(valid));
        var edgeDensity = Cv2.CountNonZero(edges) / (double)validPixels;
        if (edgeDensity < TexturedBackgroundEdgeDensity)
            return FastInpaintStrategy.Telea;

        return pixelCount <= FsrBestMaximumPixels
            ? FastInpaintStrategy.FsrBest
            : FastInpaintStrategy.FsrFast;
    }

    private static IReadOnlyList<Rect> GetInpaintRegions(Mat erasedMask, int contextPadding)
    {
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        var labelCount = Cv2.ConnectedComponentsWithStats(
            erasedMask,
            labels,
            stats,
            centroids,
            PixelConnectivity.Connectivity8,
            MatType.CV_32S);
        var regions = new List<Rect>(Math.Max(0, labelCount - 1));
        for (var label = 1; label < labelCount; label++)
        {
            var bounds = new Rect(
                stats.At<int>(label, (int)ConnectedComponentsTypes.Left),
                stats.At<int>(label, (int)ConnectedComponentsTypes.Top),
                stats.At<int>(label, (int)ConnectedComponentsTypes.Width),
                stats.At<int>(label, (int)ConnectedComponentsTypes.Height));
            regions.Add(ExpandAndClamp(bounds, contextPadding, erasedMask.Size()));
        }

        return MergeOverlappingRegions(regions);
    }

    private static IReadOnlyList<Rect> MergeOverlappingRegions(IReadOnlyList<Rect> regions)
    {
        var merged = new List<Rect>(regions.Count);
        foreach (var region in regions)
        {
            var candidate = region;
            var mergedAny = true;
            while (mergedAny)
            {
                mergedAny = false;
                for (var index = merged.Count - 1; index >= 0; index--)
                {
                    if (!Intersects(candidate, merged[index]))
                        continue;

                    candidate = Union(candidate, merged[index]);
                    merged.RemoveAt(index);
                    mergedAny = true;
                }
            }

            merged.Add(candidate);
        }

        return merged;
    }

    private static Rect ExpandAndClamp(Rect bounds, int padding, Size imageSize)
    {
        var left = Math.Max(0, bounds.X - padding);
        var top = Math.Max(0, bounds.Y - padding);
        var right = Math.Min(imageSize.Width, bounds.X + bounds.Width + padding);
        var bottom = Math.Min(imageSize.Height, bounds.Y + bounds.Height + padding);
        return new Rect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }

    private static bool Intersects(Rect left, Rect right) =>
        left.X <= right.X + right.Width
        && right.X <= left.X + left.Width
        && left.Y <= right.Y + right.Height
        && right.Y <= left.Y + left.Height;

    private static Rect Union(Rect left, Rect right)
    {
        var x = Math.Min(left.X, right.X);
        var y = Math.Min(left.Y, right.Y);
        var maxX = Math.Max(left.X + left.Width, right.X + right.Width);
        var maxY = Math.Max(left.Y + left.Height, right.Y + right.Height);
        return new Rect(x, y, maxX - x, maxY - y);
    }

    private static Mat InpaintAotGan(Mat bgr, Mat mask, string modelDirectory, double medianHeight)
    {
        var modelPath = WindowsImageTranslationModelStore.ResolveModelPath(modelDirectory);
        if (!WindowsImageTranslationModelStore.AreModelFilesInstalled(modelDirectory))
            throw new FileNotFoundException("AOT-GAN model is not installed.", modelPath);

        using var sessionOptions = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL
        };
        using var session = new InferenceSession(modelPath, sessionOptions);
        var inpainted = bgr.Clone();
        var contextPadding = Math.Clamp((int)Math.Round(medianHeight * 4), 64, 256);
        foreach (var region in GetInpaintRegions(mask, contextPadding))
        {
            using var sourceRegion = new Mat(bgr, region);
            using var maskedRegion = new Mat(mask, region);
            var side = Math.Max(sourceRegion.Width, sourceRegion.Height);
            var left = (side - sourceRegion.Width) / 2;
            var top = (side - sourceRegion.Height) / 2;
            using var squareImage = new Mat();
            using var squareMask = new Mat();
            Cv2.CopyMakeBorder(
                sourceRegion,
                squareImage,
                top,
                side - sourceRegion.Height - top,
                left,
                side - sourceRegion.Width - left,
                BorderTypes.Reflect101);
            Cv2.CopyMakeBorder(
                maskedRegion,
                squareMask,
                top,
                side - sourceRegion.Height - top,
                left,
                side - sourceRegion.Width - left,
                BorderTypes.Constant,
                Scalar.All(0));
            using var inputImage = new Mat();
            using var inputMask = new Mat();
            Cv2.Resize(squareImage, inputImage, new Size(AotGanInputSize, AotGanInputSize));
            Cv2.Resize(squareMask, inputMask, new Size(AotGanInputSize, AotGanInputSize), 0, 0, InterpolationFlags.Nearest);
            var imageTensor = CreateImageTensor(inputImage);
            var maskTensor = CreateMaskTensor(inputMask);
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("image", imageTensor),
                NamedOnnxValue.CreateFromTensor("mask", maskTensor)
            };
            using var outputs = session.Run(inputs);
            var outputTensor = outputs.FirstOrDefault(output => output.Name == "painted_image")?.AsTensor<float>()
                               ?? throw new InvalidDataException("AOT-GAN output 'painted_image' was not returned.");
            using var predicted = ConvertAotGanOutput(outputTensor);
            using var scaled = new Mat();
            Cv2.Resize(predicted, scaled, new Size(side, side));
            using var cropped = new Mat(scaled, new Rect(left, top, sourceRegion.Width, sourceRegion.Height));
            using var outputRegion = new Mat(inpainted, region);
            cropped.CopyTo(outputRegion, maskedRegion);
        }

        return inpainted;
    }

    private static DenseTensor<float> CreateImageTensor(Mat image)
    {
        var tensor = new DenseTensor<float>([1, 3, AotGanInputSize, AotGanInputSize]);
        for (var y = 0; y < AotGanInputSize; y++)
        for (var x = 0; x < AotGanInputSize; x++)
        {
            var pixel = image.At<Vec3b>(y, x);
            tensor[0, 0, y, x] = pixel.Item2 / 255f;
            tensor[0, 1, y, x] = pixel.Item1 / 255f;
            tensor[0, 2, y, x] = pixel.Item0 / 255f;
        }

        return tensor;
    }

    private static DenseTensor<float> CreateMaskTensor(Mat mask)
    {
        var tensor = new DenseTensor<float>([1, 1, AotGanInputSize, AotGanInputSize]);
        for (var y = 0; y < AotGanInputSize; y++)
        for (var x = 0; x < AotGanInputSize; x++)
            tensor[0, 0, y, x] = mask.At<byte>(y, x) / 255f;
        return tensor;
    }

    private static Mat ConvertAotGanOutput(Tensor<float> output)
    {
        if (output.Rank != 4
            || output.Dimensions[0] < 1
            || output.Dimensions[1] < 3
            || output.Dimensions[2] != AotGanInputSize
            || output.Dimensions[3] != AotGanInputSize)
        {
            throw new InvalidDataException("AOT-GAN output shape is invalid.");
        }

        var result = new Mat(AotGanInputSize, AotGanInputSize, MatType.CV_8UC3);
        for (var y = 0; y < AotGanInputSize; y++)
        {
            for (var x = 0; x < AotGanInputSize; x++)
            {
                var blue = Math.Clamp(output[0, 2, y, x] * 255f, 0, 255);
                var green = Math.Clamp(output[0, 1, y, x] * 255f, 0, 255);
                var red = Math.Clamp(output[0, 0, y, x] * 255f, 0, 255);
                result.Set(y, x, new Vec3b((byte)blue, (byte)green, (byte)red));
            }
        }

        return result;
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

internal enum FastInpaintStrategy
{
    Telea,
    FsrFast,
    FsrBest
}

internal static class ImageCleanerWorkerProtocol
{
    private const int Magic = 0x4D494345;
    private const int Version = 2;
    private const int MaxRegionCount = 100_000;
    private const int MaxPolygonPointCount = 10_000;

    internal static void WriteRequest(BinaryWriter writer, ImageCleanerWorkerRequest request)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(request);
        writer.Write(Magic);
        writer.Write(Version);
        WindowsImageFrameProtocol.Write(writer, request.Source);
        writer.Write((int)request.Mode);
        writer.Write(request.ModelDirectory ?? string.Empty);
        writer.Write(request.Regions.Count);
        foreach (var region in request.Regions)
        {
            writer.Write(region.Polygon.Count);
            foreach (var point in region.Polygon)
            {
                writer.Write(point.X);
                writer.Write(point.Y);
            }
        }
        writer.Flush();
    }

    internal static ImageCleanerWorkerRequest ReadRequest(BinaryReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        EnsureHeader(reader);
        var source = WindowsImageFrameProtocol.Read(reader);
        var mode = (ImageTextEraseMode)reader.ReadInt32();
        if (!Enum.IsDefined(mode))
            throw new InvalidDataException("Image cleaner erase mode is invalid.");
        var modelDirectory = reader.ReadString();
        if (modelDirectory.Length > 32_768)
            throw new InvalidDataException("Image cleaner model directory is invalid.");
        var regionCount = reader.ReadInt32();
        if (regionCount < 0 || regionCount > MaxRegionCount)
            throw new InvalidDataException("Image cleaner region count is invalid.");
        var regions = new OcrTextRegion[regionCount];
        for (var regionIndex = 0; regionIndex < regionCount; regionIndex++)
        {
            var pointCount = reader.ReadInt32();
            if (pointCount < 0 || pointCount > MaxPolygonPointCount)
                throw new InvalidDataException("Image cleaner polygon point count is invalid.");
            var points = new ImagePoint[pointCount];
            for (var pointIndex = 0; pointIndex < pointCount; pointIndex++)
                points[pointIndex] = new ImagePoint(reader.ReadDouble(), reader.ReadDouble());
            regions[regionIndex] = new OcrTextRegion(string.Empty, points, 0);
        }
        return new ImageCleanerWorkerRequest(source, regions, mode, modelDirectory);
    }

    internal static void WriteResponse(BinaryWriter writer, ImageCleanerWorkerResponse response)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(response);
        writer.Write(Magic);
        writer.Write(Version);
        writer.Write((byte)response.Status);
        if (response.Status == ImageCleanerWorkerStatus.Success)
            WindowsImageFrameProtocol.Write(writer, response.Image!);
        else
            writer.Write(response.ErrorMessage ?? string.Empty);
        writer.Flush();
    }

    internal static ImageCleanerWorkerResponse ReadResponse(BinaryReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        EnsureHeader(reader);
        var status = (ImageCleanerWorkerStatus)reader.ReadByte();
        if (!Enum.IsDefined(status))
            throw new InvalidDataException("Image cleaner response status is invalid.");
        return status == ImageCleanerWorkerStatus.Success
            ? ImageCleanerWorkerResponse.Success(WindowsImageFrameProtocol.Read(reader))
            : ImageCleanerWorkerResponse.Failure(reader.ReadString());
    }

    private static void EnsureHeader(BinaryReader reader)
    {
        if (reader.ReadInt32() != Magic || reader.ReadInt32() != Version)
            throw new InvalidDataException("Image cleaner worker protocol header is invalid.");
    }
}

internal sealed record ImageCleanerWorkerRequest(
    ImageFrame Source,
    IReadOnlyList<OcrTextRegion> Regions,
    ImageTextEraseMode Mode = ImageTextEraseMode.Fast,
    string ModelDirectory = "");

internal enum ImageCleanerWorkerStatus : byte
{
    Success = 0,
    Failed = 1
}

internal sealed record ImageCleanerWorkerResponse(
    ImageCleanerWorkerStatus Status,
    ImageFrame? Image,
    string? ErrorMessage)
{
    internal static ImageCleanerWorkerResponse Success(ImageFrame image) =>
        new(ImageCleanerWorkerStatus.Success, image, null);

    internal static ImageCleanerWorkerResponse Failure(string errorMessage) =>
        new(ImageCleanerWorkerStatus.Failed, null, errorMessage);
}
