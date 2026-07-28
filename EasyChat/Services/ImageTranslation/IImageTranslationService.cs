using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using EasyChat.Models.Ocr;
using EasyChat.Services.Languages;

namespace EasyChat.Services.ImageTranslation;

public interface IImageTranslationService
{
    Task<ImageTranslationResult> TranslateAsync(
        Bitmap bitmap,
        OcrRecognitionResult recognition,
        LanguageDefinition? source,
        LanguageDefinition? target,
        CancellationToken cancellationToken = default);
}
