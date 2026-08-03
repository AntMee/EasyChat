using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using EasyChat.Contracts.Translation;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RestSharp;

namespace EasyChat.Infrastructure.Translation.Tencent;

public sealed class TencentTranslationProvider : ITranslationProvider, IDisposable
{
    private readonly ITencentTranslationClient _client;
    private readonly ILogger _logger;
    private readonly Func<string> _requestError;

    public TencentTranslationProvider(
        string secretId,
        string secretKey,
        string? proxy,
        Func<string> requestError,
        ILogger logger)
        : this(
            new TencentTranslationClient(secretId, secretKey, proxy),
            requestError,
            logger)
    {
    }

    internal TencentTranslationProvider(
        ITencentTranslationClient client,
        Func<string> requestError,
        ILogger logger)
    {
        _client = client;
        _requestError = requestError;
        _logger = logger;

        _logger.LogDebug("TencentService initialized");
    }

    public void Dispose()
    {
        _client.Dispose();
        _logger.LogDebug("TencentService disposed");
    }

    public async Task<string> TranslateAsync(
        TranslationProviderRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var content = await _client.TranslateAsync(
            request.Text,
            request.SourceLanguageCode,
            request.TargetLanguageCode,
            cancellationToken);
        if (request.ShowOriginal)
            return content ?? _requestError();

        if (content == null)
        {
            _logger.LogWarning("Translation failed: null response");
            return _requestError();
        }

        var response = JObject.Parse(content);
        if (response["Response"]?["Error"] != null)
        {
            _logger.LogWarning("API error: {Response}", content);
            return _requestError();
        }

        var result = response["Response"]!["TargetText"]!.ToString();
        _logger.LogDebug("Translation completed: ResultLength={Length}", result.Length);
        return result;
    }
}

internal interface ITencentTranslationClient : IDisposable
{
    Task<string?> TranslateAsync(
        string text,
        string sourceLanguageCode,
        string targetLanguageCode,
        CancellationToken cancellationToken);
}

internal sealed class TencentTranslationClient : ITencentTranslationClient
{
    internal const string Endpoint = "tmt.tencentcloudapi.com";
    internal const int TimeoutSeconds = 5;

    private readonly RestClient _client;
    private readonly Func<DateTime> _localNow;
    private readonly string _secretId;
    private readonly string _secretKey;
    private readonly Func<DateTime> _utcNow;

    public TencentTranslationClient(string secretId, string secretKey, string? proxy)
        : this(
            secretId,
            secretKey,
            new RestClient(CreateOptions(proxy)),
            static () => DateTime.Now,
            static () => DateTime.UtcNow)
    {
    }

    internal TencentTranslationClient(
        string secretId,
        string secretKey,
        RestClient client,
        Func<DateTime> localNow,
        Func<DateTime> utcNow)
    {
        _secretId = secretId;
        _secretKey = secretKey;
        _client = client;
        _localNow = localNow;
        _utcNow = utcNow;
    }

    public void Dispose() => _client.Dispose();

    public async Task<string?> TranslateAsync(
        string text,
        string sourceLanguageCode,
        string targetLanguageCode,
        CancellationToken cancellationToken)
    {
        var payload = CreatePayload(text, sourceLanguageCode, targetLanguageCode);
        var request = CreateRequest(
            payload,
            _secretId,
            _secretKey,
            _localNow(),
            _utcNow);
        var response = await _client.ExecuteAsync(request, cancellationToken);
        return response.Content;
    }

    internal static RestClientOptions CreateOptions(string? proxy)
    {
        var options = new RestClientOptions();
        if (proxy != null)
            options.Proxy = new WebProxy(proxy);
        return options;
    }

    internal static string CreatePayload(
        string text,
        string sourceLanguageCode,
        string targetLanguageCode)
        => JsonConvert.SerializeObject(new Payload
        {
            SourceText = text,
            Source = sourceLanguageCode,
            Target = targetLanguageCode,
            ProjectId = 0
        });

