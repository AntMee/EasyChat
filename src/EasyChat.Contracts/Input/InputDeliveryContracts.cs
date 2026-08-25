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

public sealed record InputTranslationPreviewRequest(
    string Text,
    string? SourceLanguageId = null,
    string? TargetLanguageId = null);

public abstract record InputTranslationPreviewEvent;

public sealed record InputTranslationPreviewStartedEvent(
    string SourceLanguageId,
    string TargetLanguageId) : InputTranslationPreviewEvent;

public sealed record InputTranslationPreviewSourceDetectedEvent(string LanguageId)
    : InputTranslationPreviewEvent;

public sealed record InputTranslationPreviewDeltaEvent(string Text) : InputTranslationPreviewEvent;

public sealed record InputTranslationPreviewWordEvent(
    string Word,
    string? Meaning = null,
    string? Phonetic = null,
    string? PartOfSpeech = null,
    IReadOnlyList<string>? Forms = null,
    IReadOnlyList<string>? Meanings = null) : InputTranslationPreviewEvent;

public sealed record InputTranslationPreviewCompletedEvent : InputTranslationPreviewEvent;

public sealed record InputTranslationPreviewFailedEvent(Error Error)
    : InputTranslationPreviewEvent;

public sealed record InputTranslatedDeliveryRequest(
    string Text,
    ExternalTargetToken Target,
    bool ReplaceCurrentInput = false,
    string? BeforeKey = null,
    string? AfterKey = null);

public interface IInputTranslationUseCases
{
    ValueTask<Result> TranslateAndDeliverAsync(
        InputTranslationRequest request,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<InputTranslationPreviewEvent> StreamPreviewAsync(
        InputTranslationPreviewRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<Result> DeliverTranslatedAsync(
        InputTranslatedDeliveryRequest request,
        CancellationToken cancellationToken = default);
}
