using System.Windows;
using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services;

public interface ICaptureProvider
{
    CaptureProviderKind Kind { get; }

    bool IsEnabled(AppSettings settings);

    bool TryGetBounds(CaptureMode mode, out Rect bounds);

    bool TryCapture(CaptureMode mode, out CaptureFrame frame, out string? error);
}
