using EasyChat.Contracts.Translation;
using Microsoft.Extensions.Logging;

namespace EasyChat.Infrastructure.Translation;

public sealed class LoggingTranslationFailureSink : ITranslationFailureSink
{
    private readonly ILogger<LoggingTranslationFailureSink> _logger;

    public LoggingTranslationFailureSink(ILogger<LoggingTranslationFailureSink> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void Report(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        _logger.LogError(exception, "Translation failed.");
    }
}
