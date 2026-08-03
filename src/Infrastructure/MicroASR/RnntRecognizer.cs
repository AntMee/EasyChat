using Microsoft.ML.OnnxRuntime;

namespace MicroASR;

public sealed record RecognitionWord(string Text, float Confidence);

public sealed record RnntResult(string Text, float Confidence, IReadOnlyList<RecognitionWord> Words)
{
    public static readonly RnntResult Empty = new(string.Empty, 0, Array.Empty<RecognitionWord>());
}

public readonly record struct RnntSearchStatistics(
    int Frames,
    int MinimumBeamFrames,
    int MediumBeamFrames,
    int MaximumBeamFrames,
    long JointRuns,
    long DecoderRuns);

public sealed class RnntRecognizer : IDisposable
{
    private const int EncoderLayers = 18;
    private const int AttentionHeads = 8;
    private const int AttentionWidth = 64;
    private const int EncoderWidth = 512;
    private const int DecoderStateSize = 2 * 1024;
    private const int InitialEncoderChunkFrames = 20;
    private const int StandardEncoderChunkFrames = 40;
    private const int EncoderMaximumHistory = 40;
    private readonly RnntDecoderOptions _options;
    private readonly InferenceSession _encoder;
    private readonly InferenceSession _decoder;
    private readonly InferenceSession _joint;
    private readonly string[] _tokens;
    private readonly int _blank;
    private readonly int _eos;
    private readonly int _predictorWidth;
    private readonly int _languageWidth;
    private readonly int _languageIndex;
    private readonly bool _usesCnnCache;
    private static readonly string[] JointInputNames = ["enc_hidden", "dec_hidden"];
    private static readonly string[] JointOutputNames = ["joint_output"];
    private readonly string[] _encoderInputNames;
    private readonly string[] _encoderOutputNames;
    private static readonly string[] DecoderInputNames = ["prev_label", "h0", "c0"];
    private static readonly string[] DecoderOutputNames = ["hidden", "h1", "c1"];

    private readonly List<float> _pendingFeatures = new(StandardEncoderChunkFrames * FbankExtractor.FilterCount);
    private readonly RunOptions _encoderRunOptions = new();
    private readonly Dictionary<int, EncoderFeatureBuffers> _encoderFeatureBuffers = [];
    private readonly Dictionary<int, AttentionCacheBuffers> _attentionCacheBuffers = [];
    private readonly OrtValue[] _encoderInputValues;
    private readonly OrtValue[] _encoderOutputValues;
    private readonly float[][]? _cnnCacheBuffers;
    private readonly OrtValue[]? _cnnCacheValues;
    private int _cnnCacheBank;
    private readonly RunOptions _decoderRunOptions = new();
    private readonly long[] _decoderToken = new long[1];
    private readonly OrtValue _decoderTokenValue;
    private readonly OrtValue[] _decoderInputValues = new OrtValue[3];
    private readonly OrtValue[] _decoderOutputValues = new OrtValue[3];
    private readonly Stack<DecoderState> _decoderStatePool = new();
    private readonly List<DecoderState> _decoderStates = [];
    private readonly RunOptions _jointRunOptions = new();
    private readonly float[] _jointAcoustic = new float[EncoderWidth];
    private readonly float[] _jointPredictor;
    private readonly float[] _jointLogits;
    private readonly OrtValue _jointAcousticValue;
    private readonly OrtValue _jointPredictorValue;
    private readonly OrtValue _jointLogitsValue;
    private readonly OrtValue[] _jointInputValues = new OrtValue[2];
    private readonly OrtValue[] _jointOutputValues = new OrtValue[1];

    private readonly float[] _featureCache = new float[3 * FbankExtractor.FilterCount];
    private int _encoderHistory;
    private int _decodedFrames;
    private int _narrowBeamFrames;
    private int _mediumBeamFrames;
    private int _fullBeamFrames;
    private long _jointRuns;
    private long _decoderRuns;
    private bool _lowLatencyChunking = true;
    private List<Hypothesis> _beam = [];
    private bool _disposed;

