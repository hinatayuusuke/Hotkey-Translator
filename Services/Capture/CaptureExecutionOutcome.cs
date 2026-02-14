using System;
using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services.Capture;

internal readonly record struct CaptureExecutionOutcome(
    CaptureFrame? Frame,
    Exception? LastError,
    CaptureExecutionTrace Trace)
{
    public bool Success => Frame is not null;
}
