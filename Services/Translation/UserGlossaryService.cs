using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services;

public sealed class UserGlossaryService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IReadOnlyList<UserGlossaryEntry> _entries;

    public UserGlossaryService(string glossaryDirectoryPath, AppLogger? logger = null)
    {
        Directory.CreateDirectory(glossaryDirectoryPath);
        _entries = LoadEntries(glossaryDirectoryPath, logger);
    }

    public GlossaryPreparedText Prepare(string translationSourceText, AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(translationSourceText))
        {
            return GlossaryPreparedText.Empty(translationSourceText);
        }

        var entries = GetApplicableEntries(settings);
        if (entries.Count == 0)
        {
            return GlossaryPreparedText.Empty(translationSourceText);
        }

        var replacements = FindReplacements(translationSourceText, entries);
        if (replacements.Count == 0)
        {
            return GlossaryPreparedText.Empty(translationSourceText);
        }

        var builder = new StringBuilder(translationSourceText.Length + (replacements.Count * 12));
        var glossaryReplacements = new List<GlossaryReplacement>(replacements.Count);
        var cursor = 0;
        for (var i = 0; i < replacements.Count; i++)
        {
            var replacement = replacements[i];
            builder.Append(translationSourceText, cursor, replacement.Start - cursor);
            var placeholder = BuildPlaceholder(i);
            builder.Append(placeholder);
            glossaryReplacements.Add(new GlossaryReplacement(replacement.SourceTerm, replacement.TargetTerm, placeholder));
            cursor = replacement.Start + replacement.Length;
        }

        builder.Append(translationSourceText, cursor, translationSourceText.Length - cursor);
        return new GlossaryPreparedText(translationSourceText, builder.ToString(), glossaryReplacements);
    }

    public GlossaryRestoreResult Restore(string translatedText, GlossaryPreparedText prepared)
    {
        if (string.IsNullOrEmpty(translatedText) || prepared.Replacements.Count == 0)
        {
            return new GlossaryRestoreResult(translatedText, 0, 0);
        }

        var restoredText = translatedText;
        var restoredCount = 0;
        var unresolvedCount = 0;
        foreach (var replacement in prepared.Replacements)
        {
            if (!restoredText.Contains(replacement.Placeholder, StringComparison.Ordinal))
            {
                unresolvedCount++;
                continue;
            }

            var updated = restoredText.Replace(replacement.Placeholder, replacement.TargetTerm, StringComparison.Ordinal);
            if (!string.Equals(updated, restoredText, StringComparison.Ordinal))
            {
                restoredText = updated;
                restoredCount++;
            }
        }

        return new GlossaryRestoreResult(restoredText, restoredCount, unresolvedCount);
    }

    public string BuildGlossaryScope(AppSettings settings)
    {
        var version = string.IsNullOrWhiteSpace(settings.GlossaryVersion) ? "v1" : settings.GlossaryVersion.Trim();
        var canonicalEntries = GetApplicableEntries(settings)
            .Select(entry =>
                $"{entry.SourceLanguage ?? "*"}|{entry.TargetLanguage ?? "*"}|{entry.Priority}|{entry.SourceTerm}|{entry.TargetTerm}")
            .ToList();
        if (canonicalEntries.Count == 0)
        {
            return version;
        }

        var payload = Encoding.UTF8.GetBytes(string.Join('\n', canonicalEntries));
        var hashBytes = SHA256.HashData(payload);
        var hash = Convert.ToHexString(hashBytes.AsSpan(0, 6));
        // WHY: Include a deterministic entry hash so folder edits invalidate cache even without file watchers.
        return $"{version}_{hash}";
    }

    private static IReadOnlyList<UserGlossaryEntry> LoadEntries(string glossaryDirectoryPath, AppLogger? logger)
    {
        var loaded = new List<UserGlossaryEntry>();
        foreach (var filePath in Directory.EnumerateFiles(glossaryDirectoryPath, "*.json", SearchOption.TopDirectoryOnly)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var json = File.ReadAllText(filePath);
                var wrapper = JsonSerializer.Deserialize<UserGlossaryFile>(json, JsonOptions);
                if (wrapper?.Entries != null)
                {
                    loaded.AddRange(wrapper.Entries);
                    continue;
                }

                var entries = JsonSerializer.Deserialize<List<UserGlossaryEntry>>(json, JsonOptions);
                if (entries != null)
                {
                    loaded.AddRange(entries);
                }
            }
            catch (Exception ex)
            {
                // NOTE: Glossary files are optional user assets. Ignore a broken file and keep startup fast.
                logger?.Info($"glossary_load skipped path=\"{filePath}\" reason=\"{ex.GetType().Name}\".");
            }
        }

        if (loaded.Count > 0)
        {
            logger?.Info($"glossary_load loaded entries={loaded.Count}.");
        }

        return loaded;
    }

    private List<UserGlossaryEntry> GetApplicableEntries(AppSettings settings)
    {
        return _entries
            .Where(entry => entry.Enabled)
            .Where(entry => !string.IsNullOrWhiteSpace(entry.SourceTerm) && !string.IsNullOrWhiteSpace(entry.TargetTerm))
            .Where(entry => LanguageMatches(entry.SourceLanguage, settings.SourceLanguage))
            .Where(entry => LanguageMatches(entry.TargetLanguage, settings.TargetLanguage))
            .OrderByDescending(entry => entry.SourceTerm.Length)
            .ThenBy(entry => entry.Priority)
            .ThenBy(entry => entry.SourceTerm, StringComparer.Ordinal)
            .ToList();
    }

    private static bool LanguageMatches(string? filter, string currentLanguage)
    {
        return string.IsNullOrWhiteSpace(filter) || string.Equals(filter, currentLanguage, StringComparison.OrdinalIgnoreCase);
    }

    private static List<ReplacementCandidate> FindReplacements(string sourceText, IReadOnlyList<UserGlossaryEntry> entries)
    {
        var occupied = new bool[sourceText.Length];
        var replacements = new List<ReplacementCandidate>();
        foreach (var entry in entries)
        {
            var sourceTerm = entry.SourceTerm;
            var startIndex = 0;
            while (startIndex < sourceText.Length)
            {
                var matchIndex = sourceText.IndexOf(sourceTerm, startIndex, StringComparison.OrdinalIgnoreCase);
                if (matchIndex < 0)
                {
                    break;
                }

                if (!IsRangeAvailable(occupied, matchIndex, sourceTerm.Length) ||
                    !MatchesTermBoundary(sourceText, matchIndex, sourceTerm.Length, sourceTerm))
                {
                    startIndex = matchIndex + 1;
                    continue;
                }

                replacements.Add(new ReplacementCandidate(matchIndex, sourceTerm.Length, sourceTerm, entry.TargetTerm));
                MarkRange(occupied, matchIndex, sourceTerm.Length);
                startIndex = matchIndex + sourceTerm.Length;
            }
        }

        replacements.Sort((left, right) => left.Start.CompareTo(right.Start));
        return replacements;
    }

    private static bool IsRangeAvailable(bool[] occupied, int start, int length)
    {
        for (var i = 0; i < length; i++)
        {
            if (occupied[start + i])
            {
                return false;
            }
        }

        return true;
    }

    private static void MarkRange(bool[] occupied, int start, int length)
    {
        for (var i = 0; i < length; i++)
        {
            occupied[start + i] = true;
        }
    }

    private static bool MatchesTermBoundary(string sourceText, int start, int length, string sourceTerm)
    {
        if (!sourceTerm.All(IsAsciiWordChar))
        {
            return true;
        }

        var end = start + length;
        var hasLeftBoundary = start == 0 || !IsAsciiWordChar(sourceText[start - 1]);
        var hasRightBoundary = end >= sourceText.Length || !IsAsciiWordChar(sourceText[end]);
        // WHY: ASCII-only glossary terms tend to be proper nouns or UI labels, so requiring word boundaries
        // avoids accidental hits such as "fire" inside "fireball" without penalizing CJK terms.
        return hasLeftBoundary && hasRightBoundary;
    }

    private static bool IsAsciiWordChar(char value)
    {
        return value is >= '0' and <= '9'
            or >= 'A' and <= 'Z'
            or >= 'a' and <= 'z'
            or '_';
    }

    private static string BuildPlaceholder(int index)
    {
        return $"__HTG_{index:D4}__";
    }

    private sealed record UserGlossaryFile(string? Name, List<UserGlossaryEntry>? Entries);

    public sealed record GlossaryPreparedText(
        string OriginalSourceText,
        string ProtectedSourceText,
        IReadOnlyList<GlossaryReplacement> Replacements)
    {
        public static GlossaryPreparedText Empty(string originalSourceText)
            => new(originalSourceText, originalSourceText, Array.Empty<GlossaryReplacement>());

        public int HitCount => Replacements.Count;
    }

    public sealed record GlossaryReplacement(string SourceTerm, string TargetTerm, string Placeholder);

    public sealed record GlossaryRestoreResult(string Text, int RestoredCount, int UnresolvedCount);

    private sealed record ReplacementCandidate(int Start, int Length, string SourceTerm, string TargetTerm);
}
