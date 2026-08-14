using System.Net.Http.Headers;
using EasyChat.Contracts.Speech;
using EasyChat.Infrastructure.Network;

namespace EasyChat.Infrastructure.Speech.Recognition;

public sealed class MicroAsrSpeechRecognitionModelDownloadStore : ISpeechRecognitionModelDownloadStore
{
    private static readonly Uri ReleaseBaseUri = new(
        "https://github.com/SwaggyMacro/MicroASR/releases/download/models-v1/");

    private static readonly IReadOnlyList<SpeechRecognitionModelDownloadPackage> Packages =
    [
        CreatePackage("da-DK"),
        CreatePackage("de-DE"),
        CreatePackage("en-US"),
        CreatePackage("es-ES"),
        CreatePackage("fr-FR"),
        CreatePackage("it-IT"),
        CreatePackage("ja-JP"),
        CreatePackage("ko-KR"),
        CreatePackage("pt-BR"),
        CreatePackage("zh-CN")
    ];

    private readonly NetworkProxyHandlerFactory _httpClients;
    private readonly ISpeechRecognitionModelInstaller _installer;

    public MicroAsrSpeechRecognitionModelDownloadStore(
        NetworkProxyHandlerFactory httpClients,
        ISpeechRecognitionModelInstaller installer)
    {
        _httpClients = httpClients ?? throw new ArgumentNullException(nameof(httpClients));
        _installer = installer ?? throw new ArgumentNullException(nameof(installer));
    }

    public IReadOnlyList<SpeechRecognitionModelDownloadPackage> ModelPackages => Packages;

    public async Task<SpeechRecognitionModelImportResult> DownloadModelAsync(
        SpeechRecognitionModelDownloadPackage package,
        SpeechRecognitionModelDownloadOptions options,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(options);
        ValidatePackage(package);

        var archivePath = Path.Combine(Path.GetTempPath(), $"easychat-asr-{Guid.NewGuid():N}.zip");
        try
        {
            using var client = _httpClients.CreateHttpClient(options.ProxyMode, options.ProxyUrl);
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("EasyChat", "1.0"));
            using var response = await client.GetAsync(
                package.DownloadUri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var contentLength = response.Content.Headers.ContentLength;
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (var output = new FileStream(
                             archivePath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 81920,
                             useAsync: true))
            {
                var buffer = new byte[81920];
                long totalRead = 0;
                progress?.Report(0);
                while (await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false) is { } count and > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    totalRead += count;
                    if (contentLength is > 0)
                        progress?.Report(Math.Min(0.99, (double)totalRead / contentLength.Value));
                }
            }

            var result = await _installer.ImportAsync(
                new SpeechRecognitionModelImportRequest(
                    archivePath,
                    SpeechRecognitionModelImportSourceKind.Archive,
                    package.Id),
                cancellationToken).ConfigureAwait(false);
            progress?.Report(1);
            return result;
        }
        finally
        {
            try
            {
                File.Delete(archivePath);
            }
            catch
            {
            }
        }
    }

    private static SpeechRecognitionModelDownloadPackage CreatePackage(string id) =>
        new(id, new Uri(ReleaseBaseUri, $"{id}.zip"));

    private static void ValidatePackage(SpeechRecognitionModelDownloadPackage package)
    {
        if (!Packages.Any(candidate =>
                string.Equals(candidate.Id, package.Id, StringComparison.OrdinalIgnoreCase) &&
                candidate.DownloadUri == package.DownloadUri))
        {
            throw new ArgumentException("The ASR model package is not available from the configured release.", nameof(package));
        }
    }
}
