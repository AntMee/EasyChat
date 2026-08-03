using System.Net;
using System.Security.Cryptography;
using System.Text;
using EasyChat.Contracts.Translation;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using RestSharp;

namespace EasyChat.Infrastructure.Translation.Baidu;

public sealed class BaiduTranslationProvider : ITranslationProvider, IDisposable
{
    private readonly IBaiduTranslationClient _client;
    private readonly ILogger _logger;
    private readonly Func<string> _requestError;

    public BaiduTranslationProvider(
        string appId,
        string secretKey,
        string? proxy,
        Func<string> requestError,
        ILogger logger)
        : this(
            new BaiduTranslationClient(appId, secretKey, proxy),
            requestError,
            logger)
    {
    }

    internal BaiduTranslationProvider(
        IBaiduTranslationClient client,
        Func<string> requestError,
        ILogger logger)
    {
        _client = client;
        _requestError = requestError;
        _logger = logger;

        _logger.LogDebug("BaiduService initialized");
    }

    public void Dispose()
    {
        _client.Dispose();
        _logger.LogDebug("BaiduService disposed");
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
            cancellationToken);
        if (request.ShowOriginal)
            return translatedResult ?? _requestError();

        if (translatedResult == null)
        {
            _logger.LogWarning("Translation failed: null response");
            return _requestError();
        }

        var response = JObject.Parse(translatedResult);
        var resultText = "";

        if (response.ContainsKey("error_msg"))
        {
            _logger.LogWarning("API error: {Response}", translatedResult);
            resultText = translatedResult;
        }
        else
        {
            var translationResults = response["trans_result"];
            if (translationResults != null)
            {
                resultText = translationResults.Aggregate(
                    resultText,
                    (current, item) => current + item["dst"]);
            }

            _logger.LogDebug("Translation completed: ResultLength={Length}", resultText.Length);
        }

        return resultText;
    }
}

internal interface IBaiduTranslationClient : IDisposable
{
    Task<string?> TranslateAsync(
        string text,
        string sourceLanguageCode,
        string targetLanguageCode,
        CancellationToken cancellationToken);
}

internal sealed class BaiduTranslationClient : IBaiduTranslationClient
{
    internal const string BaseUrl = "https://api.fanyi.baidu.com/";
    internal const string Endpoint = "api/trans/vip/translate";

    private readonly string _appId;
    private readonly RestClient _client;
    private readonly string _secretKey;

    public BaiduTranslationClient(string appId, string secretKey, string? proxy)
        : this(appId, secretKey, new RestClient(CreateOptions(proxy)))
    {
    }

    internal BaiduTranslationClient(
        string appId,
        string secretKey,
        RestClient client)
    {
        _appId = appId;
        _secretKey = secretKey;
        _client = client;
    }

    public void Dispose() => _client.Dispose();

    public async Task<string?> TranslateAsync(
        string text,
        string sourceLanguageCode,
        string targetLanguageCode,
        CancellationToken cancellationToken)
    {
        var request = CreateRequest(
            text,
            sourceLanguageCode,
            targetLanguageCode,
            _appId,
            _secretKey,
            CreateSalt());
        var response = await _client.ExecuteAsync(request, cancellationToken);
        return response.Content;
    }

    internal static RestClientOptions CreateOptions(string? proxy)
    {
        var options = new RestClientOptions(BaseUrl);
        if (proxy != null)
            options.Proxy = new WebProxy(proxy);
        return options;
    }

    internal static RestRequest CreateRequest(
        string text,
        string sourceLanguageCode,
        string targetLanguageCode,
        string appId,
        string secretKey,
        int salt)
    {
        var saltText = salt.ToString();
        var request = new RestRequest(Endpoint);
        request.AddParameter("q", text);
        request.AddParameter("from", sourceLanguageCode);
        request.AddParameter("to", targetLanguageCode);
        request.AddParameter("appid", appId);
        request.AddParameter("salt", saltText);
        request.AddParameter("sign", ComputeMd5Hash(appId + text + saltText + secretKey));
        return request;
    }

    internal static int CreateSalt() => Random.Shared.Next(100000);

    private static string ComputeMd5Hash(string input)
    {
        var hashBytes = MD5.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(hashBytes);
    }
}
