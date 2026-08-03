namespace EasyChat.Infrastructure.Settings.Persistence;

internal interface ISettingsFileStore
{
    ValueTask<string> ReadAllTextAsync(
        string path,
        CancellationToken cancellationToken);

    ValueTask WriteAllTextAsync(
        string path,
        string contents,
        CancellationToken cancellationToken);
}

internal sealed class PhysicalSettingsFileStore : ISettingsFileStore
{
    public ValueTask<string> ReadAllTextAsync(
        string path,
        CancellationToken cancellationToken) =>
        new(File.ReadAllTextAsync(path, cancellationToken));

    public async ValueTask WriteAllTextAsync(
        string path,
        string contents,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
                        ?? throw new InvalidOperationException("A settings file requires a directory.");
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporaryPath, contents, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, fullPath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }
}
