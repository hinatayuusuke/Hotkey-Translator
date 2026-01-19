using System.Text;

namespace Hotkey_Translator.Services;

public sealed class NormalizationService
{
    public string Normalize(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var normalized = input.Normalize(NormalizationForm.FormKC);
        var builder = new StringBuilder(normalized.Length);
        var lastWasSpace = false;

        foreach (var ch in normalized)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!lastWasSpace)
                {
                    builder.Append(' ');
                    lastWasSpace = true;
                }

                continue;
            }

            lastWasSpace = false;
            if (char.IsPunctuation(ch) || char.IsSymbol(ch))
            {
                continue;
            }

            builder.Append(char.ToLowerInvariant(ch));
        }

        return builder.ToString().Trim();
    }
}
