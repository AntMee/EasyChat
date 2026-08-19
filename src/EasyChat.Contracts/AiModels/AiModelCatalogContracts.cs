namespace EasyChat.Contracts.AiModels;

public enum AiModelCatalogProvider
{
    OpenAiCompatible,
    Google,
    Claude
}

public sealed record AiModelCatalogRequest(
    string ApiUrl,
    string ApiKey,
    AiModelCatalogProvider Provider);

public interface IAiModelCatalogTransport
{
    Task<IReadOnlyList<string>> FetchModelsAsync(
        AiModelCatalogRequest request,
        CancellationToken cancellationToken = default);
}
