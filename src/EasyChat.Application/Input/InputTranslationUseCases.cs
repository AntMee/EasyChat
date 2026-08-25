using EasyChat.Contracts.Input;
using EasyChat.Contracts.Platform;
using EasyChat.Contracts.SelectionTranslation;
using EasyChat.Contracts.Settings;
using EasyChat.Contracts.Translation;
using EasyChat.Shared.Results;

namespace EasyChat.Application.Input;

public sealed class InputTranslationUseCases(
    ISettingsUseCases settings,
    ITranslationLanguageCatalog languages,
    ITranslationUseCases translation,
    ISelectionTranslationUseCases selectionTranslation,
    IInputDeliveryUseCases delivery) : IInputTranslationUseCases
{
    private readonly ISettingsUseCases _settings = settings;
    private readonly ITranslationLanguageCatalog _languages = languages;
    private readonly ITranslationUseCases _translation = translation;
    private readonly ISelectionTranslationUseCases _selectionTranslation = selectionTranslation;
    private readonly IInputDeliveryUseCases _delivery = delivery;

    public async ValueTask<Result> TranslateAndDeliverAsync(
        InputTranslationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (sourceId, targetId) = ResolveDirection(
            request.SourceLanguageId,
            request.TargetLanguageId);

        var translated = await _translation.TranslateAsync(
            new TranslationRequest(
                request.Text,
                _languages.Get(sourceId),
                _languages.Get(targetId),
                PlainText: true),
            cancellationToken).ConfigureAwait(false);
        if (translated.IsFailure)
            return Result.Failure(translated.Error);
        if (string.IsNullOrWhiteSpace(translated.Value.Text))
            return Result.Failure(new Error("input.translation-empty", "Translation returned no text."));

        return await DeliverTranslatedAsync(
            new InputTranslatedDeliveryRequest(
                translated.Value.Text,
                request.Target,
                request.ReplaceCurrentInput,
                request.BeforeKey,
                request.AfterKey),
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<Result> DeliverTranslatedAsync(
        InputTranslatedDeliveryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var input = _settings.Current.Input;
        return await _delivery.DeliverAsync(
            new InputDeliveryRequest(
                request.Text,
                request.Target,
                input.DeliveryMode switch
                {
                    InputDeliveryMode.Paste => TextDeliveryMode.Paste,
                    InputDeliveryMode.Message => TextDeliveryMode.Message,
                    _ => TextDeliveryMode.Type
                },
                TimeSpan.FromMilliseconds(Math.Max(0, input.KeySendDelay)),
                request.ReplaceCurrentInput,
                request.BeforeKey,
                request.AfterKey),
            cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<InputTranslationPreviewEvent> StreamPreviewAsync(
        InputTranslationPreviewRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (sourceId, targetId) = ResolveDirection(request.SourceLanguageId, request.TargetLanguageId);
        yield return new InputTranslationPreviewStartedEvent(sourceId, targetId);

        var selectionRequest = new SelectionTranslationRequest(
            request.Text,
            _languages.Get(sourceId),
            _languages.Get(targetId),
            ResolveAnnotationLanguage());
        var stream = _selectionTranslation.StreamSentenceAsync(
            selectionRequest,
            SelectionTranslationConfigurationScope.Global,
            cancellationToken);
        await using var enumerator = stream.GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            bool hasNext;
            Exception? failure = null;
            try
            {
                hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                hasNext = false;
                failure = exception;
            }

            if (failure is not null)
            {
                yield return new InputTranslationPreviewFailedEvent(
                    new Error("input.preview-failed", failure.Message));
                yield break;
            }

            if (!hasNext)
                yield break;

            var item = enumerator.Current;
            switch (item)
            {
                case SelectionTranslationSourceDetectedEvent detected:
                    yield return new InputTranslationPreviewSourceDetectedEvent(detected.Language);
                    break;
                case SelectionTranslationDeltaEvent delta:
                    yield return new InputTranslationPreviewDeltaEvent(delta.Text);
                    break;
                case SelectionTranslationWordEvent word:
                    yield return new InputTranslationPreviewWordEvent(
                        word.Word,
                        word.Meaning,
                        word.Phonetic,
                        word.PartOfSpeech,
                        word.Forms,
                        word.Meanings);
                    break;
                case SelectionTranslationCompletedEvent:
                    yield return new InputTranslationPreviewCompletedEvent();
                    break;
            }
        }
    }

    private (string SourceId, string TargetId) ResolveDirection(
        string? requestedSourceId,
        string? requestedTargetId)
    {
        var input = _settings.Current.Input;
        var general = _settings.Current.General;
        if (input.FollowGlobalLanguage)
        {
            var sourceId = general.SourceLanguage.Id;
            var targetId = general.TargetLanguage.Id;
            if (input.ReverseTranslateLanguage)
                (sourceId, targetId) = (targetId, sourceId);
            return (sourceId, targetId);
        }

        return (
            requestedSourceId ?? input.TypingSourceLanguage,
            requestedTargetId ?? input.TypingTargetLanguage);
    }

    private TranslationLanguage? ResolveAnnotationLanguage()
    {
        var nativeLanguage = _settings.Current.General.NativeLanguage;
        return nativeLanguage is null ? null : _languages.Get(nativeLanguage.Id);
    }
}
