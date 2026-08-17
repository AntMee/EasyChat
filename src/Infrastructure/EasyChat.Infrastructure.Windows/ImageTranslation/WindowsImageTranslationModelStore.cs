using System.IO.Compression;
using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using EasyChat.Contracts.ApplicationData;
using EasyChat.Contracts.ImageTranslation;
using EasyChat.Contracts.Settings;

namespace EasyChat.Infrastructure.Windows.ImageTranslation;

[SupportedOSPlatform("windows")]
public sealed class WindowsImageTranslationModelStore : IImageTranslationModelStore
{
    internal const string AotGanModelId = "aotgan-onnx";
    internal const string AotGanOnnxFileName = "aotgan.onnx";
    internal const string AotGanDataFileName = "aotgan.data";
    internal const string AotGanArchiveSha256 = "AA23DE28BC3B9A87D0AB5AF85FF2E0DC524B61E539FBCBFD4878B9028A1C01A3";
    private const string AotGanVersion = "qualcomm/AOT-GAN-v0.60.0-onnx-float";
    private const string AotGanDownloadUri =
        "https://qaihub-public-assets.s3.us-west-2.amazonaws.com/qai-hub-models/models/aotgan/releases/v0.60.0/aotgan-onnx-float.zip";
    private static readonly ModelFileSpec[] AotGanFiles =
    [
        new(
            "aotgan-onnx-float/aotgan.onnx",
            AotGanOnnxFileName,
            679_522,
            "273E226D6CE8F786EAEB95DEF0FBE22F4025F694A9211513B834A20A80AE2334"),
        new(
            "aotgan-onnx-float/aotgan.data",
            AotGanDataFileName,
            60_793_600,
            "9A5FB31BFF02A111E16979EF8A463E3D919688343FAC1A8268C2C5B2241F05F3")
    ];
    private static readonly ImageTranslationModelPackage AotGanPackage = new(
        AotGanModelId,
        "AOT-GAN",
        "Precise screenshot text removal model.");
    private readonly IApplicationDataPaths _applicationData;

    public WindowsImageTranslationModelStore(IApplicationDataPaths applicationData)
    {
        _applicationData = applicationData ?? throw new ArgumentNullException(nameof(applicationData));
    }

    public IReadOnlyList<ImageTranslationModelPackage> ModelPackages { get; } = [AotGanPackage];

    internal static ImageTranslationModelPackage AotGanModelPackage => AotGanPackage;