    internal static RestRequest CreateRequest(
        string payload,
        string secretId,
        string secretKey,
        DateTime localDate,
        Func<DateTime> utcNow)
    {
        var headers = BuildHeaders(
            "tmt",
            Endpoint,
            "ap-guangzhou",
            "TextTranslate",
            "2018-03-21",
            localDate,
            utcNow,
            payload,
            secretId,
            secretKey);

        var request = new RestRequest($"https://{Endpoint}", Method.Post)
            .AddHeader("Content-Type", "application/json")
            .AddBody(payload);

        foreach (var (key, value) in headers)
            request.AddHeader(key, value);

        request.Timeout = TimeSpan.FromSeconds(TimeoutSeconds);
        return request;
    }

    internal static IReadOnlyDictionary<string, string> BuildHeaders(
        string service,
        string endpoint,
        string region,
        string action,
        string version,
        DateTime date,
        Func<DateTime> utcNow,
        string requestPayload,
        string secretId,
        string secretKey)
    {
        var dateString = date.ToString("yyyy-MM-dd");
        var requestTimestamp = new DateTimeOffset(utcNow()).ToUnixTimeSeconds();

        const string algorithm = "TC3-HMAC-SHA256";
        const string httpRequestMethod = "POST";
        const string canonicalUri = "/";
        const string canonicalQueryString = "";
        const string contentType = "application/json";
        var canonicalHeaders =
            "content-type:" + contentType + "; charset=utf-8\n"
            + "host:" + endpoint + "\n";
        const string signedHeaders = "content-type;host";
        var hashedRequestPayload = Sha256Hex(requestPayload);
        var canonicalRequest = httpRequestMethod + "\n"
                                                 + canonicalUri + "\n"
                                                 + canonicalQueryString + "\n"
                                                 + canonicalHeaders + "\n"
                                                 + signedHeaders + "\n"
                                                 + hashedRequestPayload;

        var credentialScope = dateString + "/" + service + "/tc3_request";
        var hashedCanonicalRequest = Sha256Hex(canonicalRequest);
        var stringToSign = algorithm + "\n"
                                    + requestTimestamp + "\n"
                                    + credentialScope + "\n"
                                    + hashedCanonicalRequest;

        var tc3SecretKey = Encoding.UTF8.GetBytes("TC3" + secretKey);
        var secretDate = HmacSha256(tc3SecretKey, Encoding.UTF8.GetBytes(dateString));
        var secretService = HmacSha256(secretDate, Encoding.UTF8.GetBytes(service));
        var secretSigning = HmacSha256(secretService, Encoding.UTF8.GetBytes("tc3_request"));
        var signatureBytes = HmacSha256(secretSigning, Encoding.UTF8.GetBytes(stringToSign));
        var signature = Convert.ToHexStringLower(signatureBytes);

        var authorization = algorithm + " "
                                      + "Credential=" + secretId + "/" + credentialScope + ", "
                                      + "SignedHeaders=" + signedHeaders + ", "
                                      + "Signature=" + signature;

        return new Dictionary<string, string>
        {
            { "Authorization", authorization },
            { "Host", endpoint },
            { "Content-Type", contentType + "; charset=utf-8" },
            { "X-TC-Timestamp", requestTimestamp.ToString() },
            { "X-TC-Version", version },
            { "X-TC-Action", action },
            { "X-TC-Region", region }
        };
    }

    private static string Sha256Hex(string value)
    {
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexStringLower(hashBytes);
    }

    private static byte[] HmacSha256(byte[] key, byte[] message)
    {
        using var mac = new HMACSHA256(key);
        return mac.ComputeHash(message);
    }

    [SuppressMessage("ReSharper", "UnusedAutoPropertyAccessor.Local")]
    private sealed class Payload
    {
        public string? SourceText { get; set; }
        public string? Source { get; set; }
        public string? Target { get; set; }
        public int ProjectId { get; set; }
    }
}
