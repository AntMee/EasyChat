using System.Text;
using EasyChat.Contracts.Settings;
using EasyChat.Contracts.Speech;
using EasyChat.Contracts.Translation;
using Microsoft.Extensions.Logging;

namespace EasyChat.Application.Speech;

internal sealed class SubtitleTranslationCoordinator
{
    private static readonly char[] Terminators = ['.', '?', '!', ';', '。', '？', '！', '；'];
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan TranslationTimeout = TimeSpan.FromSeconds(15);
    private const int DisplayUpdateDebounceMilliseconds = 80;
    private const string RealtimePrompt =
        "Translate [SourceLang] to [TargetLang]. Return only the natural translation. Do not explain, analyze, or reason.";

    private readonly object _sync = new();
    private readonly Dictionary<SubtitleLineState, PendingTranslation> _pending = [];
    private readonly Dictionary<SubtitleLineState, DateTimeOffset> _lastDisplayUpdates = [];
    private readonly Func<SpeechRecognitionSettings> _getSettings;
    private readonly ITranslationUseCases _translation;
    private readonly ITranslationLanguageCatalog _languages;
    private readonly Action<SubtitleLineState> _publish;
    private readonly ILogger _logger;
    private readonly CancellationToken _sessionToken;

    public SubtitleTranslationCoordinator(
        Func<SpeechRecognitionSettings> getSettings,
        ITranslationUseCases translation,
        ITranslationLanguageCatalog languages,
        Action<SubtitleLineState> publish,
        ILogger logger,
        CancellationToken sessionToken)
    {
        _getSettings = getSettings;
        _translation = translation;
        _languages = languages;
        _publish = publish;
        _logger = logger;
        _sessionToken = sessionToken;
    }

    public Task QueueAsync(SubtitleLineState line, string text, bool isFinal)
    {
        lock (_sync)
        {
            line.IsTranslating = true;
            if (!_pending.TryGetValue(line, out var pending))
            {
                pending = new PendingTranslation();
                _pending.Add(line, pending);
            }
            pending.Text = text;
            pending.UpdatedAt = DateTimeOffset.UtcNow;
            pending.IsFinal |= isFinal;
            pending.Runner ??= Task.Run(() => RunAsync(line, pending));
        }
        _publish(line);
        return Task.CompletedTask;
    }

