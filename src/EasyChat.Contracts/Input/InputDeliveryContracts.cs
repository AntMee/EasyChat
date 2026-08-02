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

public sealed record InputTranslationRequest(
    string Text,
    ExternalTargetToken Target,
    string? SourceLanguageId = null,
    string? TargetLanguageId = null,
    bool ReplaceCurrentInput = false,
    string? BeforeKey = null,
    string? AfterKey = null);

public interface IInputTranslationUseCases
{
    ValueTask<Result> TranslateAndDeliverAsync(
        InputTranslationRequest request,
        CancellationToken cancellationToken = default);
}
