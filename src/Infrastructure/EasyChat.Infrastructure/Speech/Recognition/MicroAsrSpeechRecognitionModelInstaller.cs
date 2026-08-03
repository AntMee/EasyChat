using System.Formats.Tar;
using System.IO.Compression;
using EasyChat.Contracts.Speech;
using MicroASR;

namespace EasyChat.Infrastructure.Speech.Recognition;

public sealed class MicroAsrSpeechRecognitionModelInstaller : ISpeechRecognitionModelInstaller
{
    private readonly string _modelsDirectory;
    private readonly Action? _modelsChanged;
    private readonly SemaphoreSlim _importGate = new(1, 1);

    public MicroAsrSpeechRecognitionModelInstaller(MicroAsrSpeechRecognitionModelCatalog catalog)
        : this(catalog.ModelsDirectory, catalog.NotifyModelsChanged)
    {
    }

    internal MicroAsrSpeechRecognitionModelInstaller(string modelsDirectory)
        : this(modelsDirectory, null)
    {
    }

    private MicroAsrSpeechRecognitionModelInstaller(
        string modelsDirectory,
        Action? modelsChanged)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelsDirectory);
        _modelsDirectory = Path.GetFullPath(modelsDirectory);
        _modelsChanged = modelsChanged;
    }

    public async ValueTask<SpeechRecognitionModelImportResult> ImportAsync(
        SpeechRecognitionModelImportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourcePath);
        await _importGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(
                () => Import(request, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _importGate.Release();
        }
    }

    private SpeechRecognitionModelImportResult Import(
        SpeechRecognitionModelImportRequest request,
        CancellationToken cancellationToken)
    {
        var sourcePath = Path.GetFullPath(request.SourcePath);
        ValidateSource(sourcePath, request.SourceKind);

        var modelsParent = Directory.GetParent(_modelsDirectory)?.FullName
                           ?? throw new InvalidOperationException("The model library has no parent directory.");
        Directory.CreateDirectory(modelsParent);
        var stagingRoot = Path.Combine(
            modelsParent,
            $".{Path.GetFileName(_modelsDirectory)}.import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingRoot);

        try
        {
            var scanRoot = request.SourceKind switch
            {
                SpeechRecognitionModelImportSourceKind.Directory => sourcePath,
                SpeechRecognitionModelImportSourceKind.Archive => ExtractArchive(
                    sourcePath,
                    Path.Combine(stagingRoot, GetArchiveDirectoryName(sourcePath)),
                    cancellationToken),
                _ => throw new ArgumentOutOfRangeException(nameof(request.SourceKind))
            };

            var packages = DiscoverPackages(scanRoot, cancellationToken);
            if (packages.Count == 0)
                throw new InvalidDataException("No compatible MicroASR model was found in the selected source.");

            var result = InstallPackages(packages, stagingRoot, cancellationToken);
            if (result.ImportedModels.Count > 0)
                _modelsChanged?.Invoke();
            return result;
        }
        finally
        {
            TryDeleteDirectory(stagingRoot);
        }
    }

    private SpeechRecognitionModelImportResult InstallPackages(
        IReadOnlyList<SpeechModelPackage> packages,
        string stagingRoot,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_modelsDirectory);
        var preparedRoot = Path.Combine(stagingRoot, "prepared");
        Directory.CreateDirectory(preparedRoot);
        var imported = new List<SpeechRecognitionModel>();
        var existing = new List<SpeechRecognitionModel>();
        var prepared = new List<(string Id, string Directory)>();
        var identifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var package in packages.OrderBy(item => item.Locale, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = ValidateModelIdentifier(package.Locale);
            if (!identifiers.Add(id))
                throw new InvalidDataException($"More than one imported model uses the identifier '{id}'.");

            var targetDirectory = Path.Combine(_modelsDirectory, id);
            if (Directory.Exists(targetDirectory))
            {
                if (!SpeechModelPackage.IsSupported(targetDirectory))
                    throw new IOException($"The existing model directory '{id}' is incomplete or invalid.");
                existing.Add(new SpeechRecognitionModel(id));
                continue;
            }
            if (File.Exists(targetDirectory))
                throw new IOException($"A file already occupies the model destination '{id}'.");

            var preparedDirectory = Path.Combine(preparedRoot, id);
            CopyDirectory(package.Directory, preparedDirectory, cancellationToken);
            if (!IsInsideDirectory(package.VadPath, package.Directory))
            {
                File.Copy(
                    package.VadPath,
                    Path.Combine(preparedDirectory, "svad.quantized.onnx"),
                    overwrite: true);
            }
            _ = SpeechModelPackage.Load(preparedDirectory);
            prepared.Add((id, preparedDirectory));
        }

        var movedDirectories = new List<string>();
        try
        {
            foreach (var item in prepared)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var targetDirectory = Path.Combine(_modelsDirectory, item.Id);
                Directory.Move(item.Directory, targetDirectory);
                movedDirectories.Add(targetDirectory);
                imported.Add(new SpeechRecognitionModel(item.Id));
            }
        }
        catch
        {
            foreach (var directory in movedDirectories.AsEnumerable().Reverse())
                TryDeleteDirectory(directory);
            throw;
        }

        return new SpeechRecognitionModelImportResult(imported, existing);
    }

    private static IReadOnlyList<SpeechModelPackage> DiscoverPackages(
        string scanRoot,
        CancellationToken cancellationToken)
    {
        if (TryLoadPackage(scanRoot, out var selectedPackage))
            return [selectedPackage!];

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false
        };
        var packages = new List<SpeechModelPackage>();
        foreach (var directory in Directory.EnumerateDirectories(scanRoot, "*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryLoadPackage(directory, out var package))
                packages.Add(package!);
        }
        return packages;
    }

    private static bool TryLoadPackage(string directory, out SpeechModelPackage? package)
    {
        try
        {
            package = SpeechModelPackage.Load(directory);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidDataException or NotSupportedException or
                                           InvalidOperationException or ArgumentException)
        {
            package = null;
            return false;
        }
    }

    private static string ExtractArchive(
        string archivePath,
        string destination,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);
        if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ExtractZip(archivePath, destination, cancellationToken);
            return destination;
        }
        if (archivePath.EndsWith(".tar", StringComparison.OrdinalIgnoreCase) ||
            archivePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ||
            archivePath.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
        {
            ExtractTar(archivePath, destination, cancellationToken);
            return destination;
        }
        throw new NotSupportedException("Only ZIP, TAR, TAR.GZ and TGZ model archives are supported.");
    }

    private static string GetArchiveDirectoryName(string archivePath)
    {
        var fileName = Path.GetFileName(archivePath);
        var directoryName = fileName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^7]
            : Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrWhiteSpace(directoryName) || directoryName is "." or "..")
            throw new InvalidDataException("The model archive must have a valid file name.");
        return directoryName;
    }

    private static void ExtractZip(
        string archivePath,
        string destination,
        CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(entry.FullName))
                continue;
            var target = ResolveArchiveEntryPath(destination, entry.FullName);
            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = entry.Open();
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
        }
    }

    private static void ExtractTar(
        string archivePath,
        string destination,
        CancellationToken cancellationToken)
    {
        using var archiveStream = File.OpenRead(archivePath);
        var compressed = archivePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ||
                         archivePath.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase);
        using Stream content = compressed
            ? new GZipStream(archiveStream, CompressionMode.Decompress)
            : archiveStream;
        using var reader = new TarReader(content);
        while (reader.GetNextEntry() is { } entry)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = ResolveArchiveEntryPath(destination, entry.Name);
            if (entry.EntryType == TarEntryType.Directory)
            {
                Directory.CreateDirectory(target);
                continue;
            }
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                throw new InvalidDataException($"Unsupported TAR entry type: {entry.EntryType}.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            entry.DataStream?.CopyTo(output);
        }
    }

    private static string ResolveArchiveEntryPath(string destination, string entryName)
    {
        var root = Path.GetFullPath(destination);
        var normalized = entryName
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);
        var target = Path.GetFullPath(Path.Combine(root, normalized));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!string.Equals(target, root, comparison) && !target.StartsWith(rootPrefix, comparison))
            throw new InvalidDataException($"Archive entry escapes the extraction directory: {entryName}");
        return target;
    }

    private static void CopyDirectory(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Symbolic-link model directories are not supported.");

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false
        };
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
        }
    }

    private static string ValidateModelIdentifier(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier) ||
            identifier is "." or ".." ||
            !string.Equals(Path.GetFileName(identifier), identifier, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Invalid model directory name: '{identifier}'.");
        }
        return identifier;
    }

    private static bool IsInsideDirectory(string path, string directory)
    {
        var relative = Path.GetRelativePath(directory, path);
        return !Path.IsPathRooted(relative) &&
               relative != ".." &&
               !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static void ValidateSource(
        string sourcePath,
        SpeechRecognitionModelImportSourceKind sourceKind)
    {
        if (sourceKind == SpeechRecognitionModelImportSourceKind.Directory && !Directory.Exists(sourcePath))
            throw new DirectoryNotFoundException($"Model source directory was not found: {sourcePath}");
        if (sourceKind == SpeechRecognitionModelImportSourceKind.Archive && !File.Exists(sourcePath))
            throw new FileNotFoundException("Model archive was not found.", sourcePath);
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch
        {
        }
    }
}
