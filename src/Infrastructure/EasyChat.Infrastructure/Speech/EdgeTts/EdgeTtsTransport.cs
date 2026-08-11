using System.Globalization;
using System.Net.WebSockets;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using EasyChat.Contracts.Speech;
using EasyChat.Contracts.Settings;
using EasyChat.Infrastructure.Network;

namespace EasyChat.Infrastructure.Speech.EdgeTts;

internal interface IEdgeTtsTransport
{
    ValueTask<ReadOnlyMemory<byte>> SynthesizeAsync(
        TtsSynthesisRequest request,
        CancellationToken cancellationToken);
}

internal sealed class EdgeTtsTransport : IEdgeTtsTransport
{
    private const long WindowsEpochSeconds = 11_644_473_600;
    private const string BaseUrl = "speech.platform.bing.com/consumer/speech/synthesize/readaloud";
    private const string TrustedClientToken = "6A5AA1D4EAFF4E9FB37E23D68491D6F4";
    private const string ChromiumVersion = "143.0.3650.75";
    private const string ChromiumMajorVersion = "143";
    private const string GecVersion = "1-" + ChromiumVersion;
    private readonly NetworkProxyHandlerFactory? _networkProxy;

    public EdgeTtsTransport()
    {
    }

    public EdgeTtsTransport(ISettingsUseCases settings) => _networkProxy = new NetworkProxyHandlerFactory(settings);

    public async ValueTask<ReadOnlyMemory<byte>> SynthesizeAsync(
        TtsSynthesisRequest request,
        CancellationToken cancellationToken)
    {
        using var client = new ClientWebSocket();
        Configure(client.Options, _networkProxy?.CreateWebSocketProxy());
        var connectionId = Guid.NewGuid().ToString("N");
        var uri = new Uri(
            $"wss://{BaseUrl}/edge/v1?TrustedClientToken={TrustedClientToken}"
            + $"&ConnectionId={connectionId}&Sec-MS-GEC={GenerateSecMsGec(DateTimeOffset.UtcNow)}"
            + $"&Sec-MS-GEC-Version={GecVersion}");
        await client.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
        await SendTextAsync(client, CreateConfigurationMessage(), cancellationToken).ConfigureAwait(false);
        await SendTextAsync(client, CreateSsmlMessage(request), cancellationToken).ConfigureAwait(false);

        using var audio = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (client.State == WebSocketState.Open)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await client.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await client.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        string.Empty,
                        cancellationToken).ConfigureAwait(false);
                    return audio.ToArray();
                }
                await message.WriteAsync(buffer.AsMemory(0, result.Count), cancellationToken)
                    .ConfigureAwait(false);
            } while (!result.EndOfMessage);

            var data = message.ToArray();
            if (result.MessageType == WebSocketMessageType.Text)
            {
                if (Encoding.UTF8.GetString(data).Contains("Path:turn.end", StringComparison.Ordinal))
                    break;
                continue;
            }

            if (result.MessageType != WebSocketMessageType.Binary || data.Length < 2)
                continue;
            var headerLength = (data[0] << 8) | data[1];
            if (data.Length < headerLength + 2)
                continue;
            var headers = Encoding.UTF8.GetString(data, 2, headerLength);
            if (headers.Contains("Path:audio", StringComparison.Ordinal))
            {
                await audio.WriteAsync(
                    data.AsMemory(headerLength + 2, data.Length - headerLength - 2),
                    cancellationToken).ConfigureAwait(false);
            }
        }

        return audio.ToArray();
    }

    internal static string GenerateSecMsGec(DateTimeOffset now)
    {
        var seconds = now.ToUnixTimeSeconds() + WindowsEpochSeconds;
        seconds -= seconds % 300;
        var value = (seconds * 10_000_000).ToString(CultureInfo.InvariantCulture);
        return Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(value + TrustedClientToken)));
    }

    private static void Configure(ClientWebSocketOptions options, System.Net.IWebProxy? proxy)
    {
        options.Proxy = proxy;
        options.SetRequestHeader(
            "User-Agent",
            $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
            + $"(KHTML, like Gecko) Chrome/{ChromiumMajorVersion}.0.0.0 Safari/537.36 "
            + $"Edg/{ChromiumMajorVersion}.0.0.0");
        options.SetRequestHeader("Accept-Encoding", "gzip, deflate, br, zstd");
        options.SetRequestHeader("Accept-Language", "en-US,en;q=0.9");
        options.SetRequestHeader("Pragma", "no-cache");
        options.SetRequestHeader("Cache-Control", "no-cache");
        options.SetRequestHeader("Origin", "chrome-extension://jdiccldimpdaibmpdkjnbmckianbfold");
        var muid = Guid.NewGuid().ToString("N").ToUpperInvariant();
        options.SetRequestHeader("Cookie", $"muid={muid};");
    }

    private static string CreateConfigurationMessage() =>
        $"X-Timestamp:{GetTimestamp()}\r\n"
        + "Content-Type:application/json; charset=utf-8\r\n"
        + "Path:speech.config\r\n\r\n"
        + "{\"context\":{\"synthesis\":{\"audio\":{\"metadataoptions\":{"
        + "\"sentenceBoundaryEnabled\":\"false\",\"wordBoundaryEnabled\":\"false\"},"
        + "\"outputFormat\":\"audio-24khz-48kbitrate-mono-mp3\"}}}}\r\n";

    private static string CreateSsmlMessage(TtsSynthesisRequest request)
    {
        var ssml = "<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='en-US'>"
                   + $"<voice name='{request.VoiceId}'>"
                   + $"<prosody pitch='{request.Pitch ?? "+0Hz"}' rate='{request.Rate ?? "+0%"}' "
                   + $"volume='{request.Volume ?? "+0%"}'>"
                   + SecurityElement.Escape(request.Text)
                   + "</prosody></voice></speak>";
        return $"X-RequestId:{Guid.NewGuid():N}\r\n"
               + "Content-Type:application/ssml+xml\r\n"
               + $"X-Timestamp:{GetTimestamp()}Z\r\n"
               + "Path:ssml\r\n\r\n"
               + ssml;
    }

    private static string GetTimestamp() =>
        DateTime.UtcNow.ToString(
            "ddd MMM dd yyyy HH:mm:ss 'GMT+0000 (Coordinated Universal Time)'",
            CultureInfo.InvariantCulture);

    private static async Task SendTextAsync(
        ClientWebSocket client,
        string message,
        CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        await client.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken)
            .ConfigureAwait(false);
    }
}
