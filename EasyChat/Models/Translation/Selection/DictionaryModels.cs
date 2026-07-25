using System.Collections.ObjectModel;
using ReactiveUI;

namespace EasyChat.Models.Translation.Selection;

public class DictionaryResult : ReactiveObject
{
    public DictionaryResult()
    {
        Examples.CollectionChanged += (_, _) => this.RaisePropertyChanged(nameof(HasExamples));
        Forms.CollectionChanged += (_, _) => this.RaisePropertyChanged(nameof(HasForms));
    }

    private string _word = string.Empty;
    public string Word { get => _word; set => this.RaiseAndSetIfChanged(ref _word, value); }
    private string _phonetic = string.Empty;
    public string Phonetic { get => _phonetic; set => this.RaiseAndSetIfChanged(ref _phonetic, value); }
    private string? _pronunciationUrl;
    public string? PronunciationUrl { get => _pronunciationUrl; set => this.RaiseAndSetIfChanged(ref _pronunciationUrl, value); }
    private string? _tips;
    public string? Tips { get => _tips; set => this.RaiseAndSetIfChanged(ref _tips, value); }
    public ObservableCollection<DictionaryExample> Examples { get; } = [];
    public bool HasExamples => Examples.Count > 0;
    public ObservableCollection<DictionaryPart> Parts { get; } = [];
    public ObservableCollection<DictionaryForm> Forms { get; } = [];
    public bool HasForms => Forms.Count > 0;
}

public class DictionaryForm : ReactiveObject
{
    public string Label { get; set; } = string.Empty;
    public string Word { get; set; } = string.Empty;
    
    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        set => this.RaiseAndSetIfChanged(ref _isLoading, value);
    }
}

public class DictionaryExample : ReactiveObject
{
    public string Origin { get; set; } = string.Empty;
    public string Translation { get; set; } = string.Empty;
    
    private bool _isOriginLoading;
    public bool IsOriginLoading
    {
        get => _isOriginLoading;
        set => this.RaiseAndSetIfChanged(ref _isOriginLoading, value);
    }
    
    private bool _isTranslationLoading;
    public bool IsTranslationLoading
    {
        get => _isTranslationLoading;
        set => this.RaiseAndSetIfChanged(ref _isTranslationLoading, value);
    }
}

public class DictionaryPart : ReactiveObject
{
    public string PartOfSpeech { get; set; } = string.Empty; // e.g., "n.", "v."
    public ObservableCollection<string> Definitions { get; } = [];
}

public class TextToken
{
    public string Text { get; set; } = string.Empty;
    public bool IsWord { get; set; }
    public int StartIndex { get; set; }
    public int Length { get; set; }
}
