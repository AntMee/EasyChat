using System.Runtime.CompilerServices;
using EasyChat.Contracts.Translation;
using EasyChat.Infrastructure.OpenAi;
using OpenAI.Chat;

namespace EasyChat.Infrastructure.Translation.Google;

/// <summary>
/// Google OpenAI-compatibility adapter. Google maps reasoning effort differently
/// from OpenAI and does not accept the generic <c>thinking</c> request field.
/// </summary>
public sealed class GoogleAiTranslationProvider : IChatTranslationProvider
{
    private readonly string _apiKey;
    private readonly string _apiUrl;
    private readonly bool _enableThinking;
    private readonly string _model;
    private readonly TranslationProxyOptions _proxy;

    public GoogleAiTranslationProvider(
        string apiUrl,
        string apiKey,
        string model,
        TranslationProxyOptions proxy,
        bool enableThinking)
    {
        _apiUrl = apiUrl;
        _apiKey = apiKey;
        _model = model;
        _proxy = proxy ?? throw new ArgumentNullException(nameof(proxy));
        _enableThinking = enableThinking;
    }

    public async Task<string> CompleteAsync(
        ChatTranslationProviderRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        ChatCompletion completion = await CreateClient().CompleteChatAsync(
            CreateMessages(request),
            CreateChatOptions(_model, _enableThinking, request),
            cancellationToken);
        return completion.Content.Count > 0
            ? string.Concat(completion.Content.Select(content => content.Text))
            : string.Empty;
    }

    public async IAsyncEnumerable<string> StreamAsync(
        ChatTranslationProviderRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

#pragma warning disable OPENAI001
        await foreach (var update in CreateClient().CompleteChatStreamingAsync(
                           CreateMessages(request),
                           CreateChatOptions(_model, _enableThinking, request),
                           cancellationToken))
        {
            foreach (var content in update.ContentUpdate)
                yield return content.Text;
        }
#pragma warning restore OPENAI001
    }

    internal static List<ChatMessage> CreateMessages(ChatTranslationProviderRequest request) =>
    [
        new SystemChatMessage(request.SystemPrompt),
        new UserChatMessage(request.UserText)
    ];

    internal static ChatCompletionOptions CreateChatOptions(
        string model,
        bool enableThinking,
        ChatTranslationProviderRequest? request = null)
    {
        var options = new ChatCompletionOptions
        {
            Temperature = request?.Temperature,
            MaxOutputTokenCount = request?.MaxOutputTokenCount
        };

#pragma warning disable OPENAI001, SCME0001
        options.Patch.Set(
            "$.reasoning_effort"u8,
            BinaryData.FromString($"\"{ResolveReasoningEffort(model, enableThinking, request)}\""));
#pragma warning restore OPENAI001, SCME0001
        return options;
    }

    internal static string ResolveReasoningEffort(
        string model,
        bool enableThinking,
        ChatTranslationProviderRequest? request = null)
    {
        if (!enableThinking)
            return SupportsDisabledThinking(model) ? "none" : "minimal";

        return request?.ReasoningEffort == ChatReasoningEffort.Low
            ? "low"
            : "high";
    }

    private static bool SupportsDisabledThinking(string model) =>
        model.Contains("gemini-2.5", StringComparison.OrdinalIgnoreCase)
        && !model.Contains("pro", StringComparison.OrdinalIgnoreCase);

    private ChatClient CreateClient() =>
        OpenAiSdkChatClientFactory.Create(_apiUrl, _apiKey, _model, _proxy);
}
