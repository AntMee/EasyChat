using System.Net.Http.Headers;
using System.Text.Json;
using EasyChat.Contracts.AiModels;
using EasyChat.Infrastructure.Network;

namespace EasyChat.Infrastructure.AiModels;

public sealed class HttpAiModelCatalogTransport : IAiModelCatalogTransport
{
    private readonly NetworkProxyHandlerFactory _clientFactory;

    internal HttpAiModelCatalogTransport(NetworkProxyHandlerFactory clientFactory) =>
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));

    public async Task<IReadOnlyList<string>> FetchModelsAsync(
        AiModelCatalogRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var apiBase = request.ApiUrl.TrimEnd('/');
        if (request.Provider == AiModelCatalogProvider.Gemini &&
            apiBase.EndsWith("/openai", StringComparison.OrdinalIgnoreCase))
        {
            apiBase = apiBase[..^"/openai".Length];
        }

        var endpoint = $"{apiBase}/models";
        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, endpoint);
        if (request.Provider == AiModelCatalogProvider.Gemini &&
            !string.IsNullOrWhiteSpace(request.ApiKey))
        {
            httpRequest.RequestUri = new Uri($"{endpoint}?key={Uri.EscapeDataString(request.ApiKey)}");
        }
        else if (request.Provider == AiModelCatalogProvider.Claude &&
                 !string.IsNullOrWhiteSpace(request.ApiKey))
        {
            httpRequest.Headers.TryAddWithoutValidation("x-api-key", request.ApiKey);
            httpRequest.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        }
        else if (!string.IsNullOrWhiteSpace(request.ApiKey))
        {
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.ApiKey);
        }

        using var client = _clientFactory.CreateHttpClient();
        using var response = await client.SendAsync(httpRequest, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return ExtractModelIds(document.RootElement)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<string> ExtractModelIds(JsonElement root)
    {
        var models = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data)
            ? data
            : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("models", out var providerModels)
                ? providerModels
                : root;
        if (models.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var item in models.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var value = item.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                    yield return value;
                continue;
            }

            if (item.ValueKind != JsonValueKind.Object ||
                !(item.TryGetProperty("id", out var id) || item.TryGetProperty("name", out id)))
            {
                continue;
            }

            var valueFromObject = id.GetString();
            if (string.IsNullOrWhiteSpace(valueFromObject))
                continue;
            yield return valueFromObject.StartsWith("models/", StringComparison.OrdinalIgnoreCase)
                ? valueFromObject["models/".Length..]
                : valueFromObject;
        }
    }
}
