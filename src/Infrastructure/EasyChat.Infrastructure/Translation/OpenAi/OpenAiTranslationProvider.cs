using System.Runtime.CompilerServices;
using EasyChat.Contracts.Translation;
using EasyChat.Infrastructure.OpenAi;
using OpenAI.Chat;

namespace EasyChat.Infrastructure.Translation.OpenAi;

public sealed class OpenAiTranslationProvider : IChatTranslationProvider
{
    private readonly string _apiKey;
    private readonly string _apiUrl;
    private readonly IOpenAiChatClientFactory _clientFactory;
    private readonly bool _enableThinking;
    private readonly string _model;
    private readonly string? _proxy;

    public OpenAiTranslationProvider(
        string apiUrl,
        string apiKey,
        string model,
        string? proxy,
        bool enableThinking = false)
        : this(
            apiUrl,
            apiKey,
            model,
            proxy,
            enableThinking,
            OpenAiChatClientFactory.Instance)
    {
    }

    internal OpenAiTranslationProvider(
        string apiUrl,
        string apiKey,
        string model,
        string? proxy,
        bool enableThinking,
        IOpenAiChatClientFactory clientFactory)
    {
        _apiUrl = apiUrl;
        _apiKey = apiKey;
        _model = model;
        _proxy = proxy;
        _enableThinking = enableThinking;
        _clientFactory = clientFactory;
    }

    public Task<string> CompleteAsync(
        ChatTranslationProviderRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var client = CreateClient();
        return client.CompleteAsync(request, _enableThinking, cancellationToken);
    }

    public async IAsyncEnumerable<string> StreamAsync(
        ChatTranslationProviderRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var client = CreateClient();
        await foreach (var chunk in client.StreamAsync(
                           request,
                           _enableThinking,
                           cancellationToken))
        {
            yield return chunk;
        }
    }

    private IOpenAiChatClient CreateClient()
        => _clientFactory.Create(_apiUrl, _apiKey, _model, _proxy);
}

internal interface IOpenAiChatClientFactory
{
    IOpenAiChatClient Create(
        string apiUrl,
        string apiKey,
        string model,
        string? proxy);
}

internal interface IOpenAiChatClient
{
    Task<string> CompleteAsync(
        ChatTranslationProviderRequest request,
        bool enableThinking,
        CancellationToken cancellationToken);

    IAsyncEnumerable<string> StreamAsync(
        ChatTranslationProviderRequest request,
        bool enableThinking,
        CancellationToken cancellationToken);
}

internal sealed class OpenAiChatClientFactory : IOpenAiChatClientFactory
{
    public static OpenAiChatClientFactory Instance { get; } = new();

    private OpenAiChatClientFactory()
    {
    }

    public IOpenAiChatClient Create(
        string apiUrl,
        string apiKey,
        string model,
        string? proxy)
        => new OpenAiChatClient(
            OpenAiSdkChatClientFactory.Create(apiUrl, apiKey, model, proxy));
}

internal sealed class OpenAiChatClient(ChatClient client) : IOpenAiChatClient
{
    private readonly ChatClient _client = client;

    public async Task<string> CompleteAsync(
        ChatTranslationProviderRequest request,
        bool enableThinking,
        CancellationToken cancellationToken)
    {
        var messages = CreateMessages(request);
        var options = CreateChatOptions(enableThinking);
        ChatCompletion completion = await _client.CompleteChatAsync(
            messages,
            options,
            cancellationToken);

        return completion.Content.Count > 0
            ? CombineContent(completion.Content.Select(content => content.Text))
            : string.Empty;
    }

    public async IAsyncEnumerable<string> StreamAsync(
        ChatTranslationProviderRequest request,
        bool enableThinking,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var messages = CreateMessages(request);
        var options = CreateChatOptions(enableThinking);

#pragma warning disable OPENAI001
        await foreach (var update in _client.CompleteChatStreamingAsync(
                           messages,
                           options,
                           cancellationToken))
        {
            foreach (var content in update.ContentUpdate)
                yield return content.Text;
        }
#pragma warning restore OPENAI001
    }

    internal static List<ChatMessage> CreateMessages(ChatTranslationProviderRequest request)
        =>
        [
            new SystemChatMessage(request.SystemPrompt),
            new UserChatMessage(request.UserText)
        ];

    internal static ChatCompletionOptions CreateChatOptions(bool enableThinking)
    {
        var options = new ChatCompletionOptions();
#pragma warning disable OPENAI001, SCME0001
        options.Patch.Set(
            "$.thinking"u8,
            BinaryData.FromString(CreateThinkingPatchJson(enableThinking)));

        if (enableThinking)
            options.ReasoningEffortLevel = ChatReasoningEffortLevel.High;
#pragma warning restore OPENAI001, SCME0001
        return options;
    }

    internal static string CreateThinkingPatchJson(bool enableThinking)
        => enableThinking
            ? "{\"type\":\"enabled\"}"
            : "{\"type\":\"disabled\"}";

    internal static string CombineContent(IEnumerable<string> content)
        => string.Concat(content);
}
