using System.Text;
using EasyChat.Contracts.SelectionTranslation;

namespace EasyChat.Application.SelectionTranslation;

public sealed class SelectionTranslationResultAccumulator
{
    private readonly string _sourceText;
    private readonly SelectionTranslationSource _source;
    private readonly StringBuilder _translation = new();
    private readonly List<SelectionWordDefinition> _definitions = [];
    private readonly List<SelectionWordForm> _forms = [];
    private readonly List<SelectionWordExample> _examples = [];
    private readonly List<SelectionKeyword> _keywords = [];
    private SelectionTranslationMode? _mode;
    private string? _detectedSourceLanguage;
    private string? _word;
    private string? _phonetic;
    private string? _tips;
    private bool _completed;

    public SelectionTranslationResultAccumulator(
        string sourceText,
        SelectionTranslationSource source = SelectionTranslationSource.Ai)
    {
        _sourceText = sourceText ?? throw new ArgumentNullException(nameof(sourceText));
        _source = source;
    }

    public void Apply(SelectionTranslationEvent translationEvent)
    {
        ArgumentNullException.ThrowIfNull(translationEvent);
        switch (translationEvent)
        {
            case SelectionTranslationStartedEvent started:
                _mode ??= started.Mode;
                if (_mode != started.Mode)
                    throw new InvalidOperationException("Translation stream cannot change modes.");
                break;
            case SelectionTranslationSourceDetectedEvent detected:
                _detectedSourceLanguage = detected.Language;
                break;
            case SelectionTranslationDeltaEvent delta:
                _translation.Append(delta.Text);
                break;
            case SelectionTranslationWordHeaderEvent header:
                _word = header.Word;
                _phonetic = header.Phonetic;
                break;
            case SelectionTranslationDefinitionEvent definition:
                _definitions.Add(new SelectionWordDefinition(definition.Pos ?? string.Empty, definition.Meaning));
                break;
            case SelectionTranslationFormEvent form:
                _forms.Add(new SelectionWordForm(form.Label, form.Word));
                break;
            case SelectionTranslationTipsEvent tips:
                _tips = tips.Text;
                break;
            case SelectionTranslationExampleEvent example:
                _examples.Add(new SelectionWordExample(example.Origin, example.Translation));
                break;
            case SelectionTranslationKeywordEvent keyword:
                _keywords.Add(new SelectionKeyword(keyword.Word, keyword.Meaning));
                break;
            case SelectionTranslationCompletedEvent:
                _completed = true;
                break;
        }
    }

    public SelectionTranslationResult Build()
    {
        if (_mode is null)
            throw new InvalidOperationException("Translation stream did not specify a mode.");
        if (!_completed)
            throw new InvalidOperationException("Translation stream ended before its done event.");

        return _mode == SelectionTranslationMode.Word
            ? new SelectionWordResult(
                _source,
                _detectedSourceLanguage,
                _word ?? _sourceText,
                _phonetic ?? string.Empty,
                _definitions,
                _tips ?? string.Empty,
                _examples,
                _forms)
            : new SelectionSentenceResult(
                _source,
                _detectedSourceLanguage,
                _sourceText,
                _translation.ToString(),
                _keywords);
    }
}
