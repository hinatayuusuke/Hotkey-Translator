using System;

namespace Hotkey_Translator.Services;

// NOTE: Only helper/runtime failures reach the repair UI; cancellation and empty OCR results do not.
internal sealed class OneOcrUnavailableException : Exception
{
    public OneOcrUnavailableException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
