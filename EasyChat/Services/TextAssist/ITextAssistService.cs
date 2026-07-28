using System.Collections.Generic;
using System.Threading;
using EasyChat.Models.Configuration;
using EasyChat.Models.Translation.TextAssist;

namespace EasyChat.Services.TextAssist;

public interface ITextAssistService
{
    IAsyncEnumerable<TextAssistStreamEvent> StreamTranslateAsync(
        string text,
        TextAssistProfile profile,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<TextAssistStreamEvent> StreamCorrectAsync(
        string text,
        TextAssistProfile profile,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<TextAssistStreamEvent> StreamPolishAsync(
        string text,
        TextAssistProfile profile,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<TextAssistStreamEvent> StreamSummarizeAsync(
        string text,
        TextAssistProfile profile,
        CancellationToken cancellationToken = default);
}
