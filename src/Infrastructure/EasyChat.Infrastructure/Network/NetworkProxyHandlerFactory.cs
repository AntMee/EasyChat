using System.Net;
using EasyChat.Contracts.Settings;

namespace EasyChat.Infrastructure.Network;

public sealed class NetworkProxyHandlerFactory(ISettingsUseCases settings)
{
    private readonly ISettingsUseCases _settings = settings ?? throw new ArgumentNullException(nameof(settings));

    public HttpClient CreateHttpClient() => new(CreateHttpHandler(), disposeHandler: true);

    public HttpClient CreateHttpClient(NetworkProxyMode mode, string? proxyUrl) =>
        new(CreateHttpHandler(mode, proxyUrl), disposeHandler: true);

    public HttpClientHandler CreateHttpHandler()
    {
        var proxy = Current;
        return CreateHttpHandler(proxy.Mode, proxy.ProxyUrl);
    }

    public HttpClientHandler CreateHttpHandler(NetworkProxyMode mode, string? proxyUrl) =>
        mode switch
        {
            NetworkProxyMode.None => new HttpClientHandler { UseProxy = false },
            NetworkProxyMode.Custom when Uri.TryCreate(proxyUrl, UriKind.Absolute, out var uri) => new HttpClientHandler
            {
                UseProxy = true,
                Proxy = new WebProxy(uri)
            },
            NetworkProxyMode.Custom => new HttpClientHandler { UseProxy = false },
            _ => new HttpClientHandler { UseProxy = true }
        };

    public IWebProxy? CreateWebSocketProxy()
    {
        var proxy = Current;
        return proxy.Mode switch
        {
            NetworkProxyMode.None => null,
            NetworkProxyMode.Custom when Uri.TryCreate(proxy.ProxyUrl, UriKind.Absolute, out var uri) => new WebProxy(uri),
            NetworkProxyMode.Custom => null,
            _ => WebRequest.DefaultWebProxy
        };
    }

    private ProxySettings Current => _settings.IsInitialized
        ? _settings.Current.NetworkProxy
        : new ProxySettings(NetworkProxyMode.System);
}
