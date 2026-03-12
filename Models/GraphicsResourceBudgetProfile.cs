namespace Hotkey_Translator.Models;

public enum GraphicsResourceBudgetProfile
{
    // COMPAT: Persisted to settings.json; keep numeric values stable.
    LowVram = 0,
    Balanced = 1,
    HighVram = 2,
    UltraVram = 3
}
