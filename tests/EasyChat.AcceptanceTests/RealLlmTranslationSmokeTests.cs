using System.Text;
using EasyChat.Contracts.Translation;
using EasyChat.Infrastructure.Translation.OpenAi;

namespace EasyChat.AcceptanceTests;

[TestClass]
public sealed class RealLlmTranslationSmokeTests
{
    private const string ApiUrlVariable = "EASYCHAT_TEST_LLM_API_URL";
    private const string ApiKeyVariable = "EASYCHAT_TEST_LLM_API_KEY";
    private const string ModelVariable = "EASYCHAT_TEST_LLM_MODEL";

    [TestMethod]
    [TestCategory("Live")]
    public async Task ConfiguredOpenAiCompatibleEndpoint_StreamsNonEmptyTranslation()
    {
        var apiUrl = Environment.GetEnvironmentVariable(ApiUrlVariable);
        var apiKey = Environment.GetEnvironmentVariable(ApiKeyVariable);
        var model = Environment.GetEnvironmentVariable(ModelVariable);
        var missing = new[]
        {
            (Name: ApiUrlVariable, Value: apiUrl),
            (Name: ApiKeyVariable, Value: apiKey),
            (Name: ModelVariable, Value: model)
        }.Where(variable => string.IsNullOrWhiteSpace(variable.Value))
            .Select(variable => variable.Name)
            .ToArray();

        if (missing.Length > 0)
        {
            Assert.Inconclusive(
                $"Set {string.Join(", ", missing)} to run the live LLM smoke test.");
            return;
        }

        var provider = new OpenAiTranslationProvider(
            apiUrl!,
            apiKey!,
            model!,
            proxy: null);
        var request = new ChatTranslationProviderRequest(
            "Translate the user's text into Simplified Chinese. Return only the translation.",
            "Good morning.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var translation = new StringBuilder();

        await foreach (var chunk in provider.StreamAsync(request, timeout.Token))
            translation.Append(chunk);

        Assert.IsFalse(
            string.IsNullOrWhiteSpace(translation.ToString()),
            "The configured LLM endpoint completed without streaming translated text.");
    }
}
