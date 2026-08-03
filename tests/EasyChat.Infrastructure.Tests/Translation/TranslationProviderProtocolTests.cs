using DeepL;
using EasyChat.Contracts.Translation;
using EasyChat.Infrastructure.OpenAi;
using EasyChat.Infrastructure.Translation;
using EasyChat.Infrastructure.Translation.Baidu;
using EasyChat.Infrastructure.Translation.DeepL;
using EasyChat.Infrastructure.Translation.Google;
using EasyChat.Infrastructure.Translation.OpenAi;
using EasyChat.Infrastructure.Translation.Tencent;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAI.Chat;
using RestSharp;

namespace EasyChat.Infrastructure.Tests.Translation;

[TestClass]
public sealed class TranslationProviderProtocolTests
{
    [TestMethod]
    public void BaiduRequest_PreservesEndpointParametersAndSignature()
    {
        var request = BaiduTranslationClient.CreateRequest(
            "source text",
            "source-code",
            "target-code",
            "app-id",
            "secret-key",
            42);
        var parameters = request.Parameters.ToDictionary(
            parameter => parameter.Name ?? string.Empty,
            parameter => parameter.Value?.ToString());

        Assert.AreEqual(BaiduTranslationClient.Endpoint, request.Resource);
        Assert.AreEqual(Method.Get, request.Method);
        Assert.AreEqual("source text", parameters["q"]);
        Assert.AreEqual("source-code", parameters["from"]);
        Assert.AreEqual("target-code", parameters["to"]);
        Assert.AreEqual("app-id", parameters["appid"]);
        Assert.AreEqual("42", parameters["salt"]);
        Assert.AreEqual("953f96d72095a1e1e49065b59342c6bc", parameters["sign"]);
    }

    [TestMethod]
    public void TencentProtocol_PreservesPayloadAndTc3Signature()
    {
        const string expectedPayload =
            "{\"SourceText\":\"source text\",\"Source\":\"source-code\",\"Target\":\"target-code\",\"ProjectId\":0}";
        var payload = TencentTranslationClient.CreatePayload(
            "source text",
            "source-code",
            "target-code");
        var headers = TencentTranslationClient.BuildHeaders(
            "tmt",
            TencentTranslationClient.Endpoint,
            "ap-guangzhou",
            "TextTranslate",
            "2018-03-21",
            new DateTime(2026, 7, 31, 9, 2, 3, DateTimeKind.Local),
            () => new DateTime(2026, 7, 31, 1, 2, 3, DateTimeKind.Utc),
            payload,
            "secret-id",
            "secret-key");

        Assert.AreEqual(expectedPayload, payload);
        Assert.AreEqual("1785459723", headers["X-TC-Timestamp"]);
        Assert.AreEqual("2018-03-21", headers["X-TC-Version"]);
        Assert.AreEqual("TextTranslate", headers["X-TC-Action"]);
        Assert.AreEqual("ap-guangzhou", headers["X-TC-Region"]);
        Assert.AreEqual(
            "TC3-HMAC-SHA256 Credential=secret-id/2026-07-31/tmt/tc3_request, "
            + "SignedHeaders=content-type;host, "
            + "Signature=9e6ecfb67da9f5fc077ca0ade6abe88ffc78824a0b0f338777460b9cf8c4254e",
            headers["Authorization"]);
    }

    [TestMethod]
    public void GoogleRequest_PreservesAllProviderParameters()
    {
        var request = GoogleTranslationClient.CreateRequest(
            "source text",
            "source-code",
            "target-code",
            "api-key",
            "nmt");
        var parameters = request.Parameters.ToDictionary(
            parameter => parameter.Name ?? string.Empty,
            parameter => parameter.Value?.ToString());

        Assert.AreEqual(GoogleTranslationClient.Endpoint, request.Resource);
        Assert.AreEqual(Method.Get, request.Method);
        Assert.AreEqual("api-key", parameters["key"]);
        Assert.AreEqual("source text", parameters["q"]);
        Assert.AreEqual("source-code", parameters["source"]);
        Assert.AreEqual("target-code", parameters["target"]);
        Assert.AreEqual("nmt", parameters["model"]);
    }

