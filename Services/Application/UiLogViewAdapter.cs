using System;
using System.Windows.Controls;

namespace Hotkey_Translator.Services.Application;

internal sealed class UiLogViewAdapter
{
    private readonly Func<TextBox?> _logBoxAccessor;
    private readonly int _maxLines;
    private int _logLineCount;

    public UiLogViewAdapter(Func<TextBox?> logBoxAccessor, int maxLines)
    {
        _logBoxAccessor = logBoxAccessor;
        _maxLines = maxLines;
    }

    public void FlushPayload(string payload)
    {
        var logBox = _logBoxAccessor();
        if (logBox == null)
        {
            return;
        }

        logBox.AppendText(payload);
        _logLineCount += CountNewlines(payload);
        TrimLogLines(logBox);
        logBox.ScrollToEnd();
    }

    private void TrimLogLines(TextBox logBox)
    {
        if (_maxLines <= 0 || _logLineCount <= _maxLines)
        {
            return;
        }

        var removeLines = _logLineCount - _maxLines;
        var text = logBox.Text;
        var cutIndex = IndexOfNthNewline(text, removeLines);
        if (cutIndex < 0)
        {
            _logLineCount = CountLines(text);
            return;
        }

        logBox.Text = text[(cutIndex + 1)..];
        _logLineCount = _maxLines;
    }

    private static int IndexOfNthNewline(string text, int count)
    {
        if (string.IsNullOrEmpty(text) || count <= 0)
        {
            return -1;
        }

        var index = -1;
        var remaining = count;
        while (remaining > 0)
        {
            index = text.IndexOf('\n', index + 1);
            if (index < 0)
            {
                return -1;
            }

            remaining--;
        }

        return index;
    }

    private static int CountNewlines(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var count = 0;
        foreach (var ch in text)
        {
            if (ch == '\n')
            {
                count++;
            }
        }

        return count;
    }

    private static int CountLines(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var lines = CountNewlines(text);
        return text.EndsWith("\n", StringComparison.Ordinal) ? lines : lines + 1;
    }
}
