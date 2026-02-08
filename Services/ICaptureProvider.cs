using System.Windows;
using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services;

public interface ICaptureProvider
{
    CaptureProviderKind Kind { get; }

    bool IsEnabled(AppSettings settings);

    bool TryGetBounds(CaptureRequest request, out Rect bounds);

    bool TryCapture(CaptureRequest request, out CaptureFrame frame, out string? error);
}
