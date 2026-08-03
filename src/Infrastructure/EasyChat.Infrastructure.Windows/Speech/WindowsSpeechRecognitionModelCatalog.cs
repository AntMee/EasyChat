using System.Runtime.Versioning;
using EasyChat.Contracts.Speech;

namespace EasyChat.Infrastructure.Windows.Speech;

[SupportedOSPlatform("windows")]
public sealed class WindowsSpeechRecognitionModelCatalog : ISpeechRecognitionModelCatalog
{
    private readonly string _modelsDirectory;

    public WindowsSpeechRecognitionModelCatalog()
        : this(Path.Combine(AppContext.BaseDirectory, "Lib"))
    {
    }

    internal WindowsSpeechRecognitionModelCatalog(string modelsDirectory)
    {
        _modelsDirectory = Path.GetFullPath(modelsDirectory);
    }

    public async ValueTask<IReadOnlyList<SpeechRecognitionModel>> GetModelsAsync(
        CancellationToken cancellationToken = default) =>
        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(_modelsDirectory))
                return (IReadOnlyList<SpeechRecognitionModel>)[];

            return Directory.EnumerateDirectories(_modelsDirectory)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => new SpeechRecognitionModel(name!))
                .OrderBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }, cancellationToken).ConfigureAwait(false);
}
