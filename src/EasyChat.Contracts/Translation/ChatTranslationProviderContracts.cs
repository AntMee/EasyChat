namespace EasyChat.Contracts.Translation;

public sealed record ChatTranslationProviderRequest(
    string SystemPrompt,
    string UserText,
    float? Temperature = null,
    int? MaxOutputTokenCount = null,
    ChatReasoningEffort ReasoningEffort = ChatReasoningEffort.Default);

public enum ChatReasoningEffort
{
    Default,
    Low,
    High
}

public interface IChatTranslationProvider
{
    Task<string> CompleteAsync(
        ChatTranslationProviderRequest request,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<string> StreamAsync(
        ChatTranslationProviderRequest request,
        CancellationToken cancellationToken = default);
}
