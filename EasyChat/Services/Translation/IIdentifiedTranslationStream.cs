using System.Collections.Generic;
using System.Threading;
using EasyChat.Models.Translation;
using EasyChat.Services.Languages;

namespace EasyChat.Services.Translation;

public interface IIdentifiedTranslationStream
{
    IAsyncEnumerable<IdentifiedTranslationStreamEvent> StreamIdentifiedTranslationsAsync(
        string text,
        LanguageDefinition? source,
        LanguageDefinition? destination,
        CancellationToken cancellationToken = default);
}
