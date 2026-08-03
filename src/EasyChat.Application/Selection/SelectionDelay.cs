namespace EasyChat.Application.Selection;

internal interface ISelectionDelay
{
    Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken);
}

internal sealed class SystemSelectionDelay : ISelectionDelay
{
    public Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, cancellationToken);
}
