namespace MicroASR;

internal static class FbankExtractor
{
    public const int FilterCount = 80;
    public const int FrameLength = 400;
    public const int FrameShift = 160;

    private const int SampleRate = 16_000;
    private const int FftSize = 512;
    private static readonly double[][] Filters = CreateMelFilters();
    private static readonly double[] Window = CreatePoveyWindow(FrameLength);

    public static float[] Extract(float[] samples, int sampleRate, out int frameCount)
    {
        if (sampleRate != SampleRate)
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "Expected 16000 Hz audio.");

        frameCount = samples.Length < FrameLength ? 0 : 1 + (samples.Length - FrameLength) / FrameShift;
        var result = new float[frameCount * FilterCount];
        var random = new Random(0);
        var workspace = new FbankWorkspace();
        var frame = new float[FrameLength];
        var features = new float[FilterCount];

        for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
        {
            Array.Copy(samples, frameIndex * FrameShift, frame, 0, FrameLength);
            ExtractFrame(frame, features, random, workspace);
            Array.Copy(features, 0, result, frameIndex * FilterCount, FilterCount);
        }

        return result;
    }

    public static void ExtractFrame(
        ReadOnlySpan<float> samples,
        Span<float> destination,
        Random random,
        FbankWorkspace workspace)
    {
        if (samples.Length != FrameLength)
            throw new ArgumentException($"Expected {FrameLength} samples.", nameof(samples));
        if (destination.Length < FilterCount)
            throw new ArgumentException($"Expected at least {FilterCount} output values.", nameof(destination));

        double[] real = workspace.Real;
        double[] imaginary = workspace.Imaginary;
        double[] frame = workspace.Frame;
        Array.Clear(real);
        Array.Clear(imaginary);
        double mean = 0;
        for (int index = 0; index < FrameLength; index++)
        {
            frame[index] = samples[index] + NextGaussian(random);
            mean += frame[index];
        }

        mean /= FrameLength;
        for (int index = 0; index < FrameLength; index++)
            frame[index] -= mean;
        for (int index = FrameLength - 1; index >= 1; index--)
            frame[index] -= 0.97 * frame[index - 1];
        frame[0] *= 0.03;

        for (int index = 0; index < FrameLength; index++)
            real[index] = frame[index] * Window[index];
        Fft(real, imaginary);

        for (int filter = 0; filter < FilterCount; filter++)
        {
            double energy = 0;
            for (int bin = 0; bin <= FftSize / 2; bin++)
            {
                double power = real[bin] * real[bin] + imaginary[bin] * imaginary[bin];
                energy += power * Filters[filter][bin];
            }
            destination[filter] = (float)Math.Log(Math.Max(energy, float.Epsilon));
        }
    }

    internal sealed class FbankWorkspace
    {
        public double[] Real { get; } = new double[FftSize];
        public double[] Imaginary { get; } = new double[FftSize];
        public double[] Frame { get; } = new double[FrameLength];
    }

    private static double[] CreatePoveyWindow(int length)
    {
        var result = new double[length];
        for (int index = 0; index < length; index++)
        {
            double hann = 0.5 - 0.5 * Math.Cos(2 * Math.PI * index / (length - 1));
            result[index] = Math.Pow(hann, 0.85);
        }
        return result;
    }

    private static double[][] CreateMelFilters()
    {
        double Mel(double frequency) => 1127.0 * Math.Log(1.0 + frequency / 700.0);
        double low = Mel(20.0);
        double high = Mel(SampleRate / 2.0);
        var points = new double[FilterCount + 2];
        for (int index = 0; index < points.Length; index++)
            points[index] = low + (high - low) * index / (points.Length - 1);

        var result = new double[FilterCount][];
        for (int filter = 0; filter < FilterCount; filter++)
        {
            result[filter] = new double[FftSize / 2 + 1];
            double left = points[filter];
            double center = points[filter + 1];
            double right = points[filter + 2];
            for (int bin = 0; bin <= FftSize / 2; bin++)
            {
                double mel = Mel(bin * SampleRate / (double)FftSize);
                result[filter][bin] = Math.Max(0, Math.Min(
                    (mel - left) / (center - left),
                    (right - mel) / (right - center)));
            }
        }
        return result;
    }

    private static double NextGaussian(Random random)
    {
        double first = 1.0 - random.NextDouble();
        double second = 1.0 - random.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(first)) * Math.Cos(2.0 * Math.PI * second);
    }

    private static void Fft(double[] real, double[] imaginary)
    {
        int length = real.Length;
        for (int source = 1, target = 0; source < length; source++)
        {
            int bit = length >> 1;
            for (; (target & bit) != 0; bit >>= 1)
                target ^= bit;
            target ^= bit;
            if (source >= target)
                continue;
            (real[source], real[target]) = (real[target], real[source]);
            (imaginary[source], imaginary[target]) = (imaginary[target], imaginary[source]);
        }

        for (int size = 2; size <= length; size <<= 1)
        {
            double angle = -2.0 * Math.PI / size;
            double stepReal = Math.Cos(angle);
            double stepImaginary = Math.Sin(angle);
            for (int offset = 0; offset < length; offset += size)
            {
                double unitReal = 1;
                double unitImaginary = 0;
                for (int index = 0; index < size / 2; index++)
                {
                    int even = offset + index;
                    int odd = even + size / 2;
                    double oddReal = real[odd] * unitReal - imaginary[odd] * unitImaginary;
                    double oddImaginary = real[odd] * unitImaginary + imaginary[odd] * unitReal;
                    real[odd] = real[even] - oddReal;
                    imaginary[odd] = imaginary[even] - oddImaginary;
                    real[even] += oddReal;
                    imaginary[even] += oddImaginary;
                    (unitReal, unitImaginary) =
                        (unitReal * stepReal - unitImaginary * stepImaginary,
                         unitReal * stepImaginary + unitImaginary * stepReal);
                }
            }
        }
    }
}

