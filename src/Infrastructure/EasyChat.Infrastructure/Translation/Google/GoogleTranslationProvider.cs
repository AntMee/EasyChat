using System.Net;
using EasyChat.Contracts.Settings;
using EasyChat.Contracts.Translation;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using RestSharp;

namespace EasyChat.Infrastructure.Translation.Google;

public sealed class GoogleTranslationProvider : ITranslationProvider, IDisposable
{
    private readonly IGoogleTranslationClient _client;
    private readonly string _key;
    private readonly ILogger _logger;
    private readonly string _model;
    private readonly Func<string> _requestError;

    public GoogleTranslationProvider(
        string model,
        string key,
        string? proxy,
        Func<string> requestError,
        ILogger logger)
        : this(model, key, TranslationProxyOptions.FromLegacyUrl(proxy), requestError, logger)
    {
    }

    public GoogleTranslationProvider(
        string model,
        string key,
        TranslationProxyOptions proxy,
        Func<string> requestError,
        ILogger logger)
        : this(
            model,
            key,
            new GoogleTranslationClient(proxy),
            requestError,
            logger)
    {
    }

    internal GoogleTranslationProvider(
        string model,
        string key,
        IGoogleTranslationClient client,
        Func<string> requestError,
        ILogger logger)
    {
        _model = model;
        _key = key;
        _client = client;
        _requestError = requestError;
        _logger = logger;

        _logger.LogDebug("GoogleService initialized: Model={Model}", model);
    }

    public void Dispose()
    {
        _client.Dispose();
        _logger.LogDebug("GoogleService disposed");
    }

    public async Task<string> TranslateAsync(
        TranslationProviderRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var translatedResult = await _client.TranslateAsync(
            request.Text,
            request.SourceLanguageCode,
            request.TargetLanguageCode,
            _key,
            _model,
            cancellationToken);
        if (request.ShowOriginal)
            return translatedResult ?? _requestError();

        if (translatedResult == null)
        {
            _logger.LogWarning("Translation failed: null response");
            return _requestError();
        }

        var response = JObject.Parse(translatedResult);
        if (response.ContainsKey("error"))
        {
            _logger.LogWarning("API error: {Response}", translatedResult);
            return translatedResult;
        }

        var result = response["data"]!["translations"]![0]!["translatedText"]!.ToString();
        _logger.LogDebug("Translation completed: ResultLength={Length}", result.Length);
        return result;
    }
}

internal interface IGoogleTranslationClient : IDisposable
{
    Task<string?> TranslateAsync(
        string text,
        string sourceLanguageCode,
        string targetLanguageCode,
        string key,
        string model,
        CancellationToken cancellationToken);
}

internal sealed class GoogleTranslationClient : IGoogleTranslationClient
{
    internal const string BaseUrl = "https://translation.googleapis.com/";
    internal const string Endpoint = "language/translate/v2/";

    private readonly RestClient _client;

    public GoogleTranslationClient(string? proxy)
        : this(TranslationProxyOptions.FromLegacyUrl(proxy))
    {
    }

    public GoogleTranslationClient(TranslationProxyOptions proxy)
        : this(new RestClient(CreateOptions(proxy)))
    {
    }

    internal GoogleTranslationClient(RestClient client)
    {
        _client = client;
    }

    public void Dispose() => _client.Dispose();

    public async Task<string?> TranslateAsync(
        string text,
        string sourceLanguageCode,
        string targetLanguageCode,
        string key,
        string model,
        CancellationToken cancellationToken)
    {
        var response = await _client.ExecuteAsync(
            CreateRequest(text, sourceLanguageCode, targetLanguageCode, key, model),
            cancellationToken);
        return response.Content;
    }

    internal static RestClientOptions CreateOptions(string? proxy)
        => CreateOptions(TranslationProxyOptions.FromLegacyUrl(proxy));

    internal static RestClientOptions CreateOptions(TranslationProxyOptions proxy)
    {
        var options = new RestClientOptions(BaseUrl);
        options.Proxy = proxy.Mode switch
        {
            NetworkProxyMode.System => WebRequest.DefaultWebProxy,
            NetworkProxyMode.Custom when Uri.TryCreate(proxy.ProxyUrl, UriKind.Absolute, out var uri) => new WebProxy(uri),
            _ => new WebProxy()
        };
        return options;
    }

    internal static RestRequest CreateRequest(
        string text,
        string sourceLanguageCode,
        string targetLanguageCode,
        string key,
        string model)
    {
        var request = new RestRequest(Endpoint);
        request.AddParameter("key", key);
        request.AddParameter("q", text);
        request.AddParameter("source", sourceLanguageCode);
        request.AddParameter("target", targetLanguageCode);
        request.AddParameter("model", model);
        return request;
    }
}
