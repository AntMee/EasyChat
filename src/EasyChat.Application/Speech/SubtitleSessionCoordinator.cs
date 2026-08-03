using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using EasyChat.Contracts.Platform;
using EasyChat.Contracts.Settings;
using EasyChat.Contracts.Speech;
using EasyChat.Contracts.Translation;
using Microsoft.Extensions.Logging;

namespace EasyChat.Application.Speech;

internal sealed class SubtitleSessionCoordinator
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan PreviewDebounce = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan PreviewMaximumWait = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan DisplayUpdateInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan TranslationTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DuplicateFinalWindow = TimeSpan.FromMilliseconds(500);
    private const int TranslationQueueCapacity = 32;
    private const string SubtitlePrompt =
        "Translate live subtitles from [SourceLang] to [TargetLang]. "
        + "The user content is JSON with context and current fields. "
        + "Use context only to resolve meaning and translate only current. "
        + "Preserve an incomplete ending instead of inventing missing speech. "
        + "Return only the concise natural translation without explanation.";

    private readonly Func<SpeechRecognitionSettings> _getSettings;
    private readonly ITranslationUseCases _translation;
    private readonly ITranslationLanguageCatalog _languages;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _aiTranslationGate;
    private readonly SemaphoreSlim _machineTranslationGate;
    private readonly long _startTimestamp;
    private readonly Func<long> _nextSubtitleId;
    private readonly Action<SpeechSessionEvent> _publish;
    private readonly IncrementalSubtitleSegmenter _segmenter = new();
    private readonly List<ManagedSubtitleLine> _floating = [];
    private readonly List<ManagedSubtitleLine> _sealedLines = [];
    private readonly List<UtteranceLineRange> _utteranceLines = [];
    private readonly LinkedList<TranslationJob> _finalJobs = [];
    private readonly Dictionary<long, ManagedSubtitleLine> _linesById = [];
    private readonly Channel<SessionMessage> _inbox = Channel.CreateBounded<SessionMessage>(
        new BoundedChannelOptions(256)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });

    private ManagedSubtitleLine? _currentLine;
    private UtteranceLineRange? _currentRange;
    private ManagedSubtitleLine? _lastUtteranceLine;
    private TranslationJob? _pendingPreview;
    private TranslationJob? _activeTranslation;
    private string _utteranceHypothesis = string.Empty;
    private string _lastFinalText = string.Empty;
    private TimeSpan? _lastFinalAt;
    private int _sentencesInCurrent;
    private long _nextTranslationJobId;
    private bool _recognitionStopped;
    private bool _stoppedPublished;
    private bool _hasPartialSinceFinal;
    private bool _started;

    public SubtitleSessionCoordinator(
        Func<SpeechRecognitionSettings> getSettings,
        ITranslationUseCases translation,
        ITranslationLanguageCatalog languages,
        ILogger logger,
        TimeProvider timeProvider,
        Func<long> nextSubtitleId,
        Action<SpeechSessionEvent> publish,
        SemaphoreSlim? aiTranslationGate = null,
        SemaphoreSlim? machineTranslationGate = null)
    {
        _getSettings = getSettings ?? throw new ArgumentNullException(nameof(getSettings));
        _translation = translation ?? throw new ArgumentNullException(nameof(translation));
        _languages = languages ?? throw new ArgumentNullException(nameof(languages));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _aiTranslationGate = aiTranslationGate ?? new SemaphoreSlim(1, 1);
        _machineTranslationGate = machineTranslationGate ?? new SemaphoreSlim(1, 1);
        _startTimestamp = _timeProvider.GetTimestamp();
        _nextSubtitleId = nextSubtitleId ?? throw new ArgumentNullException(nameof(nextSubtitleId));
        _publish = publish ?? throw new ArgumentNullException(nameof(publish));
    }

    public async Task RunAsync(
        IAsyncEnumerable<SpeechRecognitionEvent> recognition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recognition);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var recognitionPump = PumpRecognitionAsync(recognition, lifetime.Token);
        var tickPump = PumpTicksAsync(lifetime.Token);
        try
        {
            while (await _inbox.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (_inbox.Reader.TryRead(out var message))
                    await HandleAsync(message, lifetime.Token).ConfigureAwait(false);

                TryStartTranslation(lifetime.Token);
                if (_recognitionStopped && !HasTranslationWork)
                {
                    if (!_stoppedPublished)
                    {
                        _stoppedPublished = true;
                        _publish(new SpeechSessionStoppedEvent());
                    }
                    if (!HasPendingFloatingExpiry)
                        return;
                }
            }
        }
        finally
        {
            lifetime.Cancel();
            CancelAndDetachActiveTranslation();
            CancelPendingJobs();
            await IgnoreCancellationAsync(recognitionPump, lifetime.Token).ConfigureAwait(false);
            await IgnoreCancellationAsync(tickPump, lifetime.Token).ConfigureAwait(false);
        }
    }

    private bool HasTranslationWork =>
        _activeTranslation is not null || _pendingPreview is not null || _finalJobs.Count > 0;

    private bool HasPendingFloatingExpiry =>
        _floating.Any(line => line.IsFloatingVisible && line.ExpiresAt is not null);

    private async Task PumpRecognitionAsync(
        IAsyncEnumerable<SpeechRecognitionEvent> recognition,
        CancellationToken cancellationToken)
    {
        var stopped = false;
        try
        {
            await foreach (var item in recognition.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                await _inbox.Writer.WriteAsync(new RecognitionMessage(item), cancellationToken)
                    .ConfigureAwait(false);
                if (item.Kind == SpeechRecognitionEventKind.Stopped)
                {
                    stopped = true;
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            await _inbox.Writer.WriteAsync(new RecognitionFailureMessage(exception), cancellationToken)
                .ConfigureAwait(false);
        }

        if (!stopped && !cancellationToken.IsCancellationRequested)
        {
            await _inbox.Writer.WriteAsync(
                    new RecognitionMessage(new SpeechRecognitionEvent(SpeechRecognitionEventKind.Stopped)),
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task PumpTicksAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TickInterval, _timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await _inbox.Writer.WriteAsync(
                        new TickMessage(GetMonotonicNow()),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task HandleAsync(SessionMessage message, CancellationToken cancellationToken)
    {
        var now = GetMonotonicNow();
        if (!_getSettings().IsTranslationEnabled)
            CancelDisabledTranslationWork(now);

        switch (message)
        {
            case RecognitionMessage recognition:
                HandleRecognition(recognition.Event, now);
                break;
            case RecognitionFailureMessage failure:
                _logger.LogError(failure.Exception, "Speech recognition event pump failed.");
                _publish(new SpeechSessionErrorEvent(failure.Exception.Message));
                HandleRecognition(
                    new SpeechRecognitionEvent(SpeechRecognitionEventKind.Stopped),
                    now);
                break;
            case TickMessage tick:
                HandleTick(tick.Now);
                break;
            case TranslationBufferMessage buffer:
                HandleTranslationBuffer(buffer, now);
                break;
            case TranslationCompletedMessage completed:
                HandleTranslationCompleted(completed, now);
                break;
        }
        await Task.CompletedTask;
    }

    private void HandleRecognition(SpeechRecognitionEvent item, TimeSpan now)
    {
        switch (item.Kind)
        {
            case SpeechRecognitionEventKind.Started:
                if (!_started)
                {
                    _started = true;
                    _publish(new SpeechSessionStartedEvent());
                }
                break;
            case SpeechRecognitionEventKind.Partial:
                _hasPartialSinceFinal = true;
                ApplySegmentation(_segmenter.ApplyPartial(item.Text ?? string.Empty, now), now);
                break;
            case SpeechRecognitionEventKind.Final:
                HandleFinal(item.Text ?? string.Empty, now);
                break;
            case SpeechRecognitionEventKind.Error:
                _publish(new SpeechSessionErrorEvent(item.Text ?? string.Empty));
                break;
            case SpeechRecognitionEventKind.Stopped:
                if (_recognitionStopped)
                    return;
                ApplySegmentation(_segmenter.CompleteLatest(), now);
                SealCurrentLine(now);
                _recognitionStopped = true;
                CancelPendingPreview();
                if (_activeTranslation is { IsFinal: false })
                    CancelAndDetachActiveTranslation();
                break;
        }
    }

    private void HandleTick(TimeSpan now)
    {
        if (!_recognitionStopped)
            ApplySegmentation(_segmenter.Tick(now), now);
        SchedulePreview(now);
        FlushBufferedTranslation(now);
        ExpireFloatingLines(now);
    }

    private void HandleFinal(string text, TimeSpan now)
    {
        var normalized = IncrementalSubtitleSegmenter.Normalize(text);
        if (normalized.Length > 0
            && !_hasPartialSinceFinal
            && string.Equals(normalized, _lastFinalText, StringComparison.Ordinal)
            && _lastFinalAt is { } lastFinalAt
            && now - lastFinalAt <= DuplicateFinalWindow)
        {
            _lastFinalAt = now;
            return;
        }

        if (normalized.Length > _lastFinalText.Length
            && !_hasPartialSinceFinal
            && _lastFinalAt is { } suffixFinalAt
            && now - suffixFinalAt <= DuplicateFinalWindow
            && normalized.StartsWith(_lastFinalText, StringComparison.Ordinal)
            && IncrementalSubtitleSegmenter.IsOnlyTerminalSuffix(
                normalized[_lastFinalText.Length..]))
        {
            AppendTerminalPunctuation(normalized[_lastFinalText.Length..], now);
            _lastFinalAt = now;
            return;
        }

        if (normalized.Length > 0
            && IncrementalSubtitleSegmenter.IsOnlyTerminalSuffix(normalized))
        {
            AppendTerminalPunctuation(normalized, now);
            _lastFinalAt = now;
            return;
        }

        ApplySegmentation(_segmenter.ApplyFinal(normalized, now), now);
        _lastFinalText = normalized;
        _lastFinalAt = now;
        _hasPartialSinceFinal = false;
    }

    private void ApplySegmentation(SubtitleSegmentationUpdate update, TimeSpan now)
    {
        if (update.ReconcileFinal)
        {
            ReconcileFinalHypothesis(update.Hypothesis, update.PreviousHypothesis, now);
            return;
        }


        if (update.StartsNewUtterance)
            CompleteUtterance();

        if (update.Hypothesis.Length > 0)
            _utteranceHypothesis = update.Hypothesis;

        foreach (var commit in update.Commits)
        {
            var start = commit.SourceStart >= 0
                ? commit.SourceStart
                : _currentRange?.Start ?? 0;
            var line = EnsureCurrentLine(now, start);
            var range = _currentRange!;
            range.End = commit.SourceEnd >= 0
                ? Math.Clamp(commit.SourceEnd, range.Start, _utteranceHypothesis.Length)
                : Math.Clamp(start + commit.Text.Length, range.Start, _utteranceHypothesis.Length);
            _sentencesInCurrent += Math.Max(1, commit.SentenceCount);
            UpdateSource(line, SliceHypothesis(range), now);
            PublishLine(line);
            if (commit.CloseLine
                || _sentencesInCurrent >= Math.Max(1, _getSettings().MaxSentencesPerLine))
            {
                SealCurrentLine(now);
            }
        }

        if (!string.IsNullOrEmpty(update.AppendToPreviousLine) && _lastUtteranceLine is not null)
        {
            UpdateSource(
                _lastUtteranceLine,
                JoinText(_lastUtteranceLine.OriginalText, update.AppendToPreviousLine),
                now);
            PublishLine(_lastUtteranceLine);
            QueueFinalTranslation(_lastUtteranceLine, now);
        }

        if (update.DraftText.Length > 0)
        {
            var line = EnsureCurrentLine(now, update.DraftStart);
            var range = _currentRange!;
            range.End = _utteranceHypothesis.Length;
            UpdateSource(line, SliceHypothesis(range), now);
            PublishLine(line);
        }
        else if (_currentLine is not null && _currentRange is not null)
        {
            UpdateSource(_currentLine, SliceHypothesis(_currentRange), now);
            PublishLine(_currentLine);
        }

        if (update.CloseCurrentLine)
            SealCurrentLine(now);
        if (update.EndsUtterance)
            CompleteUtterance();
    }

    private ManagedSubtitleLine EnsureCurrentLine(TimeSpan now, int sourceStart)
    {
        if (_currentLine is not null)
            return _currentLine;
        var timestamp = _timeProvider.GetLocalNow().TimeOfDay;
        _currentLine = new ManagedSubtitleLine(_nextSubtitleId(), timestamp, now);
        _currentRange = new UtteranceLineRange(
            _currentLine,
            Math.Clamp(sourceStart, 0, _utteranceHypothesis.Length),
            Math.Clamp(sourceStart, 0, _utteranceHypothesis.Length));
        _utteranceLines.Add(_currentRange);
        _linesById.Add(_currentLine.Id, _currentLine);
        _floating.Add(_currentLine);
        TrimFloatingHistory();
        return _currentLine;
    }

    private string SliceHypothesis(UtteranceLineRange range)
    {
        var start = Math.Clamp(range.Start, 0, _utteranceHypothesis.Length);
        var end = Math.Clamp(range.End, start, _utteranceHypothesis.Length);
        range.Start = IncrementalSubtitleSegmenter.SkipWhitespace(
            _utteranceHypothesis,
            start,
            end);
        range.End = IncrementalSubtitleSegmenter.TrimTrailingWhitespace(
            _utteranceHypothesis,
            range.Start,
            end);
        range.Line.SourceStart = range.Start;
        range.Line.SourceEnd = range.End;
        return _utteranceHypothesis[range.Start..range.End];
    }

    private void UpdateSource(
        ManagedSubtitleLine line,
        string text,
        TimeSpan now)
    {
        text = IncrementalSubtitleSegmenter.Normalize(text);
        if (string.Equals(line.OriginalText, text, StringComparison.Ordinal))
            return;

        var previous = line.OriginalText;
        line.OriginalText = text;
        line.Revision++;
        line.LastSourceChangedAt = now;
        line.ExpiresAt = null;
        line.IsTranslationTerminal = false;
        line.TranslationDefinition = null;
        if (IsPreviewEligible(text)
            && !string.Equals(line.LastPreviewRequestedSource, text, StringComparison.Ordinal))
        {
            line.PreviewEligibleAt ??= now;
        }
        else
            line.PreviewEligibleAt = null;

        if (_activeTranslation is { IsFinal: false } active
            && active.LineId == line.Id
            && !text.StartsWith(active.SourceText, StringComparison.Ordinal))
        {
            CancelAndDetachActiveTranslation();
        }
        if (_pendingPreview is not null
            && _pendingPreview.LineId == line.Id
            && !text.StartsWith(_pendingPreview.SourceText, StringComparison.Ordinal))
        {
            CancelPendingPreview();
        }

        if (!text.StartsWith(previous, StringComparison.Ordinal))
        {
            line.LastPreviewRequestedSource = string.Empty;
            line.ShadowTranslation = string.Empty;
            line.ShadowTranslationSource = string.Empty;
            line.ShadowTranslationDefinition = null;
            if (line.LastTranslatedSource.Length > 0
                && !text.StartsWith(line.LastTranslatedSource, StringComparison.Ordinal))
            {
                line.TranslatedText = string.Empty;
                line.DisplayTranslatedText = string.Empty;
                line.LastTranslatedSource = string.Empty;
                line.LastTranslationDefinition = null;
            }
        }
    }

    private void SealCurrentLine(TimeSpan now)
    {
        var line = _currentLine;
        if (line is null)
            return;
        if (string.IsNullOrWhiteSpace(line.OriginalText))
        {
            if (_currentRange is not null)
                _utteranceLines.Remove(_currentRange);
            _currentLine = null;
            _currentRange = null;
            _sentencesInCurrent = 0;
            return;
        }

        line.IsSealed = true;
        line.IsTemporary = false;
        line.PreviewEligibleAt = null;
        if (!_sealedLines.Contains(line))
            _sealedLines.Add(line);
        _lastUtteranceLine = line;
        _currentLine = null;
        _currentRange = null;
        _sentencesInCurrent = 0;
        PublishLine(line);
        QueueFinalTranslation(line, now);
    }

    private void CompleteUtterance()
    {
        if (_utteranceLines.Count > 0)
            _lastUtteranceLine = _utteranceLines[^1].Line;
        _utteranceLines.Clear();
        _utteranceHypothesis = string.Empty;
        _currentLine = null;
        _currentRange = null;
        _sentencesInCurrent = 0;
    }

    private void AppendTerminalPunctuation(string punctuation, TimeSpan now)
    {
        var target = _currentLine
                     ?? _utteranceLines.LastOrDefault()?.Line
                     ?? _lastUtteranceLine;
        if (target is null)
        {
            _segmenter.Reset();
            _lastFinalText = punctuation;
            _hasPartialSinceFinal = false;
            return;
        }

        var overlap = FindSuffixOverlap(target.OriginalText, punctuation);
        var append = punctuation[overlap..];
        if (append.Length > 0)
        {
            UpdateSource(target, target.OriginalText + append, now);
            target.SourceEnd += append.Length;
        }

        if (ReferenceEquals(target, _currentLine))
        {
            SealCurrentLine(now);
        }
        else
        {
            target.IsSealed = true;
            target.IsTemporary = false;
            target.PreviewEligibleAt = null;
            if (!_sealedLines.Contains(target))
                _sealedLines.Add(target);
            PublishLine(target);
            QueueFinalTranslation(target, now);
        }

        _lastUtteranceLine = target;
        _lastFinalText = target.OriginalText;
        _hasPartialSinceFinal = false;
        _segmenter.Reset();
        CompleteUtterance();
    }

    private static int FindSuffixOverlap(string text, string suffix)
    {
        for (var length = Math.Min(text.Length, suffix.Length); length > 0; length--)
        {
            if (text.EndsWith(suffix[..length], StringComparison.Ordinal))
                return length;
        }
        return 0;
    }

    private void ReconcileFinalHypothesis(
        string finalHypothesis,
        string previousHypothesis,
        TimeSpan now)
    {
        finalHypothesis = IncrementalSubtitleSegmenter.Normalize(finalHypothesis);
        if (finalHypothesis.Length == 0)
        {
            SealCurrentLine(now);
            CompleteUtterance();
            return;
        }

        var prior = _utteranceHypothesis.Length > 0
            ? _utteranceHypothesis
            : previousHypothesis;
        var commonPrefix = IncrementalSubtitleSegmenter.AlignPrefixToTextElement(
            finalHypothesis,
            FindOrdinalPrefixLength(prior, finalHypothesis));
        var firstAffected = _utteranceLines.FindIndex(range =>
            !range.Line.IsSealed
            || range.End > commonPrefix
            || !RangeMatchesHypothesis(range, finalHypothesis));
        var preservedCount = firstAffected < 0 ? _utteranceLines.Count : firstAffected;
        var preserved = _utteranceLines.Take(preservedCount).ToList();
        var affected = _utteranceLines.Skip(preservedCount).ToList();
        _utteranceHypothesis = finalHypothesis;
        var rebuildStart = preserved.Count == 0
            ? 0
            : IncrementalSubtitleSegmenter.SkipWhitespace(
                finalHypothesis,
                preserved[^1].End,
                finalHypothesis.Length);
        var desired = BuildFinalLineRanges(
            finalHypothesis,
            rebuildStart,
            Math.Max(1, _getSettings().MaxSentencesPerLine));
        if (preserved.Count > 0
            && desired.Count == 1
            && IncrementalSubtitleSegmenter.IsOnlyTerminalSuffix(
                finalHypothesis[desired[0].Start..desired[0].End]))
        {
            var lastPreserved = preserved[^1];
            lastPreserved.End = desired[0].End;
            UpdateSource(
                lastPreserved.Line,
                SliceHypothesis(lastPreserved),
                now);
            desired.Clear();
        }

        var rebuilt = new List<UtteranceLineRange>(preserved);
        for (var index = 0; index < desired.Count; index++)
        {
            var source = desired[index];
            UtteranceLineRange binding;
            if (index < affected.Count)
            {
                binding = affected[index];
                binding.Start = source.Start;
                binding.End = source.End;
            }
            else
            {
                var line = CreateLine(now);
                binding = new UtteranceLineRange(line, source.Start, source.End);
            }
            rebuilt.Add(binding);
            UpdateSource(
                binding.Line,
                SliceHypothesis(binding),
                now);
        }

        foreach (var obsolete in affected.Skip(desired.Count))
        {
            RemoveQueuedTranslations(obsolete.Line.Id);
            if (_pendingPreview?.LineId == obsolete.Line.Id)
                CancelPendingPreview();
            if (_activeTranslation?.LineId == obsolete.Line.Id)
                CancelAndDetachActiveTranslation();
            UpdateSource(obsolete.Line, string.Empty, now);
            obsolete.Line.IsSealed = true;
            obsolete.Line.IsTemporary = false;
            obsolete.Line.IsTranslating = false;
            PublishLine(obsolete.Line);
            RemoveFromFloating(obsolete.Line);
            _floating.Remove(obsolete.Line);
            _sealedLines.Remove(obsolete.Line);
            _linesById.Remove(obsolete.Line.Id);
        }

        _utteranceLines.Clear();
        _utteranceLines.AddRange(rebuilt);
        _currentLine = null;
        _currentRange = null;
        _sentencesInCurrent = 0;

        foreach (var binding in rebuilt)
        {
            var line = binding.Line;
            line.IsSealed = true;
            line.IsTemporary = false;
            line.PreviewEligibleAt = null;
            if (!_sealedLines.Contains(line))
                _sealedLines.Add(line);
            PublishLine(line);
        }
        foreach (var binding in rebuilt)
            QueueFinalTranslation(binding.Line, now);

        _lastUtteranceLine = rebuilt.LastOrDefault()?.Line ?? _lastUtteranceLine;
        _lastFinalText = finalHypothesis;
        _hasPartialSinceFinal = false;
        CompleteUtterance();
    }

    private static int FindOrdinalPrefixLength(string left, string right)
    {
        var length = Math.Min(left.Length, right.Length);
        var index = 0;
        while (index < length && left[index] == right[index])
            index++;
        return index;
    }

    private static bool RangeMatchesHypothesis(
        UtteranceLineRange range,
        string hypothesis) =>
        range.Start >= 0
        && range.End >= range.Start
        && range.End <= hypothesis.Length
        && string.Equals(
            range.Line.OriginalText,
            hypothesis[range.Start..range.End],
            StringComparison.Ordinal);

    private ManagedSubtitleLine CreateLine(TimeSpan now)
    {
        var line = new ManagedSubtitleLine(
            _nextSubtitleId(),
            _timeProvider.GetLocalNow().TimeOfDay,
            now);
        _linesById.Add(line.Id, line);
        _floating.Add(line);
        TrimFloatingHistory();
        return line;
    }

    private static List<SubtitleSourceRange> BuildFinalLineRanges(
        string text,
        int sourceStart,
        int maximumSentences)
    {
        var ranges = new List<SubtitleSourceRange>();
        var lineStart = IncrementalSubtitleSegmenter.SkipWhitespace(
            text,
            sourceStart,
            text.Length);
        var sentences = 0;
        foreach (var boundary in IncrementalSubtitleSegmenter.FindStrongBoundaries(text))
        {
            if (boundary <= lineStart)
                continue;
            sentences++;
            if (sentences < maximumSentences)
                continue;
            AddSizedRanges(text, lineStart, boundary, ranges);
            lineStart = IncrementalSubtitleSegmenter.SkipWhitespace(text, boundary, text.Length);
            sentences = 0;
        }
        if (lineStart < text.Length)
            AddSizedRanges(text, lineStart, text.Length, ranges);
        return ranges;
    }

    private static void AddSizedRanges(
        string text,
        int start,
        int end,
        List<SubtitleSourceRange> destination)
    {
        start = IncrementalSubtitleSegmenter.SkipWhitespace(text, start, end);
        end = IncrementalSubtitleSegmenter.TrimTrailingWhitespace(text, start, end);
        while (start < end)
        {
            var candidate = text[start..end];
            if (IncrementalSubtitleSegmenter.CountWords(candidate)
                    <= IncrementalSubtitleSegmenter.MaximumWords
                && IncrementalSubtitleSegmenter.CountDisplayColumns(candidate)
                    <= IncrementalSubtitleSegmenter.MaximumDisplayColumns)
            {
                destination.Add(new SubtitleSourceRange(start, end));
                return;
            }

            var cut = IncrementalSubtitleSegmenter.FindPreferredCut(candidate);
            if (cut <= 0 || cut >= candidate.Length)
                cut = Math.Max(1, candidate.Length / 2);
            var pieceEnd = IncrementalSubtitleSegmenter.TrimTrailingWhitespace(
                text,
                start,
                start + cut);
            if (pieceEnd <= start)
                pieceEnd = Math.Min(end, start + cut);
            destination.Add(new SubtitleSourceRange(start, pieceEnd));
            start = IncrementalSubtitleSegmenter.SkipWhitespace(text, pieceEnd, end);
        }
    }

    private void SchedulePreview(TimeSpan now)
    {
        var line = _currentLine;
        var settings = _getSettings();
        if (line is null
            || line.IsSealed
            || !settings.IsTranslationEnabled
            || !settings.IsRealTimePreviewEnabled)
            return;
        if (!IsPreviewEligible(line.OriginalText) || line.PreviewEligibleAt is null)
            return;
        if (string.Equals(line.LastPreviewRequestedSource, line.OriginalText, StringComparison.Ordinal))
            return;
        if (now - line.LastSourceChangedAt < PreviewDebounce
            && now - line.PreviewEligibleAt.Value < PreviewMaximumWait)
        {
            return;
        }

        QueuePreviewTranslation(line);
    }

    private void QueuePreviewTranslation(ManagedSubtitleLine line)
    {
        if (_finalJobs.Count > 0 || _recognitionStopped)
            return;
        CancelPendingPreview();
        _pendingPreview = CreateTranslationJob(line, isFinal: false);
        line.TranslationDefinition = _pendingPreview.Definition;
        line.LastPreviewRequestedSource = line.OriginalText;
        line.PreviewEligibleAt = null;
        line.IsTranslating = true;
        PublishLine(line);
    }

    private void QueueFinalTranslation(ManagedSubtitleLine line, TimeSpan now)
    {
        var settings = _getSettings();
        if (!settings.IsTranslationEnabled)
        {
            CancelDisabledTranslationWork(now);
            if (line.IsTranslationTerminal)
                return;
            line.TranslationDefinition = null;
            MarkTranslationTerminal(line, now);
            return;
        }

        var expectedDefinition = CreateTranslationDefinition(line, settings);
        if (line.IsTranslationTerminal
            && Equals(line.TranslationDefinition, expectedDefinition))
        {
            return;
        }

        line.IsTranslationTerminal = false;
        line.TranslationDefinition = expectedDefinition;
        line.ExpiresAt = null;
        if (_pendingPreview is { } pending
            && pending.LineId == line.Id
            && pending.Revision == line.Revision
            && Equals(pending.Definition, expectedDefinition)
            && string.Equals(pending.SourceText, line.OriginalText, StringComparison.Ordinal))
        {
            _pendingPreview = null;
            pending.IsFinal = true;
            _finalJobs.AddLast(pending);
            line.IsTranslating = true;
            PublishLine(line);
            return;
        }
        if (_activeTranslation is { } active)
        {
            if (!active.IsObsolete
                && active.LineId == line.Id
                && active.Revision == line.Revision
                && Equals(active.Definition, expectedDefinition)
                && string.Equals(active.SourceText, line.OriginalText, StringComparison.Ordinal))
            {
                if (!active.IsFinal)
                    active.IsFinal = true;
                line.IsTranslating = true;
                PublishLine(line);
                return;
            }
        }

        if (string.Equals(line.ShadowTranslationSource, line.OriginalText, StringComparison.Ordinal)
            && Equals(line.ShadowTranslationDefinition, expectedDefinition)
            && !string.IsNullOrWhiteSpace(line.ShadowTranslation))
        {
            CancelPendingPreview();
            line.TranslatedText = line.ShadowTranslation;
            line.DisplayTranslatedText = line.ShadowTranslation;
            line.LastTranslatedSource = line.OriginalText;
            line.LastTranslationDefinition = expectedDefinition;
            MarkTranslationTerminal(line, now);
            return;
        }
        if (string.Equals(line.LastTranslatedSource, line.OriginalText, StringComparison.Ordinal)
            && Equals(line.LastTranslationDefinition, expectedDefinition)
            && !string.IsNullOrWhiteSpace(line.DisplayTranslatedText))
        {
            CancelPendingPreview();
            MarkTranslationTerminal(line, now);
            return;
        }

        CancelPendingPreview();
        if (_activeTranslation is { } activeToCancel)
        {
            if (activeToCancel.LineId == line.Id || !activeToCancel.IsFinal)
                CancelAndDetachActiveTranslation();
        }

        RemoveQueuedTranslations(line.Id);
        while (_finalJobs.Count >= TranslationQueueCapacity)
        {
            var dropped = _finalJobs.First!.Value;
            _finalJobs.RemoveFirst();
            dropped.Cancellation.Dispose();
            if (_linesById.TryGetValue(dropped.LineId, out var droppedLine)
                && droppedLine.Revision == dropped.Revision
                && string.Equals(droppedLine.OriginalText, dropped.SourceText, StringComparison.Ordinal))
            {
                _logger.LogWarning(
                    "Dropping the oldest unstarted subtitle translation for line {SubtitleId} because the queue is full.",
                    dropped.LineId);
                MarkTranslationTerminal(droppedLine, now);
            }
        }

        _finalJobs.AddLast(CreateTranslationJob(line, isFinal: true, expectedDefinition));
        line.IsTranslating = true;
        PublishLine(line);
    }

    private TranslationJob CreateTranslationJob(
        ManagedSubtitleLine line,
        bool isFinal,
        TranslationJobDefinition? definition = null)
    {
        definition ??= CreateTranslationDefinition(line, _getSettings());
        return new TranslationJob(
            Interlocked.Increment(ref _nextTranslationJobId),
            line.Id,
            line.Revision,
            line.OriginalText,
            isFinal,
            definition,
            CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None));
    }

    private TranslationJobDefinition CreateTranslationDefinition(
        ManagedSubtitleLine line,
        SpeechRecognitionSettings settings)
    {
        var isAi = settings.EngineType != 0;
        var lineIndex = _sealedLines.IndexOf(line);
        var contextCandidates = lineIndex >= 0
            ? _sealedLines.Take(lineIndex)
            : _sealedLines;
        var context = contextCandidates
            .TakeLast(2)
            .Select(candidate => new SubtitleTranslationContext(
                candidate.OriginalText,
                candidate.DisplayTranslatedText))
            .ToArray();
        var requestText = isAi
            ? JsonSerializer.Serialize(new
            {
                context,
                current = line.OriginalText
            })
            : line.OriginalText;
        var selection = isAi
            ? new TranslationProviderSelection(
                TranslationEngineNames.AiModel,
                AiModelId: settings.EngineId,
                PromptOverride: SubtitlePrompt)
            : new TranslationProviderSelection(
                TranslationEngineNames.MachineTrans,
                MachineProviderId: settings.EngineId);
        return new TranslationJobDefinition(
            requestText,
            selection,
            _languages.Get(MapRecognitionLanguage(settings.RecognitionLanguage)),
            _languages.Get(settings.TargetLanguage));
    }

    private void TryStartTranslation(CancellationToken sessionToken)
    {
        if (!_getSettings().IsTranslationEnabled)
        {
            CancelDisabledTranslationWork(GetMonotonicNow());
            return;
        }
        if (_activeTranslation is not null)
            return;
        TranslationJob? job = null;
        if (_finalJobs.First is not null)
        {
            job = _finalJobs.First.Value;
            _finalJobs.RemoveFirst();
        }
        else if (!_recognitionStopped && _pendingPreview is not null)
        {
            job = _pendingPreview;
            _pendingPreview = null;
        }
        if (job is null)
            return;

        job.SessionRegistration = sessionToken.Register(job.Cancellation.Cancel);
        _activeTranslation = job;
        job.Runner = RunTranslationAsync(job, sessionToken);
    }

    private async Task RunTranslationAsync(TranslationJob job, CancellationToken sessionToken)
    {
        using var logicalLifetime = CancellationTokenSource.CreateLinkedTokenSource(
            sessionToken,
            job.Cancellation.Token);
        var providerRun = RunProviderTranslationAsync(job, sessionToken);
        try
        {
            var result = await providerRun.WaitAsync(
                    TranslationTimeout,
                    _timeProvider,
                    logicalLifetime.Token)
                .ConfigureAwait(false);
            await TryWriteCompletionAsync(
                CreateTranslationCompletion(job, result),
                sessionToken).ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            TryCancel(job.Cancellation);
            await TryWriteCompletionAsync(
                new TranslationCompletedMessage(
                    job.Id,
                    job.LineId,
                    job.Revision,
                    job.SourceText,
                    string.Empty,
                    new OperationCanceledException("Subtitle translation timed out.", exception),
                    false),
                sessionToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (logicalLifetime.IsCancellationRequested)
        {
            await TryWriteCompletionAsync(
                new TranslationCompletedMessage(
                    job.Id,
                    job.LineId,
                    job.Revision,
                    job.SourceText,
                    string.Empty,
                    null,
                    true),
                sessionToken).ConfigureAwait(false);
        }
        finally
        {
            job.SessionRegistration.Dispose();
            job.Cancellation.Dispose();
        }
    }

    private async Task<TranslationRunResult> RunProviderTranslationAsync(
        TranslationJob job,
        CancellationToken sessionToken)
    {
        using var providerLifetime = CancellationTokenSource.CreateLinkedTokenSource(
            sessionToken,
            job.Cancellation.Token);
        var translationGate = string.Equals(
            job.Selection.Engine,
            TranslationEngineNames.MachineTrans,
            StringComparison.Ordinal)
            ? _machineTranslationGate
            : _aiTranslationGate;
        var gateHeld = false;
        try
        {
            await translationGate.WaitAsync(providerLifetime.Token).ConfigureAwait(false);
            gateHeld = true;
            var builder = new StringBuilder();
            var session = _translation.Prepare(job.Selection);
            using var disposable = session as IDisposable;
            await foreach (var item in session.StreamAsync(
                               new TranslationRequest(
                                   job.RequestText,
                                   job.SourceLanguage,
                                   job.TargetLanguage,
                                   Provider: job.Selection),
                               providerLifetime.Token).ConfigureAwait(false))
            {
                switch (item)
                {
                    case TranslationDeltaEvent { Text.Length: > 0 } delta:
                        builder.Append(delta.Text);
                        await _inbox.Writer.WriteAsync(
                                new TranslationBufferMessage(
                                    job.Id,
                                    job.LineId,
                                    job.Revision,
                                    job.SourceText,
                                    builder.ToString()),
                                providerLifetime.Token)
                            .ConfigureAwait(false);
                        break;
                    case TranslationFailedEvent failed:
                        throw new InvalidOperationException(failed.Error.Message);
                }
            }
            return new TranslationRunResult(builder.ToString(), null, false);
        }
        catch (OperationCanceledException) when (providerLifetime.IsCancellationRequested)
        {
            return new TranslationRunResult(string.Empty, null, true);
        }
        catch (Exception exception)
        {
            return new TranslationRunResult(string.Empty, exception, false);
        }
        finally
        {
            if (gateHeld)
                translationGate.Release();
        }
    }

    private static TranslationCompletedMessage CreateTranslationCompletion(
        TranslationJob job,
        TranslationRunResult result) =>
        new(
            job.Id,
            job.LineId,
            job.Revision,
            job.SourceText,
            result.Text,
            result.Exception,
            result.WasCanceled);

    private void HandleTranslationBuffer(TranslationBufferMessage message, TimeSpan now)
    {
        var job = _activeTranslation;
        if (job is null
            || job.IsObsolete
            || !MessageMatchesJob(message, job)
            || !TryResolveJobLine(job, out var line))
            return;
        job.Buffer = message.Text;
        line.ShadowTranslation = message.Text;
        line.ShadowTranslationSource = job.SourceText;
        line.ShadowTranslationDefinition = job.Definition;
        if (!job.Revealed
            && IsReadableTranslation(message.Text)
            && CanRevealTranslation(line, job))
        {
            job.Revealed = true;
            ApplyTranslationDisplay(line, job, message.Text);
            job.NextDisplayAt = now + DisplayUpdateInterval;
        }
        else if (job.Revealed && now >= job.NextDisplayAt)
        {
            ApplyTranslationDisplay(line, job, message.Text);
            job.NextDisplayAt = now + DisplayUpdateInterval;
        }
    }

    private void HandleTranslationCompleted(TranslationCompletedMessage message, TimeSpan now)
    {
        var job = _activeTranslation;
        if (job is null || !MessageMatchesJob(message, job))
            return;
        _activeTranslation = null;

        if (job.IsObsolete)
            return;

        if (TryResolveJobLine(job, out var line))
        {
            if (message.Exception is not null)
            {
                if (message.Exception is OperationCanceledException)
                    _logger.LogDebug("Subtitle translation timed out for line {SubtitleId}.", line.Id);
                else
                    _logger.LogError(message.Exception, "Subtitle translation failed for line {SubtitleId}.", line.Id);
            }
            else if (!message.WasCanceled)
            {
                var translated = message.Text.Length > 0 ? message.Text : job.Buffer;
                line.ShadowTranslation = translated;
                line.ShadowTranslationSource = job.SourceText;
                line.ShadowTranslationDefinition = job.Definition;
                if (CanRevealTranslation(line, job))
                {
                    ApplyTranslationDisplay(line, job, translated);
                    line.LastTranslatedSource = job.SourceText;
                    line.LastTranslationDefinition = job.Definition;
                }
            }

            if (job.IsFinal && line.IsSealed
                            && string.Equals(line.OriginalText, job.SourceText, StringComparison.Ordinal))
            {
                MarkTranslationTerminal(line, now);
            }
            else
            {
                line.IsTranslating = false;
                PublishLine(line);
            }
        }
    }

    private void FlushBufferedTranslation(TimeSpan now)
    {
        var job = _activeTranslation;
        if (job is null || !job.Revealed || now < job.NextDisplayAt || job.Buffer.Length == 0)
            return;
        if (!TryResolveJobLine(job, out var line))
            return;
        ApplyTranslationDisplay(line, job, job.Buffer);
        job.NextDisplayAt = now + DisplayUpdateInterval;
    }

    private bool TryResolveJobLine(TranslationJob job, out ManagedSubtitleLine line)
    {
        line = default!;
        if (!_linesById.TryGetValue(job.LineId, out var candidate))
            return false;
        var sourceMatches = job.IsFinal
            ? candidate.Revision == job.Revision
              && string.Equals(candidate.OriginalText, job.SourceText, StringComparison.Ordinal)
            : candidate.OriginalText.StartsWith(job.SourceText, StringComparison.Ordinal);
        if (!sourceMatches)
            return false;
        line = candidate;
        return true;
    }

    private static bool MessageMatchesJob(
        TranslationBufferMessage message,
        TranslationJob job) =>
        message.JobId == job.Id
        && message.LineId == job.LineId
        && message.Revision == job.Revision
        && string.Equals(message.SourceText, job.SourceText, StringComparison.Ordinal);

    private static bool MessageMatchesJob(
        TranslationCompletedMessage message,
        TranslationJob job) =>
        message.JobId == job.Id
        && message.LineId == job.LineId
        && message.Revision == job.Revision
        && string.Equals(message.SourceText, job.SourceText, StringComparison.Ordinal);

    private void ApplyTranslationDisplay(
        ManagedSubtitleLine line,
        TranslationJob job,
        string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
        line.TranslatedText = text;
        line.DisplayTranslatedText = text;
        line.ShadowTranslation = text;
        line.ShadowTranslationSource = job.SourceText;
        line.ShadowTranslationDefinition = job.Definition;
        line.LastTranslatedSource = job.SourceText;
        line.LastTranslationDefinition = job.Definition;
        PublishLine(line);
    }

    private static bool CanRevealTranslation(ManagedSubtitleLine line, TranslationJob job) =>
        job.IsFinal
        || string.IsNullOrWhiteSpace(line.DisplayTranslatedText)
        || (line.LastTranslatedSource.Length > 0
            && job.SourceText.StartsWith(line.LastTranslatedSource, StringComparison.Ordinal));

    private void MarkTranslationTerminal(ManagedSubtitleLine line, TimeSpan now)
    {
        line.IsTranslating = false;
        line.IsTranslationTerminal = true;
        var seconds = _getSettings().AutoClearInterval;
        line.ExpiresAt = seconds > 0 ? now + TimeSpan.FromSeconds(seconds) : null;
        PublishLine(line);
    }

    private void ExpireFloatingLines(TimeSpan now)
    {
        foreach (var line in _floating
                     .Where(line => line.IsFloatingVisible
                                    && line.IsSealed
                                    && line.ExpiresAt is not null
                                    && line.ExpiresAt <= now)
                     .ToArray())
        {
            RemoveFromFloating(line);
        }
    }

    private void TrimFloatingHistory()
    {
        var settings = _getSettings();
        var limit = settings.FloatingDisplayMode == FloatingDisplayMode.Segmented
            ? Math.Max(1, settings.MaxFloatingHistory)
            : 100;
        while (_floating.Count(line => line.IsFloatingVisible) > limit)
        {
            var removable = _floating.FirstOrDefault(line => line.IsFloatingVisible && line != _currentLine);
            if (removable is null)
                return;
            RemoveFromFloating(removable);
        }
    }

    private void RemoveFromFloating(ManagedSubtitleLine line)
    {
        if (!line.IsFloatingVisible)
            return;
        line.IsFloatingVisible = false;
        _publish(new SpeechFloatingSubtitleRemovedEvent(line.Id));
    }

    private void PublishLine(ManagedSubtitleLine line)
    {
        _publish(new SpeechSubtitleChangedEvent(line.Snapshot()));
        TrimFloatingHistory();
    }

    private void RemoveQueuedTranslations(long lineId)
    {
        var node = _finalJobs.First;
        while (node is not null)
        {
            var next = node.Next;
            if (node.Value.LineId == lineId)
            {
                node.Value.Cancellation.Dispose();
                _finalJobs.Remove(node);
            }
            node = next;
        }
    }

    private void CancelPendingPreview()
    {
        var job = _pendingPreview;
        if (job is null)
            return;
        _pendingPreview = null;
        job.Cancellation.Cancel();
        job.Cancellation.Dispose();
        if (_linesById.TryGetValue(job.LineId, out var line)
            && (_activeTranslation?.LineId != line.Id)
            && !_finalJobs.Any(candidate => candidate.LineId == line.Id))
        {
            line.IsTranslating = false;
            PublishLine(line);
        }
    }

    private void CancelAndDetachActiveTranslation()
    {
        var job = _activeTranslation;
        if (job is null || job.IsObsolete)
            return;
        job.IsObsolete = true;
        TryCancel(job.Cancellation);
        if (_linesById.TryGetValue(job.LineId, out var line)
            && !_finalJobs.Any(candidate => candidate.LineId == line.Id))
        {
            line.IsTranslating = false;
            PublishLine(line);
        }
    }

    private static void TryCancel(CancellationTokenSource cancellation)
    {
        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void CancelPendingJobs()
    {
        CancelPendingPreview();
        foreach (var job in _finalJobs)
        {
            job.Cancellation.Cancel();
            job.Cancellation.Dispose();
        }
        _finalJobs.Clear();
    }

    private void CancelDisabledTranslationWork(TimeSpan now)
    {
        var affectedLineIds = new HashSet<long>();
        if (_pendingPreview is { } pending)
            affectedLineIds.Add(pending.LineId);
        if (_activeTranslation is { IsObsolete: false } active)
            affectedLineIds.Add(active.LineId);
        foreach (var job in _finalJobs)
            affectedLineIds.Add(job.LineId);
        if (affectedLineIds.Count == 0)
            return;

        CancelAndDetachActiveTranslation();
        CancelPendingJobs();
        foreach (var lineId in affectedLineIds)
        {
            if (!_linesById.TryGetValue(lineId, out var line))
                continue;
            line.TranslationDefinition = null;
            line.ShadowTranslationDefinition = null;
            line.LastTranslationDefinition = null;
            line.LastPreviewRequestedSource = string.Empty;
            line.PreviewEligibleAt = !line.IsSealed && IsPreviewEligible(line.OriginalText)
                ? now
                : null;
            if (line.IsSealed)
            {
                if (!line.IsTranslationTerminal || line.IsTranslating)
                    MarkTranslationTerminal(line, now);
            }
            else if (line.IsTranslating)
            {
                line.IsTranslating = false;
                PublishLine(line);
            }
        }
    }

    private async Task TryWriteCompletionAsync(
        TranslationCompletedMessage message,
        CancellationToken sessionToken)
    {
        try
        {
            await _inbox.Writer.WriteAsync(message, sessionToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (sessionToken.IsCancellationRequested)
        {
        }
    }

    private static bool IsPreviewEligible(string text) =>
        IncrementalSubtitleSegmenter.CountWords(text) >= 4
        || IncrementalSubtitleSegmenter.CountGraphemes(text) >= 8;

    private static bool IsReadableTranslation(string text) =>
        IncrementalSubtitleSegmenter.CountWords(text) >= 2
        || IncrementalSubtitleSegmenter.CountGraphemes(text) >= 6;

    private static string JoinText(string left, string right)
    {
        left = left.TrimEnd();
        right = right.TrimStart();
        if (left.Length == 0)
            return right;
        if (right.Length == 0)
            return left;
        if (IsPunctuation(right[0]) || IsCjk(left[^1]) || IsCjk(right[0]))
            return left + right;
        return left + " " + right;
    }

    private static bool IsPunctuation(char character) =>
        char.IsPunctuation(character) && character is not '(' and not '[' and not '{';

    private static bool IsCjk(char character) =>
        character is >= '\u2e80' and <= '\u9fff'
            or >= '\uac00' and <= '\ud7af'
            or >= '\uf900' and <= '\ufaff';

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

    private TimeSpan GetMonotonicNow() =>
        _timeProvider.GetElapsedTime(_startTimestamp, _timeProvider.GetTimestamp());

    private static async Task IgnoreCancellationAsync(Task task, CancellationToken cancellationToken)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private abstract record SessionMessage;
    private sealed record RecognitionMessage(SpeechRecognitionEvent Event) : SessionMessage;
    private sealed record RecognitionFailureMessage(Exception Exception) : SessionMessage;
    private sealed record TickMessage(TimeSpan Now) : SessionMessage;
    private sealed record TranslationBufferMessage(
        long JobId,
        long LineId,
        long Revision,
        string SourceText,
        string Text) : SessionMessage;
    private sealed record TranslationCompletedMessage(
        long JobId,
        long LineId,
        long Revision,
        string SourceText,
        string Text,
        Exception? Exception,
        bool WasCanceled) : SessionMessage;

    private sealed record TranslationRunResult(
        string Text,
        Exception? Exception,
        bool WasCanceled);

    private sealed record TranslationJobDefinition(
        string RequestText,
        TranslationProviderSelection Selection,
        TranslationLanguage SourceLanguage,
        TranslationLanguage TargetLanguage);

    private sealed class TranslationJob(
        long id,
        long lineId,
        long revision,
        string sourceText,
        bool isFinal,
        TranslationJobDefinition definition,
        CancellationTokenSource cancellation)
    {
        public long Id { get; } = id;
        public long LineId { get; } = lineId;
        public long Revision { get; } = revision;
        public string SourceText { get; } = sourceText;
        public TranslationJobDefinition Definition { get; } = definition;
        public string RequestText => Definition.RequestText;
        public bool IsFinal { get; set; } = isFinal;
        public TranslationProviderSelection Selection => Definition.Selection;
        public TranslationLanguage SourceLanguage => Definition.SourceLanguage;
        public TranslationLanguage TargetLanguage => Definition.TargetLanguage;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public CancellationTokenRegistration SessionRegistration { get; set; }
        public Task? Runner { get; set; }
        public string Buffer { get; set; } = string.Empty;
        public bool Revealed { get; set; }
        public bool IsObsolete { get; set; }
        public TimeSpan NextDisplayAt { get; set; }
    }

    private sealed class ManagedSubtitleLine(long id, TimeSpan timestamp, TimeSpan createdAt)
    {
        public long Id { get; } = id;
        public TimeSpan Timestamp { get; } = timestamp;
        public long Revision { get; set; }
        public string OriginalText { get; set; } = string.Empty;
        public string TranslatedText { get; set; } = string.Empty;
        public string DisplayTranslatedText { get; set; } = string.Empty;
        public string ShadowTranslation { get; set; } = string.Empty;
        public string ShadowTranslationSource { get; set; } = string.Empty;
        public TranslationJobDefinition? ShadowTranslationDefinition { get; set; }
        public string LastTranslatedSource { get; set; } = string.Empty;
        public TranslationJobDefinition? LastTranslationDefinition { get; set; }
        public TranslationJobDefinition? TranslationDefinition { get; set; }
        public string LastPreviewRequestedSource { get; set; } = string.Empty;
        public bool IsTranslating { get; set; }
        public bool IsTranslationTerminal { get; set; }
        public bool IsTemporary { get; set; } = true;
        public bool IsSealed { get; set; }
        public bool IsFloatingVisible { get; set; } = true;
        public TimeSpan LastSourceChangedAt { get; set; } = createdAt;
        public TimeSpan? PreviewEligibleAt { get; set; }
        public TimeSpan? ExpiresAt { get; set; }
        public int SourceStart { get; set; }
        public int SourceEnd { get; set; }

        public SpeechSubtitleLine Snapshot() => new(
            Id,
            Timestamp,
            OriginalText,
            TranslatedText,
            DisplayTranslatedText,
            IsTranslating,
            IsTemporary);
    }

    private sealed class UtteranceLineRange(
        ManagedSubtitleLine line,
        int start,
        int end)
    {
        public ManagedSubtitleLine Line { get; } = line;
        public int Start { get; set; } = start;
        public int End { get; set; } = end;
    }

    private readonly record struct SubtitleSourceRange(int Start, int End);

    private sealed record SubtitleTranslationContext(string Original, string Translation);
}
