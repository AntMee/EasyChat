using System.Collections.Generic;
using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using EasyChat.Models.Translation;
using EasyChat.Services.Languages;

namespace EasyChat.Services.Translation;

public interface ITranslation
{
    Task<string> TranslateAsync(string text, LanguageDefinition? source, LanguageDefinition? destination, bool showOriginal = false,
        CancellationToken cancellationToken = default);

    async IAsyncEnumerable<string> StreamTranslateAsync(string text, LanguageDefinition? source, LanguageDefinition? destination,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var result = await TranslateAsync(text, source, destination, false, cancellationToken);
        yield return result;
    }

    async IAsyncEnumerable<TranslationStreamEvent> StreamTranslateEventsAsync(
        string text,
        LanguageDefinition? source,
        LanguageDefinition? destination,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);

        yield return new TranslationStartedEvent("translation", source!.EnglishName, destination!.EnglishName);
        await foreach (var chunk in StreamTranslateAsync(text, source, destination, cancellationToken))
        {
            if (!string.IsNullOrEmpty(chunk))
                yield return new TranslationDeltaEvent(chunk);
        }

        yield return new TranslationCompletedEvent();
    }
}
