using EasyChat.Contracts.Platform;
using EasyChat.Shared.Results;

namespace EasyChat.Contracts.Input;

public sealed record InputDeliveryRequest(
    string Text,
    ExternalTargetToken Target,
    TextDeliveryMode Mode,
    TimeSpan KeyDelay,
    bool ReplaceCurrentInput = false,
    string? BeforeKey = null,
    string? AfterKey = null);

public interface IInputDeliveryUseCases
{
    ValueTask<Result> DeliverAsync(
        InputDeliveryRequest request,
        CancellationToken cancellationToken = default);
}