    [TestMethod]
    [DataRow("quality_optimized", "QualityOptimized")]
    [DataRow("prefer_quality_optimized", "PreferQualityOptimized")]
    [DataRow("latency_optimized", "LatencyOptimized")]
    [DataRow("unknown", "LatencyOptimized")]
    public async Task DeepLProvider_PreservesModelMapping(
        string configuredModel,
        string expectedModel)
    {
        var client = new RecordingDeepLClient();
        var provider = new DeepLTranslationProvider(configuredModel, client);

        await provider.TranslateAsync(new TranslationProviderRequest("text", "EN", "ZH", false));

        Assert.AreEqual(expectedModel, client.ModelType.ToString());
    }

    [TestMethod]
    public void OpenAiProtocol_PreservesMessageOrderAndThinkingConfiguration()
    {
        var request = new ChatTranslationProviderRequest("system prompt", "user text");
        var messages = OpenAiChatClient.CreateMessages(request);
        var enabled = OpenAiChatClient.CreateChatOptions(true);
        var disabled = OpenAiChatClient.CreateChatOptions(false);

        Assert.HasCount(2, messages);
        Assert.IsInstanceOfType<SystemChatMessage>(messages[0]);
        Assert.IsInstanceOfType<UserChatMessage>(messages[1]);
        Assert.AreEqual("system prompt", messages[0].Content.Single().Text);
        Assert.AreEqual("user text", messages[1].Content.Single().Text);
        Assert.AreEqual("{\"type\":\"enabled\"}", OpenAiChatClient.CreateThinkingPatchJson(true));
        Assert.AreEqual("{\"type\":\"disabled\"}", OpenAiChatClient.CreateThinkingPatchJson(false));
#pragma warning disable OPENAI001
        Assert.AreEqual(ChatReasoningEffortLevel.High, enabled.ReasoningEffortLevel);
        Assert.IsNull(disabled.ReasoningEffortLevel);
#pragma warning restore OPENAI001
    }

    [TestMethod]
    public void ProviderFactory_MapsResolvedOptionsToTechnologyAdapters()
    {
        var factory = new TranslationProviderFactory(NullLoggerFactory.Instance);
        var ai = factory.Create(new AiTranslationProviderOptions(
            new AiTranslationProviderConfiguration(
                "ai", "AI", "OpenAi", "https://api.example.com", "key", "model", false, false),
            null));
        var baidu = factory.Create(new MachineTranslationProviderOptions(
            new BaiduTranslationProviderConfiguration("baidu", false, "id", "key"),
            null,
            "request failed"));
        var tencent = factory.Create(new MachineTranslationProviderOptions(
            new TencentTranslationProviderConfiguration("tencent", false, "id", "key"),
            null,
            "request failed"));
        var google = factory.Create(new MachineTranslationProviderOptions(
            new GoogleTranslationProviderConfiguration("google", false, "nmt", "key"),
            null,
            "request failed"));
        var deepL = factory.Create(new MachineTranslationProviderOptions(
            new DeepLTranslationProviderConfiguration("deepl", false, "latency_optimized", "key"),
            null,
            "request failed"));

        Assert.IsInstanceOfType<OpenAiTranslationProvider>(ai);
        Assert.IsInstanceOfType<BaiduTranslationProvider>(baidu);
        Assert.IsInstanceOfType<TencentTranslationProvider>(tencent);
        Assert.IsInstanceOfType<GoogleTranslationProvider>(google);
        Assert.IsInstanceOfType<DeepLTranslationProvider>(deepL);

        (baidu as IDisposable)?.Dispose();
        (tencent as IDisposable)?.Dispose();
        (google as IDisposable)?.Dispose();
    }

    [TestMethod]
    public void OpenAiClientOptions_PreserveEndpointAndOptionalProxy()
    {
        var direct = OpenAiSdkChatClientFactory.CreateOptions(
            "https://api.example.com/v1",
            null);
        var proxied = OpenAiSdkChatClientFactory.CreateOptions(
            "https://api.example.com/v1",
            "http://127.0.0.1:7890");

        Assert.AreEqual(new Uri("https://api.example.com/v1"), direct.Endpoint);
        Assert.AreEqual(new Uri("https://api.example.com/v1"), proxied.Endpoint);
        Assert.AreNotSame(direct.Transport, proxied.Transport);
    }

    private sealed class RecordingDeepLClient : IDeepLTranslationClient
    {
        public ModelType ModelType { get; private set; }

        public Task<string> TranslateAsync(
            string text,
            string sourceLanguageCode,
            string targetLanguageCode,
            ModelType modelType,
            CancellationToken cancellationToken)
        {
            ModelType = modelType;
            return Task.FromResult("translated");
        }
    }
}
