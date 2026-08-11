using EasyChat.Infrastructure.Network;
using Velopack.Sources;

namespace EasyChat.Infrastructure.Updates;

internal sealed class NetworkProxyFileDownloader(NetworkProxyHandlerFactory proxyFactory)
    : HttpClientFileDownloader
{
    private readonly NetworkProxyHandlerFactory _proxyFactory =
        proxyFactory ?? throw new ArgumentNullException(nameof(proxyFactory));

    protected override HttpClientHandler CreateHttpClientHandler() =>
        _proxyFactory.CreateHttpHandler();
}
