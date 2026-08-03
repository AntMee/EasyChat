using System.Globalization;

namespace MicroASR;

public sealed class SpeechModelPackage
{
    private SpeechModelPackage(
        string directory,
        string encoderPath,
        string predictorPath,
        string jointPath,
        string tokensPath,
        string vadPath,
        string? punctuationRulesPath,
        int languageIndex,
        int languageCount,
        float vadThreshold,
        string? locale)
    {
        Directory = directory;
        EncoderPath = encoderPath;
        PredictorPath = predictorPath;
        JointPath = jointPath;
        TokensPath = tokensPath;
        VadPath = vadPath;
        PunctuationRulesPath = punctuationRulesPath;
        LanguageIndex = languageIndex;
        LanguageCount = languageCount;
        VadThreshold = vadThreshold;
        Locale = string.IsNullOrWhiteSpace(locale) ? Path.GetFileName(directory) : locale;
    }

    public string Directory { get; }
    public string Locale { get; }
    public string EncoderPath { get; }
    public string PredictorPath { get; }
    public string JointPath { get; }
    public string TokensPath { get; }
    public string VadPath { get; }
    public string? PunctuationRulesPath { get; }
    public int LanguageIndex { get; }
    public int LanguageCount { get; }
    public float VadThreshold { get; }

    public static SpeechModelPackage Load(string modelDirectory, string? fallbackVadPath = null)
    {
        string directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(modelDirectory));
        string modelConfigPath = Path.Combine(directory, "model_onnx_quant.config");
        string speechConfigPath = Path.Combine(directory, "sr.ini");
        if (!File.Exists(modelConfigPath))
            throw new FileNotFoundException("Model configuration was not found.", modelConfigPath);
        if (!File.Exists(speechConfigPath))
            throw new FileNotFoundException("Speech configuration was not found.", speechConfigPath);

        IReadOnlyDictionary<string, string> model = ReadConfig(modelConfigPath);
        IReadOnlyDictionary<string, string> speech = ReadConfig(speechConfigPath);
        if (!model.TryGetValue("ModelType", out string? modelType) ||
            !string.Equals(modelType, "ONNX_TRANSFORMER_ENCODER", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException($"Unsupported model type: {modelType ?? "<missing>"}.");
        }

        string encoder = ResolveRequired(directory, model, "ModelEncoder");
        string predictor = ResolveRequired(directory, model, "ModelPredictor");
        string joint = ResolveRequired(directory, model, "ModelJoint");
        string tokens = ResolveRequired(directory, speech, "token-path");
        string vad = ResolveVad(directory, speech, fallbackVadPath);
        string? punctuationRules = ResolveOptional(directory, speech, "punctuation-path");
        punctuationRules ??= System.IO.Directory.EnumerateFiles(
            directory, "*_explicitPuncRules.txt", SearchOption.TopDirectoryOnly).SingleOrDefault();
        (int languageIndex, int languageCount) = ReadLanguage(model);
        float threshold = ReadFloat(speech, "vad-threshold", 0.4f);
        speech.TryGetValue("output-locale", out string? locale);
        return new SpeechModelPackage(
            directory, encoder, predictor, joint, tokens, vad, punctuationRules,
            languageIndex, languageCount, threshold, locale);
    }

    public static bool IsSupported(string modelDirectory)
    {
        try
        {
            Load(modelDirectory);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidDataException or NotSupportedException)
        {
            return false;
        }
    }

    public static IReadOnlyList<SpeechModelPackage> Discover(string modelLibraryDirectory)
    {
        string libraryDirectory = Path.GetFullPath(modelLibraryDirectory);
        if (!System.IO.Directory.Exists(libraryDirectory))
            throw new DirectoryNotFoundException($"Model library was not found: {libraryDirectory}");

        var packages = new List<SpeechModelPackage>();
        foreach (string directory in System.IO.Directory.EnumerateDirectories(libraryDirectory)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            try
            {
                packages.Add(Load(directory));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                               InvalidDataException or NotSupportedException)
            {
                // A model library may contain unrelated directories.
            }
        }
        return packages;
    }

    private static string ResolveVad(
        string directory,
        IReadOnlyDictionary<string, string> speech,
        string? fallbackVadPath)
    {
        if (speech.TryGetValue("vad-model-path", out string? configured))
        {
            string path = ResolvePath(directory, configured);
            if (File.Exists(path))
                return path;
        }

        string? libraryDirectory = System.IO.Directory.GetParent(directory)?.FullName;
        if (libraryDirectory is not null)
        {
            // MicroASR models-v1 ships one locale-neutral VAD in en-US for every locale to share.
            string english = Path.Combine(libraryDirectory, "en-US", "svad.quantized.onnx");
            if (File.Exists(english))
                return english;
            string? shared = System.IO.Directory.EnumerateFiles(
                libraryDirectory, "svad.quantized.onnx", SearchOption.AllDirectories).FirstOrDefault();
            if (shared is not null)
                return shared;
        }

        if (!string.IsNullOrWhiteSpace(fallbackVadPath))
        {
            string fallback = Path.GetFullPath(fallbackVadPath);
            if (File.Exists(fallback))
                return fallback;
        }

        throw new FileNotFoundException("No compatible neural VAD model was found.", directory);
    }

    private static string ResolveRequired(
        string directory,
        IReadOnlyDictionary<string, string> config,
        string key)
    {
        if (!config.TryGetValue(key, out string? relativePath) || string.IsNullOrWhiteSpace(relativePath))
            throw new InvalidDataException($"Configuration is missing '{key}'.");
        string path = ResolvePath(directory, relativePath);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Configured model file '{key}' was not found.", path);
        return path;
    }

    private static string? ResolveOptional(
        string directory,
        IReadOnlyDictionary<string, string> config,
        string key)
    {
        if (!config.TryGetValue(key, out string? relativePath) || string.IsNullOrWhiteSpace(relativePath))
            return null;
        string path = ResolvePath(directory, relativePath);
        return File.Exists(path) ? path : null;
    }

    private static string ResolvePath(string directory, string relativePath)
    {
        string normalized = relativePath.Trim().Trim('\"')
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);
        string path = Path.GetFullPath(Path.Combine(directory, normalized));
        string relative = Path.GetRelativePath(directory, path);
        if (Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException($"Configured path escapes the model directory: {relativePath}");
        return path;
    }

    private static IReadOnlyDictionary<string, string> ReadConfig(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string sourceLine in File.ReadLines(path))
        {
            string line = sourceLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            int separator = line.IndexOf('=');
            if (separator <= 0)
                continue;
            result[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }
        return result;
    }

    private static float ReadFloat(
        IReadOnlyDictionary<string, string> config,
        string key,
        float fallback) =>
        config.TryGetValue(key, out string? text) &&
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
            ? value
            : fallback;

    private static (int Index, int Count) ReadLanguage(IReadOnlyDictionary<string, string> config)
    {
        if (!config.TryGetValue("LangCandidates", out string? candidateText) ||
            !config.TryGetValue("Lang", out string? language))
        {
            return (0, 0);
        }

        string[] candidates = candidateText.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        int index = Array.FindIndex(candidates,
            candidate => string.Equals(candidate, language, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            throw new InvalidDataException($"Configured language '{language}' is not in LangCandidates.");
        return (index, candidates.Length);
    }
}
