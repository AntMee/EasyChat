using EasyChat.Contracts.Speech;
using MicroASR;

namespace EasyChat.Infrastructure.Speech.Recognition;

public sealed class MicroAsrSpeechRecognitionModelCatalog : ISpeechRecognitionModelCatalog
{
    private readonly string _modelsDirectory;

    public MicroAsrSpeechRecognitionModelCatalog()
        : this(Path.Combine(AppContext.BaseDirectory, "Models"))
    {
    }

    internal MicroAsrSpeechRecognitionModelCatalog(string modelsDirectory)
    {
        _modelsDirectory = Path.GetFullPath(modelsDirectory);
    }

    public async ValueTask<IReadOnlyList<SpeechRecognitionModel>> GetModelsAsync(
        CancellationToken cancellationToken = default) =>
        await Task.Run(() => Discover(cancellationToken), cancellationToken).ConfigureAwait(false);

    private IReadOnlyList<SpeechRecognitionModel> Discover(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_modelsDirectory))
            return [];

        var models = new List<SpeechRecognitionModel>();
        foreach (var directory in Directory.EnumerateDirectories(_modelsDirectory)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (SpeechModelPackage.IsSupported(directory))
                models.Add(new SpeechRecognitionModel(Path.GetFileName(directory)));
        }
        return models;
    }
}
