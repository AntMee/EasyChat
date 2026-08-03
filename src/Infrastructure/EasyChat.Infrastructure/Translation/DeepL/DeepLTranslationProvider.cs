using System.Net;
using DeepL;
using EasyChat.Contracts.Translation;

namespace EasyChat.Infrastructure.Translation.DeepL;

public sealed class DeepLTranslationProvider : ITranslationProvider
{
    private readonly IDeepLTranslationClient _client;
    private readonly ModelType _modelType;

    public DeepLTranslationProvider(string modelType, string apiKey, string? proxy)
        : this(modelType, new DeepLTranslationClient(apiKey, proxy))
    {
    }

    internal DeepLTranslationProvider(string modelType, IDeepLTranslationClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _modelType = modelType switch
        {
            "quality_optimized" => ModelType.QualityOptimized,
            "prefer_quality_optimized" => ModelType.PreferQualityOptimized,
            _ => ModelType.LatencyOptimized
        };
    }

    public Task<string> TranslateAsync(
        TranslationProviderRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _client.TranslateAsync(
            request.Text,
            request.SourceLanguageCode,
            request.TargetLanguageCode,
            _modelType,
            cancellationToken);
    }
}

internal interface IDeepLTranslationClient
{
    Task<string> TranslateAsync(
        string text,
        string sourceLanguageCode,
        string targetLanguageCode,
        ModelType modelType,
        CancellationToken cancellationToken);
}

internal sealed class DeepLTranslationClient : IDeepLTranslationClient
{
    private readonly Translator _translator;

    public DeepLTranslationClient(string apiKey, string? proxy)
    {
        if (proxy != null)
        {
            var handler = new HttpClientHandler
            {
                Proxy = new WebProxy(proxy),
                UseProxy = true
            };
            var options = new TranslatorOptions
            {
                ClientFactory = () => new HttpClientAndDisposeFlag
                {
                    HttpClient = new HttpClient(handler),
                    DisposeClient = true
                }
            };
            _translator = new Translator(apiKey, options);
        }
        else
        {
            _translator = new Translator(apiKey);
        }
    }

    public async Task<string> TranslateAsync(
        string text,
        string sourceLanguageCode,
        string targetLanguageCode,
        ModelType modelType,
        CancellationToken cancellationToken)
    {
        var result = await _translator.TranslateTextAsync(
            text,
            sourceLanguageCode,
            targetLanguageCode,
            new TextTranslateOptions { ModelType = modelType },
            cancellationToken);
        return result.Text;
    }
}
