namespace Hotkey_Translator.Models;

public enum CaptureProviderKind
{
    // NOTE: v1 is DX11-only, but the kind name is generic so future OpenGL/Vulkan backends can share the same slot.
    GraphicsHook,
    Wgc,
    Dxgi,
    Gdi
}
