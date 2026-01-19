using System;

namespace Hotkey_Translator.Services;

public sealed class AppLogger
{
    private readonly Action<string> _sink;

    public AppLogger(Action<string> sink)
    {
        _sink = sink;
    }

    public void Info(string message)
    {
        _sink($"{DateTime.Now:HH:mm:ss} {message}");
    }

    public void Error(string message)
    {
        _sink($"{DateTime.Now:HH:mm:ss} ERROR: {message}");
    }

    public void Error(Exception ex, string message)
    {
        _sink($"{DateTime.Now:HH:mm:ss} ERROR: {message} {ex.Message}");
    }
}
