using System.Runtime.CompilerServices;
using EasyChat.Contracts.Settings;
using EasyChat.Contracts.Translation;
using EasyChat.Shared.Results;

namespace EasyChat.Application.Translation;

public sealed class TranslationUseCases : ITranslationUseCases
{
    private readonly TranslationSessionResolver _sessionResolver;
    private readonly ITranslationFailureSink _failureSink;

    public TranslationUseCases(
        ISettingsUseCases settings,
        ITranslationProviderFactory providerFactory,
        ITranslationFailureSink failureSink,
        TranslationMessages messages)
    {
        _sessionResolver = new TranslationSessionResolver(
            settings,
            providerFactory,
            messages);
        _failureSink = failureSink ?? throw new ArgumentNullException(nameof(failureSink));
    }

    public ITranslationSession Prepare(TranslationProviderSelection? provider = null) =>
        _sessionResolver.Create(provider);

    public async Task<Result<TranslationResponse>> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var session = _sessionResolver.Create(request.Provider);
            using var disposable = session as IDisposable;
            var response = await session.TranslateAsync(request, cancellationToken)
                .ConfigureAwait(false);
            return Result<TranslationResponse>.Success(response);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _failureSink.Report(exception);
            return Result<TranslationResponse>.Failure(
                new Error("translation.failed", exception.Message));
        }
    }

    public async IAsyncEnumerable<TranslationEvent> StreamAsync(
        TranslationRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        ITranslationSession? session = null;
        Exception? creationFailure = null;
        try
        {
            session = _sessionResolver.Create(request.Provider);
        }
        catch (Exception exception)
        {
            creationFailure = exception;
        }

        if (creationFailure is not null)
        {
            yield return new TranslationFailedEvent(
                new Error("translation.create_failed", creationFailure.Message));
            yield break;
        }

        using var disposable = session as IDisposable;
        await foreach (var item in session!.StreamAsync(request, cancellationToken)
                           .ConfigureAwait(false))
        {
            yield return item;
        }
    }

}
