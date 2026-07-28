using System.Collections.Generic;
using System.Linq;

namespace EasyChat.Models.Ocr;

public sealed record OcrRecognitionResult(IReadOnlyList<OcrTextRegion> Regions)
{
    public string Text => string.Join("\n", Regions.Select(region => region.Text));
}
