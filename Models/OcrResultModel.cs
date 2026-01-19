using System.Collections.Generic;

namespace Hotkey_Translator.Models;

public sealed class OcrResultModel
{
    public OcrResultModel(IReadOnlyList<OcrLine> lines, int pixelWidth, int pixelHeight)
    {
        Lines = lines;
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
    }

    public IReadOnlyList<OcrLine> Lines { get; }
    public int PixelWidth { get; }
    public int PixelHeight { get; }
}
