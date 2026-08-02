using System.Text;
using EasyChat.Contracts.Settings;
using EasyChat.Contracts.Speech;

namespace EasyChat.Application.Speech;

internal sealed class SubtitleTimeline(
    Func<SpeechRecognitionSettings> getSettings,
    Action<SpeechSessionEvent> publish)
{
    private readonly SubtitleTextSegmenter _segmenter = new();
    private readonly List<SubtitleLineState> _floating = [];
    private readonly List<SubtitleLineState> _temporary = [];
    private readonly StringBuilder _committedText = new();
    private SubtitleLineState? _current;
    private int _sentencesInCurrent;
    private long _nextId;
    private SpeechRecognitionSettings Settings => getSettings();

    public void Reset()
    {
        foreach (var line in _floating.ToArray())
            publish(new SpeechFloatingSubtitleRemovedEvent(line.Id));
        _floating.Clear();
        _temporary.Clear();
        _current = null;
        _sentencesInCurrent = 0;
        _committedText.Clear();
    }

    public async Task ApplyFinalAsync(
        string text,
        Func<SubtitleLineState, string, bool, Task> translate,
        CancellationToken cancellationToken)
    {
        var segments = _segmenter.SplitSentences(text);
        for (var index = 0; index < segments.Count; index++)
        {
            var line = EnsureCurrent();
            var segment = segments[index];
            var count = _segmenter.CountSentences(segment);
            if (count == 0 && !string.IsNullOrWhiteSpace(segment))
                count = 1;
            if (_committedText.Length > 0)
                _committedText.Append(' ');
            _committedText.Append(segment);
            line.OriginalText = _committedText.ToString();
            _sentencesInCurrent += count;
            Publish(line);
            if (Settings.IsTranslationEnabled)
                await translate(line, line.OriginalText, true).ConfigureAwait(false);

            if (_sentencesInCurrent < Math.Max(1, Settings.MaxSentencesPerLine))
                continue;
            AddToFloating(line);
            if (index < segments.Count - 1 || segments.Count > 1)
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
            _current = null;
            _sentencesInCurrent = 0;
            _committedText.Clear();
        }
    }

    public async Task ApplyPartialAsync(
        string text,
        Func<SubtitleLineState, string, bool, Task> translate)
    {
        var line = EnsureCurrent();
        var separator = _committedText.Length > 0 ? " " : string.Empty;
        var completeText = _committedText + separator + text;
        if (Settings.FloatingDisplayMode == FloatingDisplayMode.Segmented)
        {
            line.OriginalText = completeText;
            Publish(line);
            AddToFloating(line);
            if (Settings.IsTranslationEnabled)
                await translate(line, line.OriginalText, false).ConfigureAwait(false);
            return;
        }

        var paragraphs = _segmenter.SplitIntoParagraphs(
            completeText,
            Math.Max(1, Settings.MaxSentencesPerLine));
        line.OriginalText = paragraphs.Count == 0 ? completeText : paragraphs[0];
        RemoveInactiveTemporaryLines();
        for (var index = 1; index < paragraphs.Count; index++)
        {
            var temporary = CreateLine(isTemporary: true);
            temporary.OriginalText = paragraphs[index];
            _temporary.Add(temporary);
            Publish(temporary);
            AddToFloating(temporary);
            if (Settings.IsTranslationEnabled)
                await translate(temporary, temporary.OriginalText, false).ConfigureAwait(false);
        }
        Publish(line);
        AddToFloating(line);
        if (Settings.IsTranslationEnabled)
            await translate(line, line.OriginalText, false).ConfigureAwait(false);
    }

    public async Task CompleteAsync(Func<SubtitleLineState, string, bool, Task> translate)
    {
        if (_current is not null)
        {
            if (Settings.IsTranslationEnabled)
                await translate(_current, _current.OriginalText, true).ConfigureAwait(false);
            Publish(_current);
            AddToFloating(_current);
            _current = null;
        }
        _sentencesInCurrent = 0;
        _committedText.Clear();
    }

    public void Publish(SubtitleLineState line)
    {
        publish(new SpeechSubtitleChangedEvent(line.Snapshot()));
        TrimFloatingHistory();
    }

    private SubtitleLineState EnsureCurrent() => _current ??= CreateLine(isTemporary: false);

    private SubtitleLineState CreateLine(bool isTemporary) =>
        new(++_nextId, DateTime.Now.TimeOfDay, isTemporary);

    private void AddToFloating(SubtitleLineState line)
    {
        if (!_floating.Contains(line))
            _floating.Add(line);
        TrimFloatingHistory();
    }

    private void RemoveInactiveTemporaryLines()
    {
        foreach (var line in _temporary.Where(line => !line.IsTranslating).ToArray())
        {
            _temporary.Remove(line);
            _floating.Remove(line);
            publish(new SpeechFloatingSubtitleRemovedEvent(line.Id));
        }
    }

    private void TrimFloatingHistory()
    {
        var limit = Settings.FloatingDisplayMode == FloatingDisplayMode.Segmented
            ? Math.Max(1, Settings.MaxFloatingHistory)
            : 100;
        while (_floating.Count > limit)
        {
            var removable = _floating.FirstOrDefault(line =>
                !line.IsTranslating
                && (!Settings.IsTranslationEnabled || !string.IsNullOrWhiteSpace(line.TranslatedText)));
            if (removable is null)
                return;
            _floating.Remove(removable);
            publish(new SpeechFloatingSubtitleRemovedEvent(removable.Id));
        }
    }
}

internal sealed class SubtitleLineState(long id, TimeSpan timestamp, bool isTemporary)
{
    public long Id { get; } = id;
    public TimeSpan Timestamp { get; } = timestamp;
    public bool IsTemporary { get; } = isTemporary;
    public string OriginalText { get; set; } = "...";
    public string TranslatedText { get; set; } = string.Empty;
    public string DisplayTranslatedText { get; set; } = string.Empty;
    public string ConfirmedOriginalText { get; set; } = string.Empty;
    public string ConfirmedTranslatedText { get; set; } = string.Empty;
    public bool IsTranslating { get; set; }

    public SpeechSubtitleLine Snapshot() => new(
        Id,
        Timestamp,
        OriginalText,
        TranslatedText,
        DisplayTranslatedText,
        IsTranslating,
        IsTemporary);
}