    public bool IsModelDownloaded(ImageTranslationModelPackage package)
    {
        var spec = ResolvePackage(package);
        var root = Path.GetFullPath(_applicationData.ImageTranslationModelsDirectory);
        var installationDirectory = spec.InstallationDirectory(root);
        var manifestPath = spec.ManifestPath(installationDirectory);
        if (!File.Exists(manifestPath) || !spec.Files.All(file => File.Exists(file.Path(installationDirectory))))
            return false;

        try
        {
            var manifest = JsonSerializer.Deserialize<ModelManifest>(File.ReadAllText(manifestPath));
            if (!Matches(spec, manifest))
                return false;

            return spec.Files.All(file => VerifyFile(file, installationDirectory));
        }
        catch (IOException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public async Task DownloadModelAsync(
        ImageTranslationModelPackage package,
        NetworkProxyMode proxyMode,
        string? proxyUrl,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var spec = ResolvePackage(package);
        var root = Path.GetFullPath(_applicationData.ImageTranslationModelsDirectory);
        Directory.CreateDirectory(root);
        var archiveTemporary = Path.Combine(root, $".{spec.Package.Id}.{Guid.NewGuid():N}.zip");
        var stagingDirectory = Path.Combine(root, $".{spec.Package.Id}.{Guid.NewGuid():N}");
        try
        {
            using var client = new HttpClient(CreateHandler(proxyMode, proxyUrl), disposeHandler: true)
            {
                Timeout = TimeSpan.FromMinutes(10)
            };
            await DownloadArchiveAsync(
                client,
                spec,
                archiveTemporary,
                progress,
                cancellationToken).ConfigureAwait(false);
            Directory.CreateDirectory(stagingDirectory);
            await ExtractModelFilesAsync(
                archiveTemporary,
                stagingDirectory,
                spec,
                progress,
                cancellationToken).ConfigureAwait(false);

            var manifest = new ModelManifest(
                spec.Version,
                spec.DownloadUri,
                spec.ArchiveSha256,
                spec.Files.Select(file => new ModelFileManifest(file.FileName, file.Length, file.Sha256)).ToArray());
            await File.WriteAllTextAsync(
                spec.ManifestPath(stagingDirectory),
                JsonSerializer.Serialize(manifest),
                cancellationToken).ConfigureAwait(false);

            InstallModelDirectory(root, stagingDirectory, spec);
            progress?.Report(1);
        }
        finally
        {
            DeleteFileIfPresent(archiveTemporary);
            if (Directory.Exists(stagingDirectory))
                Directory.Delete(stagingDirectory, recursive: true);
        }
    }

    public void DeleteModel(ImageTranslationModelPackage package)
    {
        var spec = ResolvePackage(package);
        var root = Path.GetFullPath(_applicationData.ImageTranslationModelsDirectory);
        var installationDirectory = spec.InstallationDirectory(root);
        if (Directory.Exists(installationDirectory))
            Directory.Delete(installationDirectory, recursive: true);
    }

    internal static bool AreModelFilesInstalled(string modelDirectory) =>
        File.Exists(ResolveModelPath(modelDirectory))
        && File.Exists(ResolveModelDataPath(modelDirectory));

    internal static string ResolveModelPath(string modelDirectory) =>
        Path.Combine(Path.GetFullPath(modelDirectory), AotGanModelId, AotGanOnnxFileName);

    internal static string ResolveModelDataPath(string modelDirectory) =>
        Path.Combine(Path.GetFullPath(modelDirectory), AotGanModelId, AotGanDataFileName);

    private static void InstallModelDirectory(string root, string stagingDirectory, PackageSpec spec)
    {
        var installationDirectory = spec.InstallationDirectory(root);
        var backupDirectory = Path.Combine(root, $".{spec.Package.Id}.previous.{Guid.NewGuid():N}");
        var movedCurrentInstallation = false;
        try
        {
            if (Directory.Exists(installationDirectory))
            {
                Directory.Move(installationDirectory, backupDirectory);
                movedCurrentInstallation = true;
            }

            Directory.Move(stagingDirectory, installationDirectory);
        }
        catch
        {
            if (movedCurrentInstallation
                && !Directory.Exists(installationDirectory)
                && Directory.Exists(backupDirectory))
            {
                Directory.Move(backupDirectory, installationDirectory);
            }

            throw;
        }

        TryDeleteDirectory(backupDirectory);
    }

    private static async Task DownloadArchiveAsync(
        HttpClient client,
        PackageSpec spec,
        string archiveTemporary,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(
            spec.DownloadUri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new FileStream(
            archiveTemporary,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            1024 * 64,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var contentLength = response.Content.Headers.ContentLength;
        var buffer = new byte[1024 * 64];
        long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            hash.AppendData(buffer, 0, read);
            total += read;
            if (contentLength is > 0)
                progress?.Report(0.9 * total / contentLength.Value);
        }

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        var digest = Convert.ToHexString(hash.GetHashAndReset());
        if (!string.Equals(digest, spec.ArchiveSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"AOT-GAN archive SHA-256 verification failed. Expected {spec.ArchiveSha256}, got {digest}.");
        }
    }

    private static async Task ExtractModelFilesAsync(
        string archivePath,
        string stagingDirectory,
        PackageSpec spec,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var totalLength = spec.Files.Sum(file => file.Length);
        long extracted = 0;
        foreach (var file in spec.Files)
        {
            var entry = archive.GetEntry(file.ArchiveEntryPath)
                        ?? throw new InvalidDataException($"AOT-GAN archive is missing '{file.ArchiveEntryPath}'.");
            if (entry.Length != file.Length)
            {
                throw new InvalidDataException(
                    $"AOT-GAN archive entry '{file.ArchiveEntryPath}' has an unexpected length.");
            }

            await using var input = entry.Open();
            await using var output = new FileStream(
                file.Path(stagingDirectory),
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1024 * 64,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1024 * 64];
            long written = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                hash.AppendData(buffer, 0, read);
                written += read;
                extracted += read;
                progress?.Report(0.9 + 0.1 * extracted / totalLength);
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            var digest = Convert.ToHexString(hash.GetHashAndReset());
            if (written != file.Length || !string.Equals(digest, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"AOT-GAN model file '{file.FileName}' failed integrity verification.");
            }
        }
    }

    private static bool Matches(PackageSpec spec, ModelManifest? manifest)
    {
        if (manifest is null
            || !string.Equals(manifest.Version, spec.Version, StringComparison.Ordinal)
            || !string.Equals(manifest.SourceUrl, spec.DownloadUri, StringComparison.Ordinal)
            || !string.Equals(manifest.ArchiveSha256, spec.ArchiveSha256, StringComparison.OrdinalIgnoreCase)
            || manifest.Files is null
            || manifest.Files.Count != spec.Files.Count)
        {
            return false;
        }

        return manifest.Files.Zip(spec.Files).All(pair =>
            string.Equals(pair.First.FileName, pair.Second.FileName, StringComparison.Ordinal)
            && pair.First.Length == pair.Second.Length
            && string.Equals(pair.First.Sha256, pair.Second.Sha256, StringComparison.OrdinalIgnoreCase));
    }

    private static bool VerifyFile(ModelFileSpec file, string root)
    {
        var path = file.Path(root);
        if (!File.Exists(path) || new FileInfo(path).Length != file.Length)
            return false;

        using var stream = File.OpenRead(path);
        var digest = Convert.ToHexString(SHA256.HashData(stream));
        return string.Equals(digest, file.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static void DeleteFileIfPresent(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // The previous model can still be mapped by an active worker.
        }
        catch (UnauthorizedAccessException)
        {
            // A later cleanup can remove a previous model held by another process.
        }
    }

    private static HttpMessageHandler CreateHandler(NetworkProxyMode mode, string? proxyUrl) =>
        mode switch
        {
            NetworkProxyMode.None => new HttpClientHandler { UseProxy = false },
            NetworkProxyMode.Custom when Uri.TryCreate(proxyUrl, UriKind.Absolute, out var uri) =>
                new HttpClientHandler { UseProxy = true, Proxy = new WebProxy(uri) },
            NetworkProxyMode.Custom => new HttpClientHandler { UseProxy = false },
            _ => new HttpClientHandler { UseProxy = true }
        };

    private static PackageSpec ResolvePackage(ImageTranslationModelPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        return string.Equals(package.Id, AotGanModelId, StringComparison.Ordinal)
            ? new PackageSpec(AotGanPackage, AotGanVersion, AotGanDownloadUri, AotGanArchiveSha256, AotGanFiles)
            : throw new ArgumentException($"Unknown image translation model '{package.Id}'.", nameof(package));
    }

    private sealed record PackageSpec(
        ImageTranslationModelPackage Package,
        string Version,
        string DownloadUri,
        string ArchiveSha256,
        IReadOnlyList<ModelFileSpec> Files)
    {
        public string InstallationDirectory(string root) => System.IO.Path.Combine(root, Package.Id);

        public string ManifestPath(string root) => System.IO.Path.Combine(root, "manifest.json");
    }

    private sealed record ModelFileSpec(
        string ArchiveEntryPath,
        string FileName,
        long Length,
        string Sha256)
    {
        public string Path(string root) => System.IO.Path.Combine(root, FileName);
    }

    private sealed record ModelManifest(
        string Version,
        string SourceUrl,
        string ArchiveSha256,
        IReadOnlyList<ModelFileManifest> Files);

    private sealed record ModelFileManifest(string FileName, long Length, string Sha256);
}
