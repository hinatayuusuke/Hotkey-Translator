using System;
using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services.Capture;

internal readonly record struct CaptureAttemptResult(
    CaptureProviderKind ProviderKind,
    bool Success,
    CaptureFailureReason Reason,
    string? Detail = null,
    bool IsBlackFrame = false,
    DateTimeOffset? CooldownUntil = null);
