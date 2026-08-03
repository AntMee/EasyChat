using Microsoft.ML.OnnxRuntime;

namespace MicroASR;

public sealed class NeuralVoiceActivityDetector : IDisposable
{
    private static readonly string[] InputNames =
    [
        "features",
        "Constant908PastValue4768",
        "Constant908PastValue4771",
        "Constant1479PastValue4762",
        "Constant1479PastValue4765",
        "Constant2050PastValue4756",
        "Constant2050PastValue4759",
    ];

    private static readonly string[] OutputNames =
    [
        "PastValue4768_Output_0",
        "PastValue4771_Output_0",
        "PastValue4762_Output_0",
        "PastValue4765_Output_0",
        "PastValue4756_Output_0",
        "PastValue4759_Output_0",
        "Plus5184_Output_0_attach_noop_",
    ];

    private static readonly int[] StateWidths = [512, 512, 512, 512, 64, 64];

    private readonly InferenceSession _session;
    private readonly RunOptions _runOptions = new();
    private readonly float[] _context = new float[11 * FbankExtractor.FilterCount];
    private readonly float[][][] _stateBuffers = [CreateEmptyStates(), CreateEmptyStates()];
    private readonly OrtValue[][] _stateValues = [new OrtValue[6], new OrtValue[6]];
    private readonly OrtValue[] _inputValues = new OrtValue[7];
    private readonly OrtValue[] _outputValues = new OrtValue[7];
    private readonly float[] _scores = new float[3];
    private readonly OrtValue _featuresValue;
    private readonly OrtValue _scoresValue;
    private int _currentStateBank;
    private bool _disposed;

    public NeuralVoiceActivityDetector(string modelDirectory)
        : this(SpeechModelPackage.Load(modelDirectory))
    {
    }

    public NeuralVoiceActivityDetector(SpeechModelPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);

        using var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            IntraOpNumThreads = 1,
            InterOpNumThreads = 1,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
        };
        _session = new InferenceSession(package.VadPath, options);
        _featuresValue = OrtValue.CreateTensorValueFromMemory(_context, [1, _context.Length]);
        _scoresValue = OrtValue.CreateTensorValueFromMemory(_scores, [1, 3]);
        for (int bank = 0; bank < _stateValues.Length; bank++)
        {
            for (int state = 0; state < StateWidths.Length; state++)
            {
                _stateValues[bank][state] = OrtValue.CreateTensorValueFromMemory(
                    _stateBuffers[bank][state], [1, StateWidths[state]]);
            }
        }
    }

    public float AcceptFeature(ReadOnlySpan<float> feature)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (feature.Length != FbankExtractor.FilterCount)
            throw new ArgumentException("Expected one 80-bin feature frame.", nameof(feature));

        Array.Copy(_context, FbankExtractor.FilterCount, _context, 0,
            _context.Length - FbankExtractor.FilterCount);
        feature.CopyTo(_context.AsSpan(_context.Length - FbankExtractor.FilterCount));

        int nextStateBank = 1 - _currentStateBank;
        _inputValues[0] = _featuresValue;
        for (int state = 0; state < StateWidths.Length; state++)
        {
            _inputValues[state + 1] = _stateValues[_currentStateBank][state];
            _outputValues[state] = _stateValues[nextStateBank][state];
        }
        _outputValues[^1] = _scoresValue;
        _session.Run(_runOptions, InputNames, _inputValues, OutputNames, _outputValues);
        _currentStateBank = nextStateBank;

        float maximum = Math.Max(_scores[0], Math.Max(_scores[1], _scores[2]));
        double silence = Math.Exp(_scores[0] - maximum);
        double speech = Math.Exp(_scores[1] - maximum);
        double otherVoice = Math.Exp(_scores[2] - maximum);
        return (float)((speech + otherVoice) / (silence + speech + otherVoice));
    }

    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Array.Clear(_context);
        foreach (float[][] bank in _stateBuffers)
        {
            foreach (float[] state in bank)
                Array.Clear(state);
        }
        Array.Clear(_scores);
        _currentStateBank = 0;
    }

    private static float[][] CreateEmptyStates() =>
    [
        new float[512],
        new float[512],
        new float[512],
        new float[512],
        new float[64],
        new float[64],
    ];

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _featuresValue.Dispose();
        _scoresValue.Dispose();
        foreach (OrtValue[] bank in _stateValues)
        {
            foreach (OrtValue state in bank)
                state.Dispose();
        }
        _runOptions.Dispose();
        _session.Dispose();
    }
}

