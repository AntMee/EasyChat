using System.Threading.Tasks;

namespace EasyChat.Services.TextAssist;

public interface ITextAssistDictionaryService
{
    Task OpenAsync(string text, string sourceLanguageId, string targetLanguageId);
}
