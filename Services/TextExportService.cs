using System.Text;
using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services;

public static class TextExportService
{
    public static string BuildPairedText(TextExportSnapshot snapshot)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < snapshot.Items.Count; i++)
        {
            var item = snapshot.Items[i];
            if (i > 0)
            {
                builder.AppendLine();
            }

            builder.Append('[').Append(i + 1).AppendLine("]");
            builder.AppendLine("Original:");
            builder.AppendLine(NormalizeText(item.OriginalText));
            builder.AppendLine();
            builder.AppendLine("Translation:");
            builder.Append(NormalizeText(item.TranslationText));
            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    private static string NormalizeText(string text)
    {
        return (text ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
    }
}
