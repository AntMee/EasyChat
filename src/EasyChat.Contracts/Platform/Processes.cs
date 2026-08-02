namespace EasyChat.Contracts.Platform;

public sealed record ProcessDescriptor(
    int ProcessId,
    string Name,
    string? MainWindowTitle,
    ReadOnlyMemory<byte> IconPng);

public interface IProcessCatalog
{
    ValueTask<IReadOnlyList<ProcessDescriptor>> GetProcessesAsync(
        CancellationToken cancellationToken = default);
}