    public RnntRecognizer(string modelDirectory)
        : this(SpeechModelPackage.Load(modelDirectory), RnntDecoderOptions.ForMode(RnntRecognitionMode.Accuracy))
    {
    }

    public RnntRecognizer(string modelDirectory, RnntRecognitionMode mode)
        : this(SpeechModelPackage.Load(modelDirectory), RnntDecoderOptions.ForMode(mode))
    {
    }

    public RnntRecognizer(string modelDirectory, RnntDecoderOptions options)
        : this(SpeechModelPackage.Load(modelDirectory), options)
    {
    }

    public RnntRecognizer(SpeechModelPackage package)
        : this(package, RnntDecoderOptions.ForMode(RnntRecognitionMode.Accuracy))
    {
    }

    public RnntRecognizer(SpeechModelPackage package, RnntRecognitionMode mode)
        : this(package, RnntDecoderOptions.ForMode(mode))
    {
    }

    public RnntRecognizer(SpeechModelPackage package, RnntDecoderOptions options)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;

        using var sessionOptions = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 4, 1, 2),
            InterOpNumThreads = 1,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
        };
        _encoder = new InferenceSession(package.EncoderPath, sessionOptions);
        _decoder = new InferenceSession(package.PredictorPath, sessionOptions);
        _joint = new InferenceSession(package.JointPath, sessionOptions);
        _predictorWidth = GetRequiredWidth(_decoder.OutputMetadata, "hidden");
        int tokenRank = _decoder.InputMetadata["prev_label"].Dimensions.Length;
        if (tokenRank is not 1 and not 2)
            throw new NotSupportedException($"Unsupported prev_label rank: {tokenRank}.");

        _usesCnnCache = _encoder.InputMetadata.ContainsKey("inp_cache_cnn");
        bool usesLanguageId = _encoder.InputMetadata.ContainsKey("lang_id");
        _languageWidth = usesLanguageId
            ? GetRequiredWidth(_encoder.InputMetadata, "lang_id")
            : 0;
        _languageIndex = package.LanguageIndex;
        if (_languageWidth > 0 && (package.LanguageCount != _languageWidth || _languageIndex >= _languageWidth))
            throw new InvalidDataException("The configured language candidates do not match the encoder input.");

        var encoderInputs = new List<string> { "cache_frames", "inp_cache_k", "inp_cache_v" };
        var encoderOutputs = new List<string> { "oup_cache_frames", "hidden_state", "oup_cache_k", "oup_cache_v" };
        if (_usesCnnCache)
        {
            encoderInputs.Add("inp_cache_cnn");
            encoderOutputs.Add("oup_cache_cnn");
            _cnnCacheBuffers = [new float[EncoderLayers * EncoderWidth * 2], new float[EncoderLayers * EncoderWidth * 2]];
            _cnnCacheValues =
            [
                OrtValue.CreateTensorValueFromMemory(_cnnCacheBuffers[0], [EncoderLayers, 1, EncoderWidth, 2]),
                OrtValue.CreateTensorValueFromMemory(_cnnCacheBuffers[1], [EncoderLayers, 1, EncoderWidth, 2]),
            ];
        }
        if (usesLanguageId)
            encoderInputs.Add("lang_id");
        _encoderInputNames = encoderInputs.ToArray();
        _encoderOutputNames = encoderOutputs.ToArray();
        _encoderInputValues = new OrtValue[_encoderInputNames.Length];
        _encoderOutputValues = new OrtValue[_encoderOutputNames.Length];

        _tokens = File.ReadAllLines(package.TokensPath).Select(line => line.Split('\t', 2)[0]).ToArray();
        _blank = Array.IndexOf(_tokens, "<blank>");
        _eos = Array.FindIndex(_tokens,
            token => string.Equals(token, "<EOS>", StringComparison.OrdinalIgnoreCase));
        if (_blank < 0)
            throw new InvalidDataException("tokens.list does not contain <blank>.");
        int vocabularySize = GetRequiredWidth(_joint.OutputMetadata, "joint_output");
        if (vocabularySize != _tokens.Length)
            throw new InvalidDataException(
                $"Token count {_tokens.Length} does not match joint vocabulary size {vocabularySize}.");

        _jointLogits = new float[_tokens.Length];
        _jointPredictor = new float[_predictorWidth];
        _jointAcousticValue = OrtValue.CreateTensorValueFromMemory(_jointAcoustic, [1, EncoderWidth]);
        _jointPredictorValue = OrtValue.CreateTensorValueFromMemory(_jointPredictor, [1, _predictorWidth]);
        _jointLogitsValue = OrtValue.CreateTensorValueFromMemory(_jointLogits, [_tokens.Length]);
        _jointInputValues[0] = _jointAcousticValue;
        _jointInputValues[1] = _jointPredictorValue;
        _jointOutputValues[0] = _jointLogitsValue;
        _decoderTokenValue = OrtValue.CreateTensorValueFromMemory(
            _decoderToken, tokenRank == 1 ? [1] : [1, 1]);
        _decoderInputValues[0] = _decoderTokenValue;
        GetAttentionCacheBuffers(0);
        Reset();
    }

    public RnntResult CurrentResult => BuildResult(_beam.Count == 0 ? null : _beam[0]);

    public RnntSearchStatistics SearchStatistics => new(
        _decodedFrames,
        _narrowBeamFrames,
        _mediumBeamFrames,
        _fullBeamFrames,
        _jointRuns,
        _decoderRuns);

    public bool AcceptFeature(ReadOnlySpan<float> feature)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (feature.Length != FbankExtractor.FilterCount)
            throw new ArgumentException("Expected one 80-bin feature frame.", nameof(feature));

        for (int index = 0; index < feature.Length; index++)
            _pendingFeatures.Add(feature[index]);

        int chunkFrames = _lowLatencyChunking
            ? InitialEncoderChunkFrames
            : StandardEncoderChunkFrames;
        if (_pendingFeatures.Count < chunkFrames * FbankExtractor.FilterCount)
            return false;

        RunEncoderChunk(chunkFrames);
        return true;
    }

    public void UseStandardChunking()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _lowLatencyChunking = false;
    }

    public bool Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int frameCount = _pendingFeatures.Count / FbankExtractor.FilterCount;
        if (frameCount == 0)
            return false;

        int paddedFrames = Math.Max(4, (frameCount + 3) / 4 * 4);
        RunEncoderChunk(paddedFrames);
        return true;
    }

    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _pendingFeatures.Clear();
        Array.Clear(_featureCache);
        _encoderHistory = 0;
        _decodedFrames = 0;
        _narrowBeamFrames = 0;
        _mediumBeamFrames = 0;
        _fullBeamFrames = 0;
        _jointRuns = 0;
        _decoderRuns = 0;
        _lowLatencyChunking = true;
        if (_cnnCacheBuffers is not null)
        {
            Array.Clear(_cnnCacheBuffers[0]);
            Array.Clear(_cnnCacheBuffers[1]);
            _cnnCacheBank = 0;
        }

        DisposeBeamStates();
        DecoderState zeroState = RentDecoderState();
        zeroState.Clear();
        DecoderState initial = RunDecoder(_blank, zeroState);
        ReturnDecoderState(zeroState);
        _beam =
        [
            new Hypothesis([], [], 0, initial, 0),
        ];
    }

    public string Recognize(float[] samples, int sampleRate)
    {
        Reset();
        float[] features = FbankExtractor.Extract(samples, sampleRate, out int frameCount);
        for (int frame = 0; frame < frameCount; frame++)
            AcceptFeature(features.AsSpan(frame * FbankExtractor.FilterCount, FbankExtractor.FilterCount));
        Flush();
        return CurrentResult.Text;
    }

    private void RunEncoderChunk(int featureFrames)
    {
        EncoderFeatureBuffers featureBuffers = GetEncoderFeatureBuffers(featureFrames);
        _featureCache.CopyTo(featureBuffers.Input, 0);
        int featureValues = featureFrames * FbankExtractor.FilterCount;
        _pendingFeatures.CopyTo(0, featureBuffers.Input, _featureCache.Length,
            Math.Min(featureValues, _pendingFeatures.Count));
        if (_pendingFeatures.Count < featureValues)
        {
            Array.Clear(
                featureBuffers.Input,
                _featureCache.Length + _pendingFeatures.Count,
                featureValues - _pendingFeatures.Count);
        }
        int consumedValues = Math.Min(featureValues, _pendingFeatures.Count);
        if (consumedValues > 0)
            _pendingFeatures.RemoveRange(0, consumedValues);

        AttentionCacheBuffers inputCache = GetAttentionCacheBuffers(_encoderHistory);
        int encodedFrames = featureFrames / 4;
        int outputHistory = _encoderHistory + encodedFrames;
        AttentionCacheBuffers outputCache = GetAttentionCacheBuffers(outputHistory);
        _encoderInputValues[0] = featureBuffers.InputValue;
        _encoderInputValues[1] = inputCache.KeyValue;
        _encoderInputValues[2] = inputCache.ValueValue;
        _encoderOutputValues[0] = featureBuffers.CacheOutputValue;
        _encoderOutputValues[1] = featureBuffers.HiddenOutputValue;
        _encoderOutputValues[2] = outputCache.KeyValue;
        _encoderOutputValues[3] = outputCache.ValueValue;
        int extraInput = 3;
        if (_usesCnnCache)
        {
            int nextCnnBank = 1 - _cnnCacheBank;
            _encoderInputValues[extraInput++] = _cnnCacheValues![_cnnCacheBank];
            _encoderOutputValues[4] = _cnnCacheValues[nextCnnBank];
        }
        if (_languageWidth > 0)
            _encoderInputValues[extraInput] = featureBuffers.LanguageIdValue!;
        _encoder.Run(
            _encoderRunOptions,
            _encoderInputNames,
            _encoderInputValues,
            _encoderOutputNames,
            _encoderOutputValues);
        if (_usesCnnCache)
            _cnnCacheBank = 1 - _cnnCacheBank;
        featureBuffers.CacheOutput.CopyTo(_featureCache, 0);

        int retainedHistory = Math.Min(outputHistory, EncoderMaximumHistory);
        if (retainedHistory != outputHistory)
        {
            AttentionCacheBuffers retainedCache = GetAttentionCacheBuffers(retainedHistory);
            CopyRetainedAttentionCache(outputCache.Key, retainedCache.Key, outputHistory, retainedHistory);
            CopyRetainedAttentionCache(outputCache.Value, retainedCache.Value, outputHistory, retainedHistory);
        }
        _encoderHistory = retainedHistory;

        for (int frame = 0; frame < encodedFrames; frame++)
            AdvanceBeam(featureBuffers.HiddenOutput.AsSpan(frame * EncoderWidth, EncoderWidth));
    }

    private EncoderFeatureBuffers GetEncoderFeatureBuffers(int featureFrames)
    {
        if (!_encoderFeatureBuffers.TryGetValue(featureFrames, out EncoderFeatureBuffers? buffers))
        {
            buffers = new EncoderFeatureBuffers(featureFrames, _languageWidth, _languageIndex);
            _encoderFeatureBuffers.Add(featureFrames, buffers);
        }
        return buffers;
    }

    private AttentionCacheBuffers GetAttentionCacheBuffers(int history)
    {
        if (!_attentionCacheBuffers.TryGetValue(history, out AttentionCacheBuffers? buffers))
        {
            buffers = new AttentionCacheBuffers(history);
            _attentionCacheBuffers.Add(history, buffers);
        }
        return buffers;
    }

    private void AdvanceBeam(ReadOnlySpan<float> acoustic)
    {
        int activeBeamWidth = GetActiveBeamWidth();
        if (activeBeamWidth == _options.MaximumBeamWidth)
            _fullBeamFrames++;
        else if (activeBeamWidth == _options.MediumBeamWidth)
            _mediumBeamFrames++;
        else
            _narrowBeamFrames++;
        var pending = _beam.Select(hypothesis => hypothesis with { SymbolsAtFrame = 0 }).ToList();
        var completed = new List<Hypothesis>(activeBeamWidth * 2);
        var frameStates = new HashSet<DecoderState>(ReferenceEqualityComparer.Instance);
        foreach (Hypothesis hypothesis in pending)
            frameStates.Add(hypothesis.State);
        int expansionBudget = activeBeamWidth * _options.MaximumSymbolsPerFrame;

        for (int expansion = 0; expansion < expansionBudget && pending.Count > 0; expansion++)
        {
            pending.Sort(HypothesisScoreComparer.Instance);
            Hypothesis hypothesis = pending[0];
            pending.RemoveAt(0);

            float[] logits = RunJoint(acoustic, hypothesis.State.Predictor);
            double logNormalizer = LogSumExp(logits);
            completed.Add(hypothesis with
            {
                Score = hypothesis.Score + logits[_blank] - logNormalizer,
                SymbolsAtFrame = 0,
            });

            if (hypothesis.SymbolsAtFrame < _options.MaximumSymbolsPerFrame)
            {
                GetTopNonBlank(
                    logits, logNormalizer,
                    out int firstToken, out double firstLogProbability,
                    out int secondToken, out double secondLogProbability);
                for (int rank = 0; rank < _options.TopTokensPerExpansion; rank++)
                {
                    int token = rank == 0 ? firstToken : secondToken;
                    double logProbability = rank == 0 ? firstLogProbability : secondLogProbability;
                    if (token < 0)
                        continue;
                    if (token == _eos)
                    {
                        completed.Add(hypothesis with
                        {
                            Score = hypothesis.Score + logProbability,
                            SymbolsAtFrame = 0,
                        });
                        continue;
                    }

                    DecoderState decoder = RunDecoder(token, hypothesis.State);
                    frameStates.Add(decoder);
                    var tokenIds = new List<int>(hypothesis.TokenIds) { token };
                    var tokenScores = new List<float>(hypothesis.TokenLogProbabilities)
                    {
                        (float)logProbability,
                    };
                    pending.Add(new Hypothesis(
                        tokenIds,
                        tokenScores,
                        hypothesis.Score + logProbability,
                        decoder,
                        hypothesis.SymbolsAtFrame + 1));
                }
            }

            pending = Prune(pending, activeBeamWidth * 2);
            if (completed.Count >= activeBeamWidth && pending.Count > 0)
            {
                double cutoff = completed.OrderByDescending(candidate => candidate.Score)
                    .Take(activeBeamWidth).Last().Score;
                if (pending.Max(candidate => candidate.Score) <= cutoff)
                    break;
            }
        }

        if (completed.Count == 0)
            completed.AddRange(pending);
        _beam = Prune(completed, activeBeamWidth);
        _decodedFrames++;
        var retainedStates = new HashSet<DecoderState>(
            _beam.Select(hypothesis => hypothesis.State),
            ReferenceEqualityComparer.Instance);
        foreach (DecoderState state in frameStates)
        {
            if (!retainedStates.Contains(state))
                ReturnDecoderState(state);
        }
    }

    private int GetActiveBeamWidth()
    {
        if (!_options.EnableAdaptiveBeam ||
            _decodedFrames < _options.AdaptiveBeamWarmupFrames ||
            _beam.Count < 2)
        {
            return _options.MaximumBeamWidth;
        }

        double scoreGap = _beam[0].Score - _beam[1].Score;
        if (scoreGap >= _options.MinimumBeamScoreGap)
            return _options.MinimumBeamWidth;
        return scoreGap >= _options.MediumBeamScoreGap
            ? _options.MediumBeamWidth
            : _options.MaximumBeamWidth;
    }

    private DecoderState RunDecoder(int token, DecoderState previous)
    {
        _decoderRuns++;
        _decoderToken[0] = token;
        DecoderState output = RentDecoderState();
        _decoderInputValues[1] = previous.HiddenValue;
        _decoderInputValues[2] = previous.CellValue;
        _decoderOutputValues[0] = output.PredictorValue;
        _decoderOutputValues[1] = output.HiddenValue;
        _decoderOutputValues[2] = output.CellValue;
        try
        {
            _decoder.Run(
                _decoderRunOptions,
                DecoderInputNames,
                _decoderInputValues,
                DecoderOutputNames,
                _decoderOutputValues);
            return output;
        }
        catch
        {
            ReturnDecoderState(output);
            throw;
        }
    }

    private float[] RunJoint(ReadOnlySpan<float> acoustic, float[] predictor)
    {
        _jointRuns++;
        acoustic.CopyTo(_jointAcoustic);
        predictor.CopyTo(_jointPredictor, 0);
        _joint.Run(
            _jointRunOptions,
            JointInputNames,
            _jointInputValues,
            JointOutputNames,
            _jointOutputValues);
        return _jointLogits;
    }

    private void GetTopNonBlank(
        float[] logits,
        double logNormalizer,
        out int firstToken,
        out double firstLogProbability,
        out int secondToken,
        out double secondLogProbability)
    {
        firstToken = -1;
        secondToken = -1;
        float firstScore = float.NegativeInfinity;
        float secondScore = float.NegativeInfinity;
        for (int token = 0; token < logits.Length; token++)
        {
            if (token == _blank)
                continue;
            float score = logits[token];
            if (score > firstScore)
            {
                secondToken = firstToken;
                secondScore = firstScore;
                firstToken = token;
                firstScore = score;
            }
            else if (score > secondScore)
            {
                secondToken = token;
                secondScore = score;
            }
        }
        firstLogProbability = firstScore - logNormalizer;
        secondLogProbability = secondScore - logNormalizer;
    }

    private RnntResult BuildResult(Hypothesis? hypothesis)
    {
        if (hypothesis is null || hypothesis.TokenIds.Count == 0)
            return RnntResult.Empty;

        var words = new List<RecognitionWord>();
        var currentPieces = new List<string>();
        var currentScores = new List<float>();
        for (int index = 0; index < hypothesis.TokenIds.Count; index++)
        {
            string piece = _tokens[hypothesis.TokenIds[index]];
            if (IsSpecialToken(piece))
                continue;

            bool startsWord = piece.StartsWith('_');
            if (startsWord && currentPieces.Count > 0)
                AddCurrentWord();
            currentPieces.Add(startsWord ? piece[1..] : piece);
            currentScores.Add(hypothesis.TokenLogProbabilities[index]);
        }
        AddCurrentWord();

        string rawText = string.Concat(hypothesis.TokenIds.Select(token => _tokens[token])
                .Where(piece => !IsSpecialToken(piece)))
            .Replace('_', ' ').Trim();
        float confidence = words.Count == 0 ? 0 : words.Average(word => word.Confidence);
        return new RnntResult(rawText, confidence, words);

        void AddCurrentWord()
        {
            string text = string.Concat(currentPieces);
            if (!string.IsNullOrEmpty(text))
            {
                double averageLogProbability = currentScores.Count == 0 ? -20 : currentScores.Average();
                words.Add(new RecognitionWord(text, (float)Math.Clamp(Math.Exp(averageLogProbability), 0, 1)));
            }
            currentPieces.Clear();
            currentScores.Clear();
        }
    }

    private static bool IsSpecialToken(string token) =>
        token.Length >= 2 && token[0] == '<' && token[^1] == '>';

    private static List<Hypothesis> Prune(IEnumerable<Hypothesis> hypotheses, int count)
    {
        var unique = new Dictionary<List<int>, Hypothesis>(TokenSequenceComparer.Instance);
        foreach (Hypothesis hypothesis in hypotheses)
        {
            if (!unique.TryGetValue(hypothesis.TokenIds, out Hypothesis? existing))
            {
                unique[hypothesis.TokenIds] = hypothesis;
                continue;
            }

            Hypothesis bestPath = hypothesis.Score > existing.Score ? hypothesis : existing;
            unique[hypothesis.TokenIds] = bestPath with
            {
                Score = LogAdd(existing.Score, hypothesis.Score),
            };
        }
        return unique.Values.OrderByDescending(hypothesis => hypothesis.Score).Take(count).ToList();
    }

    private static double LogSumExp(float[] logits)
    {
        float maximum = logits.Max();
        double sum = 0;
        foreach (float value in logits)
            sum += Math.Exp(value - maximum);
        return maximum + Math.Log(sum);
    }

    private static double LogAdd(double left, double right)
    {
        double maximum = Math.Max(left, right);
        return maximum + Math.Log(Math.Exp(left - maximum) + Math.Exp(right - maximum));
    }

    private static void CopyRetainedAttentionCache(
        float[] sourceCache,
        float[] destinationCache,
        int sourceHistory,
        int retainedHistory)
    {
        for (int layer = 0; layer < EncoderLayers; layer++)
        {
            for (int head = 0; head < AttentionHeads; head++)
            {
                int source = ((layer * AttentionHeads + head) * sourceHistory +
                              sourceHistory - retainedHistory) * AttentionWidth;
                int destination = (layer * AttentionHeads + head) * retainedHistory * AttentionWidth;
                Array.Copy(sourceCache, source, destinationCache, destination, retainedHistory * AttentionWidth);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _jointAcousticValue.Dispose();
        _jointPredictorValue.Dispose();
        _jointLogitsValue.Dispose();
        _jointRunOptions.Dispose();
        _encoderRunOptions.Dispose();
        _decoderTokenValue.Dispose();
        _decoderRunOptions.Dispose();
        if (_cnnCacheValues is not null)
        {
            foreach (OrtValue value in _cnnCacheValues)
                value.Dispose();
        }
        foreach (EncoderFeatureBuffers buffers in _encoderFeatureBuffers.Values)
            buffers.Dispose();
        foreach (AttentionCacheBuffers buffers in _attentionCacheBuffers.Values)
            buffers.Dispose();
        DisposeBeamStates();
        foreach (DecoderState state in _decoderStates)
            state.Dispose();
        _decoderStates.Clear();
        _decoderStatePool.Clear();
        _joint.Dispose();
        _decoder.Dispose();
        _encoder.Dispose();
    }

    private void DisposeBeamStates()
    {
        var states = new HashSet<DecoderState>(ReferenceEqualityComparer.Instance);
        foreach (Hypothesis hypothesis in _beam)
        {
            if (states.Add(hypothesis.State))
                ReturnDecoderState(hypothesis.State);
        }
        _beam.Clear();
    }

    private DecoderState RentDecoderState()
    {
        if (_decoderStatePool.TryPop(out DecoderState? state))
            return state;
        state = new DecoderState(_predictorWidth);
        _decoderStates.Add(state);
        return state;
    }

    private void ReturnDecoderState(DecoderState state) => _decoderStatePool.Push(state);

    private sealed class DecoderState : IDisposable
    {
        public DecoderState(int predictorWidth)
        {
            Predictor = new float[predictorWidth];
            PredictorValue = OrtValue.CreateTensorValueFromMemory(Predictor, [1, 1, predictorWidth]);
            HiddenValue = OrtValue.CreateTensorValueFromMemory(Hidden, [2, 1, 1024]);
            CellValue = OrtValue.CreateTensorValueFromMemory(Cell, [2, 1, 1024]);
        }

        public float[] Predictor { get; }
        public float[] Hidden { get; } = new float[DecoderStateSize];
        public float[] Cell { get; } = new float[DecoderStateSize];
        public OrtValue PredictorValue { get; }
        public OrtValue HiddenValue { get; }
        public OrtValue CellValue { get; }

        public void Clear()
        {
            Array.Clear(Predictor);
            Array.Clear(Hidden);
            Array.Clear(Cell);
        }

        public void Dispose()
        {
            PredictorValue.Dispose();
            HiddenValue.Dispose();
            CellValue.Dispose();
        }
    }

    private sealed class EncoderFeatureBuffers : IDisposable
    {
        public EncoderFeatureBuffers(int featureFrames, int languageWidth, int languageIndex)
        {
            int encodedFrames = featureFrames / 4;
            int inputFrames = featureFrames + 3;
            Input = new float[inputFrames * FbankExtractor.FilterCount];
            CacheOutput = new float[3 * FbankExtractor.FilterCount];
            HiddenOutput = new float[encodedFrames * EncoderWidth];
            InputValue = OrtValue.CreateTensorValueFromMemory(
                Input, [inputFrames, FbankExtractor.FilterCount]);
            CacheOutputValue = OrtValue.CreateTensorValueFromMemory(
                CacheOutput, [3, FbankExtractor.FilterCount]);
            HiddenOutputValue = OrtValue.CreateTensorValueFromMemory(
                HiddenOutput, [1, encodedFrames, EncoderWidth]);
            if (languageWidth > 0)
            {
                LanguageIds = new float[inputFrames * languageWidth];
                for (int frame = 0; frame < inputFrames; frame++)
                    LanguageIds[frame * languageWidth + languageIndex] = 1;
                LanguageIdValue = OrtValue.CreateTensorValueFromMemory(
                    LanguageIds, [inputFrames, languageWidth]);
            }
        }

        public float[] Input { get; }
        public float[] CacheOutput { get; }
        public float[] HiddenOutput { get; }
        public float[]? LanguageIds { get; }
        public OrtValue InputValue { get; }
        public OrtValue CacheOutputValue { get; }
        public OrtValue HiddenOutputValue { get; }
        public OrtValue? LanguageIdValue { get; }

        public void Dispose()
        {
            InputValue.Dispose();
            CacheOutputValue.Dispose();
            HiddenOutputValue.Dispose();
            LanguageIdValue?.Dispose();
        }
    }

    private static int GetRequiredWidth(IReadOnlyDictionary<string, NodeMetadata> metadata, string name)
    {
        if (!metadata.TryGetValue(name, out NodeMetadata? value) ||
            value.Dimensions.Length == 0 || value.Dimensions[^1] <= 0)
        {
            throw new NotSupportedException($"Model tensor '{name}' does not have a fixed trailing dimension.");
        }
        return value.Dimensions[^1];
    }

    private sealed class AttentionCacheBuffers : IDisposable
    {
        public AttentionCacheBuffers(int history)
        {
            int length = EncoderLayers * AttentionHeads * history * AttentionWidth;
            Key = new float[length];
            Value = new float[length];
            long[] shape = [EncoderLayers, 1, AttentionHeads, history, AttentionWidth];
            KeyValue = OrtValue.CreateTensorValueFromMemory(Key, shape);
            ValueValue = OrtValue.CreateTensorValueFromMemory(Value, shape);
        }

        public float[] Key { get; }
        public float[] Value { get; }
        public OrtValue KeyValue { get; }
        public OrtValue ValueValue { get; }

        public void Dispose()
        {
            KeyValue.Dispose();
            ValueValue.Dispose();
        }
    }

    private sealed record Hypothesis(
        List<int> TokenIds,
        List<float> TokenLogProbabilities,
        double Score,
        DecoderState State,
        int SymbolsAtFrame);

    private sealed class HypothesisScoreComparer : IComparer<Hypothesis>
    {
        public static readonly HypothesisScoreComparer Instance = new();
        public int Compare(Hypothesis? left, Hypothesis? right) => right!.Score.CompareTo(left!.Score);
    }

    private sealed class TokenSequenceComparer : IEqualityComparer<List<int>>
    {
        public static readonly TokenSequenceComparer Instance = new();

        public bool Equals(List<int>? left, List<int>? right)
        {
            if (ReferenceEquals(left, right))
                return true;
            if (left is null || right is null || left.Count != right.Count)
                return false;
            for (int index = 0; index < left.Count; index++)
            {
                if (left[index] != right[index])
                    return false;
            }
            return true;
        }

        public int GetHashCode(List<int> tokens)
        {
            var hash = new HashCode();
            foreach (int token in tokens)
                hash.Add(token);
            return hash.ToHashCode();
        }
    }
}

