using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using EasyChat.Contracts.Settings;
using EasyChat.Contracts.Translation;
using OpenAI;
using OpenAI.Chat;

namespace EasyChat.Infrastructure.OpenAi;

internal static class OpenAiSdkChatClientFactory
{
    internal static ChatClient Create(
        string apiUrl,
        string apiKey,
        string model,
        string? proxy)
        => Create(apiUrl, () => apiKey, model, TranslationProxyOptions.FromLegacyUrl(proxy));

    internal static ChatClient Create(
        string apiUrl,
        string apiKey,
        string model,
        TranslationProxyOptions proxy)
        => Create(apiUrl, () => apiKey, model, proxy);

    internal static ChatClient Create(
        string apiUrl,
        Func<string> apiKeyFactory,
        string model,
        TranslationProxyOptions proxy)
    {
        ArgumentNullException.ThrowIfNull(apiKeyFactory);

        var options = CreateOptionsWithPolicy(apiUrl, proxy);
        var client = new OpenAIClient(new ApiKeyCredential(apiKeyFactory()), options);
        return client.GetChatClient(model);
    }

    internal static OpenAIClientOptions CreateOptions(string apiUrl, string? proxy)
        => CreateOptionsWithPolicy(apiUrl, TranslationProxyOptions.FromLegacyUrl(proxy));

    internal static OpenAIClientOptions CreateOptionsWithPolicy(
        string apiUrl,
        TranslationProxyOptions proxy)
    {
        ArgumentNullException.ThrowIfNull(proxy);
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(apiUrl)
        };

        options.Transport = new HttpClientPipelineTransport(CreateHttpClient(proxy));

        return options;
    }

    internal static HttpClient CreateHttpClient(TranslationProxyOptions proxy)
        => new(CreateProxyHandler(proxy))
        {
            // Streaming responses can legitimately run longer than HttpClient's 100-second default.
            // Callers still control request lifetime through their cancellation tokens.
            Timeout = Timeout.InfiniteTimeSpan
        };

    internal static HttpClientHandler CreateProxyHandler(string? proxy)
        => CreateProxyHandler(TranslationProxyOptions.FromLegacyUrl(proxy));

    internal static HttpClientHandler CreateProxyHandler(TranslationProxyOptions proxy)
        => new()
        {
            Proxy = proxy.Mode == NetworkProxyMode.Custom &&
                    Uri.TryCreate(proxy.ProxyUrl, UriKind.Absolute, out var uri)
                ? new WebProxy(uri)
                : null,
            UseProxy = proxy.Mode == NetworkProxyMode.System ||
                       proxy.Mode == NetworkProxyMode.Custom &&
                       Uri.TryCreate(proxy.ProxyUrl, UriKind.Absolute, out _)
        };
}
