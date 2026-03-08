namespace Hotkey_Translator.Models;

public sealed class RoiPreset
{
    public int SlotIndex { get; set; }
    public NormalizedRect? NormalizedRoi { get; set; }
    public bool EnableRoi { get; set; } = true;
}
