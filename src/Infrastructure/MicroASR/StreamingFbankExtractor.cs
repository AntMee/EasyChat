using System.Buffers.Binary;

namespace MicroASR;

public sealed class StreamingFbankExtractor
{
    public const int SampleRate = 16_000;
    public const int FeatureSize = FbankExtractor.FilterCount;

    private readonly List<float> _samples = new(FbankExtractor.FrameLength * 4);
    private readonly Random _random = new(0);
    private readonly FbankExtractor.FbankWorkspace _workspace = new();
    private readonly float[] _frame = new float[FbankExtractor.FrameLength];
    private readonly float[] _features = new float[FbankExtractor.FilterCount];
    private int _offset;

    public void AcceptPcm(ReadOnlySpan<byte> pcm, Action<float[]> featureAvailable)
    {
        ArgumentNullException.ThrowIfNull(featureAvailable);
        int sampleCount = pcm.Length / 2;
        for (int index = 0; index < sampleCount; index++)
            _samples.Add(BinaryPrimitives.ReadInt16LittleEndian(pcm.Slice(index * 2, 2)));

        while (_samples.Count - _offset >= FbankExtractor.FrameLength)
        {
            _samples.CopyTo(_offset, _frame, 0, _frame.Length);
            FbankExtractor.ExtractFrame(_frame, _features, _random, _workspace);
            featureAvailable(_features);
            _offset += FbankExtractor.FrameShift;
        }

        if (_offset >= FbankExtractor.FrameShift * 100)
        {
            _samples.RemoveRange(0, _offset);
            _offset = 0;
        }
    }

    public void Reset()
    {
        _samples.Clear();
        _offset = 0;
    }
}

