using EasyChat.Contracts.Translation;
using EasyChat.Infrastructure.Translation.Baidu;
using EasyChat.Infrastructure.Translation.DeepL;
using EasyChat.Infrastructure.Translation.Google;
using EasyChat.Infrastructure.Translation.OpenAi;
using EasyChat.Infrastructure.Translation.Tencent;
using Microsoft.Extensions.Logging;

namespace EasyChat.Infrastructure.Translation;

public sealed class TranslationProviderFactory : ITranslationProviderFactory
{
    private readonly ILoggerFactory _loggerFactory;

    public TranslationProviderFactory(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    }

    public IChatTranslationProvider Create(AiTranslationProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var provider = options.Provider;
        return new OpenAiTranslationProvider(
            provider.ApiUrl,
            provider.ApiKey,
            provider.Model,
            options.ProxyUrl,
            provider.EnableThinking);
    }

    public ITranslationProvider Create(MachineTranslationProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var provider = options.Provider;
        return provider switch
        {
            BaiduTranslationProviderConfiguration baidu => new BaiduTranslationProvider(
                baidu.AppId,
                baidu.AppKey,
                options.ProxyUrl,
                () => options.RequestErrorMessage,
                _loggerFactory.CreateLogger<BaiduTranslationProvider>()),
            TencentTranslationProviderConfiguration tencent => new TencentTranslationProvider(
                tencent.SecretId,
                tencent.SecretKey,
                options.ProxyUrl,
                () => options.RequestErrorMessage,
                _loggerFactory.CreateLogger<TencentTranslationProvider>()),
            GoogleTranslationProviderConfiguration google => new GoogleTranslationProvider(
                google.Model,
                google.ApiKey,
                options.ProxyUrl,
                () => options.RequestErrorMessage,
                _loggerFactory.CreateLogger<GoogleTranslationProvider>()),
            DeepLTranslationProviderConfiguration deepL => new DeepLTranslationProvider(
                deepL.ModelType,
                deepL.ApiKey,
                options.ProxyUrl),
            _ => throw new ArgumentException(
                $"Unknown machine translation provider: {provider.Name}",
                nameof(options))
        };
    }
}