    public async Task CompleteAsync()
    {
        while (true)
        {
            Task[] tasks;
            lock (_sync)
                tasks = _pending.Values.Select(value => value.Runner).OfType<Task>().ToArray();
            if (tasks.Length == 0)
                return;
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
    }

    private async Task RunAsync(SubtitleLineState line, PendingTranslation pending)
    {
        try
        {
            while (!_sessionToken.IsCancellationRequested)
            {
                string text;
                bool isFinal;
                DateTimeOffset updatedAt;
                lock (_sync)
                {
                    text = pending.Text;
                    isFinal = pending.IsFinal;
                    pending.IsFinal = false;
                    updatedAt = pending.UpdatedAt;
                }

                if (!isFinal && !HasUntranslatedTerminator(line, text))
                {
                    var remaining = QuietPeriod - (DateTimeOffset.UtcNow - updatedAt);
                    if (remaining > TimeSpan.Zero)
                        await Task.Delay(remaining, _sessionToken).ConfigureAwait(false);
                    lock (_sync)
                    {
                        if (pending.Text != text || pending.UpdatedAt != updatedAt)
                            continue;
                    }
                    isFinal = true;
                }

                await TranslateAsync(line, text, isFinal).ConfigureAwait(false);
                lock (_sync)
                {
                    if (pending.Text == text)
                    {
                        _pending.Remove(line);
                        return;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_sessionToken.IsCancellationRequested)
        {
        }
        finally
        {
            lock (_sync)
                _pending.Remove(line);
            if (!_sessionToken.IsCancellationRequested)
            {
                line.IsTranslating = false;
                _publish(line);
            }
        }
    }

    private async Task TranslateAsync(SubtitleLineState line, string text, bool isFinal)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
        try
        {
            if (!text.StartsWith(line.ConfirmedOriginalText, StringComparison.Ordinal))
            {
                line.ConfirmedOriginalText = string.Empty;
                line.ConfirmedTranslatedText = string.Empty;
            }

            var delta = text[line.ConfirmedOriginalText.Length..];
            if (delta.Length == 0)
                return;
            var lastTerminator = delta.LastIndexOfAny(Terminators);
            var stable = lastTerminator < 0 ? string.Empty : delta[..(lastTerminator + 1)];
            var unstable = lastTerminator < 0 ? delta : delta[(lastTerminator + 1)..];
            var selection = CreateProviderSelection();
            var settings = _getSettings();
            var source = _languages.Get(MapRecognitionLanguage(settings.RecognitionLanguage));
            var target = _languages.Get(settings.TargetLanguage);

            if (stable.Length > 0)
            {
                var translated = await TranslatePartAsync(
                    line,
                    stable,
                    line.ConfirmedTranslatedText,
                    source,
                    target,
                    selection,
                    streamDisplay: true).ConfigureAwait(false);
                if (translated.Length > 0)
                {
                    line.ConfirmedOriginalText += stable;
                    line.ConfirmedTranslatedText += translated;
                    line.TranslatedText = line.ConfirmedTranslatedText;
                    line.DisplayTranslatedText = line.ConfirmedTranslatedText;
                    _publish(line);
                }
            }

            if (!isFinal)
                return;
            if (unstable.Length > 0)
            {
                var translated = await TranslatePartAsync(
                    line,
                    unstable,
                    line.ConfirmedTranslatedText,
                    source,
                    target,
                    selection,
                    settings.IsRealTimePreviewEnabled).ConfigureAwait(false);
                var finalText = line.ConfirmedTranslatedText + translated;
                line.TranslatedText = finalText;
                if (!string.IsNullOrWhiteSpace(finalText) || string.IsNullOrWhiteSpace(line.DisplayTranslatedText))
                    line.DisplayTranslatedText = finalText;
                _publish(line);
            }
            else if (stable.Length == 0 && line.ConfirmedTranslatedText.Length > 0)
            {
                line.DisplayTranslatedText = line.ConfirmedTranslatedText;
                _publish(line);
            }
        }
        catch (OperationCanceledException) when (_sessionToken.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Subtitle translation timed out.");
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Subtitle translation failed.");
            if (!_sessionToken.IsCancellationRequested)
            {
                line.TranslatedText += $" (Error: {exception.Message})";
                _publish(line);
            }
        }
    }

    private async Task<string> TranslatePartAsync(
        SubtitleLineState line,
        string text,
        string prefix,
        TranslationLanguage source,
        TranslationLanguage target,
        TranslationProviderSelection selection,
        bool streamDisplay)
    {
        using var timeout = new CancellationTokenSource(TranslationTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_sessionToken, timeout.Token);
        var builder = new StringBuilder();
        var session = _translation.Prepare(selection);
        using var disposable = session as IDisposable;
        await foreach (var item in session.StreamAsync(
                           new TranslationRequest(text, source, target, Provider: selection),
                           linked.Token).ConfigureAwait(false))
        {
            if (item is not TranslationDeltaEvent { Text.Length: > 0 } delta)
                continue;
            builder.Append(delta.Text);
            var current = prefix + builder;
            line.TranslatedText = current;
            if (streamDisplay)
                UpdateDisplayWithDebounce(line, current);
            _publish(line);
        }
        return builder.ToString();
    }

    private void UpdateDisplayWithDebounce(SubtitleLineState line, string text)
    {
        if (text.Length == 0)
            return;
        var now = DateTimeOffset.UtcNow;
        lock (_sync)
        {
            if (_lastDisplayUpdates.TryGetValue(line, out var last)
                && (now - last).TotalMilliseconds < DisplayUpdateDebounceMilliseconds)
            {
                return;
            }
            _lastDisplayUpdates[line] = now;
        }
        if (line.DisplayTranslatedText.StartsWith(text, StringComparison.Ordinal)
            && line.DisplayTranslatedText.Length > text.Length)
        {
            return;
        }
        line.DisplayTranslatedText = text;
    }

    private bool HasUntranslatedTerminator(SubtitleLineState line, string text)
    {
        var start = text.StartsWith(line.ConfirmedOriginalText, StringComparison.Ordinal)
            ? line.ConfirmedOriginalText.Length
            : 0;
        return text.IndexOfAny(Terminators, start) >= 0;
    }

    private TranslationProviderSelection CreateProviderSelection()
    {
        var settings = _getSettings();
        return settings.EngineType == 0
            ? new TranslationProviderSelection(
                TranslationEngineNames.MachineTrans,
                MachineProviderId: settings.EngineId)
            : new TranslationProviderSelection(
                TranslationEngineNames.AiModel,
                AiModelId: settings.EngineId,
                PromptOverride: RealtimePrompt);
    }

    private static string MapRecognitionLanguage(string modelName)
    {
        if (modelName.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            return "zh-Hans";
        if (modelName.StartsWith("en", StringComparison.OrdinalIgnoreCase))
            return "en";
        if (modelName.StartsWith("ja", StringComparison.OrdinalIgnoreCase))
            return "ja";
        if (modelName.StartsWith("ko", StringComparison.OrdinalIgnoreCase))
            return "ko";
        return "auto";
    }

    private sealed class PendingTranslation
    {
        public string Text { get; set; } = string.Empty;
        public DateTimeOffset UpdatedAt { get; set; }
        public bool IsFinal { get; set; }
        public Task? Runner { get; set; }
    }
}
