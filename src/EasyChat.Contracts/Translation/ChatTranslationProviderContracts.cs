namespace EasyChat.Contracts.Translation;

public sealed record ChatTranslationProviderRequest(
    string SystemPrompt,
    string UserText);

public interface IChatTranslationProvider
{
    Task<string> CompleteAsync(
        ChatTranslationProviderRequest request,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<string> StreamAsync(
        ChatTranslationProviderRequest request,
        CancellationToken cancellationToken = default);
}
