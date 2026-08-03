using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
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
        => Create(apiUrl, () => apiKey, model, proxy);

    internal static ChatClient Create(
        string apiUrl,
        Func<string> apiKeyFactory,
        string model,
        string? proxy)
    {
        ArgumentNullException.ThrowIfNull(apiKeyFactory);

        var options = CreateOptions(apiUrl, proxy);
        var client = new OpenAIClient(new ApiKeyCredential(apiKeyFactory()), options);
        return client.GetChatClient(model);
    }

    internal static OpenAIClientOptions CreateOptions(string apiUrl, string? proxy)
    {
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(apiUrl)
        };

        if (!string.IsNullOrWhiteSpace(proxy))
        {
            var handler = CreateProxyHandler(proxy);
            options.Transport = new HttpClientPipelineTransport(new HttpClient(handler));
        }

        return options;
    }

    internal static HttpClientHandler CreateProxyHandler(string proxy)
        => new()
        {
            Proxy = new WebProxy(proxy),
            UseProxy = true
        };
}
