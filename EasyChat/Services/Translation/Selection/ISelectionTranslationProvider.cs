using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using EasyChat.Models.Translation.Selection;

namespace EasyChat.Services.Translation.Selection;

public interface ISelectionTranslationProvider
{
    /// <summary>
    /// Translates the given text using the specified source and target languages.
    /// The provider should automatically detect if it's a word or sentence and return the appropriate result.
    /// </summary>
    Task<SelectionTranslationResult> TranslateAsync(string text, string sourceLang, string targetLang, CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams independently parseable result updates. Providers without native streaming can
    /// return the final result as a sequence of events.
    /// </summary>
    async IAsyncEnumerable<SelectionTranslationStreamEvent> StreamTranslateAsync(
        string text,
        string sourceLang,
        string targetLang,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var result = await TranslateAsync(text, sourceLang, targetLang, cancellationToken);
        foreach (var translationEvent in SelectionTranslationStreamEventFactory.FromResult(result))
        {
            yield return translationEvent;
        }
    }
}
