using System;

namespace Hotkey_Translator.Services;

public sealed class AppLogger
{
    private readonly Action<string> _sink;
    private volatile bool _enabled = true;

    public AppLogger(Action<string> sink)
    {
        _sink = sink;
    }

    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
    }

    public void Info(string message)
    {
        if (!_enabled)
        {
            return;
        }

        _sink($"{DateTime.Now:HH:mm:ss.fff} {message}");
    }

    public void Error(string message)
    {
        if (!_enabled)
        {
            return;
        }

        _sink($"{DateTime.Now:HH:mm:ss.fff} ERROR: {message}");
    }

    public void Error(Exception ex, string message)
    {
        if (!_enabled)
        {
            return;
        }

        _sink($"{DateTime.Now:HH:mm:ss.fff} ERROR: {message} {ex.Message}");
    }
}
