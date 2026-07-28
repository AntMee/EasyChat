using System.Collections.Generic;
using Avalonia.Media.Imaging;

namespace EasyChat.Services.ImageTranslation;

public sealed record ImageTranslationResult(
    Bitmap Bitmap,
    IReadOnlyList<string> Warnings,
    int DetectedBlockCount,
    int TranslatedBlockCount);
