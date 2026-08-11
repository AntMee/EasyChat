using EasyChat.Contracts.Settings;

namespace EasyChat.Contracts.Translation;

public static class MachineTranslationProviderNames
{
    public const string Baidu = "Baidu";
    public const string Tencent = "Tencent";
    public const string Google = "Google";
    public const string DeepL = "DeepL";
}

public sealed record AiTranslationProviderConfiguration(
    string Id,
    string Name,
    string ModelType,
    string ApiUrl,
    string ApiKey,
    string Model,
    bool UseProxy,
    bool EnableThinking);

public abstract record MachineTranslationProviderConfiguration(
    string Id,
    string Name,
    bool UseProxy);

public sealed record BaiduTranslationProviderConfiguration(
    string Id,
    bool UseProxy,
    string AppId,
    string AppKey)
    : MachineTranslationProviderConfiguration(
        Id,
        MachineTranslationProviderNames.Baidu,
        UseProxy);

public sealed record TencentTranslationProviderConfiguration(
    string Id,
    bool UseProxy,
    string SecretId,
    string SecretKey)
    : MachineTranslationProviderConfiguration(
        Id,
        MachineTranslationProviderNames.Tencent,
        UseProxy);

public sealed record GoogleTranslationProviderConfiguration(
    string Id,
    bool UseProxy,
    string Model,
    string ApiKey)
    : MachineTranslationProviderConfiguration(
        Id,
        MachineTranslationProviderNames.Google,
        UseProxy);

public sealed record DeepLTranslationProviderConfiguration(
    string Id,
    bool UseProxy,
    string ModelType,
    string ApiKey)
    : MachineTranslationProviderConfiguration(
        Id,
        MachineTranslationProviderNames.DeepL,
        UseProxy);

public sealed record TranslationProxyOptions(NetworkProxyMode Mode, string? ProxyUrl)
{
    public static TranslationProxyOptions Direct { get; } = new(NetworkProxyMode.None, null);

    public static TranslationProxyOptions FromLegacyUrl(string? proxyUrl) => new(
        string.IsNullOrWhiteSpace(proxyUrl) ? NetworkProxyMode.None : NetworkProxyMode.Custom,
        proxyUrl);
}

public sealed record AiTranslationProviderOptions(
    AiTranslationProviderConfiguration Provider,
    string? ProxyUrl)
{
    public TranslationProxyOptions Proxy { get; init; } =
        TranslationProxyOptions.FromLegacyUrl(ProxyUrl);

    public static AiTranslationProviderOptions WithProxy(
        AiTranslationProviderConfiguration provider,
        TranslationProxyOptions proxy) => new(provider, proxy.ProxyUrl) { Proxy = proxy };
}

public sealed record MachineTranslationProviderOptions(
    MachineTranslationProviderConfiguration Provider,
    string? ProxyUrl,
    string RequestErrorMessage)
{
    public TranslationProxyOptions Proxy { get; init; } =
        TranslationProxyOptions.FromLegacyUrl(ProxyUrl);

    public static MachineTranslationProviderOptions WithProxy(
        MachineTranslationProviderConfiguration provider,
        TranslationProxyOptions proxy,
        string requestErrorMessage) =>
        new(provider, proxy.ProxyUrl, requestErrorMessage) { Proxy = proxy };
}

/// <summary>
/// Creates technology-specific providers from choices already resolved by Application.
/// </summary>
public interface ITranslationProviderFactory
{
    IChatTranslationProvider Create(AiTranslationProviderOptions options);

    ITranslationProvider Create(MachineTranslationProviderOptions options);
}
