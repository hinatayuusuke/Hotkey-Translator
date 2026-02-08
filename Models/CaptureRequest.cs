using System;

namespace Hotkey_Translator.Models;

public readonly record struct CaptureRequest(CaptureMode Mode, IntPtr? TargetWindowHandle)
{
    public IntPtr ResolveWindowHandle(IntPtr fallbackWindowHandle)
    {
        if (Mode != CaptureMode.ActiveWindow)
        {
            return IntPtr.Zero;
        }

        if (TargetWindowHandle.HasValue && TargetWindowHandle.Value != IntPtr.Zero)
        {
            return TargetWindowHandle.Value;
        }

        return fallbackWindowHandle;
    }
}
