namespace QuietSelection.Core;

public sealed record TranslationSegment(string Text, string Source);

public static class TranslationPlanner
{
    public static bool IsSupported(string source, string target) => source == target ||
        (source, target) is ("en", "ru") or ("ja", "en") or ("ja", "ru");

    /// <summary>
    /// Assigns a source language to every sentence. Sentences the strict detector leaves open are
    /// resolved from their own dominant script, then from neighbouring sentences; only text that
    /// still has no clue (for example Han characters without kana, or an unsupported script)
    /// needs <paramref name="uncertainSource"/>, chosen by the user for this operation.
    /// </summary>
    public static IReadOnlyList<TranslationSegment> Plan(string text, AppSettings settings, string? uncertainSource = null)
    {
        settings.Validate();
        if (text.Length > TextLimits.Maximum) throw new QuietException(ErrorCode.TooLong);
        if (string.IsNullOrWhiteSpace(text)) throw new QuietException(ErrorCode.NoSelection);
        var candidates = settings.ActiveLanguages.Select(x => x.Language).ToHashSet(StringComparer.Ordinal);
        if (uncertainSource is not null && !candidates.Contains(uncertainSource))
            throw new QuietException(ErrorCode.SourceLanguageRequired);
        var detected = LanguagePlanner.Detect(text, candidates);
        bool documentHasKana = text.Any(IsKana);
        var letters = detected.Select(x => Count(x.Text)).ToArray();
        var sources = detected.Select((x, i) => !x.HasLetters ? settings.Primary.Language : x.Language ?? Dominant(letters[i], candidates, documentHasKana)).ToArray();
        // Remaining sentences follow the nearest known sentence whose language uses the same scripts.
        for (int i = 0; i < sources.Length; i++)
        {
            if (sources[i] is not null) continue;
            for (int distance = 1; distance < sources.Length && sources[i] is null; distance++)
                sources[i] = Known(i - distance, letters[i]) ?? Known(i + distance, letters[i]);
        }
        string? Known(int index, Letters own) => index >= 0 && index < detected.Count && detected[index].HasLetters
            && detected[index].Language is { } language && Writes(language, own) ? language : null;
        var segments = detected.Select((x, i) => new TranslationSegment(x.Text,
            sources[i] ?? uncertainSource ?? throw new QuietException(ErrorCode.SourceLanguageRequired))).ToArray();
        if (segments.Any(x => !IsSupported(x.Source, settings.Primary.Language))) throw new QuietException(ErrorCode.UnsupportedPair);
        return segments;
    }

    private readonly record struct Letters(int Cyrillic, int Latin, int Kana, int Han, int Other)
    {
        public int Total => Cyrillic + Latin + Kana + Han + Other;
    }

    // Whether a language is written with all the scripts present (names in Latin are common everywhere).
    private static bool Writes(string language, Letters x) => x.Other == 0 && language switch
    {
        "ru" => x.Kana + x.Han == 0,
        "en" => x.Cyrillic + x.Kana + x.Han == 0,
        "ja" => x.Cyrillic == 0,
        _ => false
    };

    private static Letters Count(string text)
    {
        int cyrillic = 0, latin = 0, kana = 0, han = 0, other = 0;
        foreach (char c in text.Where(char.IsLetter))
        {
            if (c is >= '\u0400' and <= '\u04FF') cyrillic++;
            else if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '\u00C0' and <= '\u024F') latin++;
            else if (IsKana(c)) kana++;
            else if (c is >= '\u3400' and <= '\u9FFF') han++;
            else other++;
        }
        return new(cyrillic, latin, kana, han, other);
    }

    // The language of a sentence the strict detector left open, from its own letters.
    private static string? Dominant(Letters x, HashSet<string> candidates, bool documentHasKana)
    {
        var (cyrillic, latin, kana, han, total) = (x.Cyrillic, x.Latin, x.Kana, x.Han, x.Total);
        if (total == 0) return null;
        if (kana > 0 && candidates.Contains("ja")) return "ja";
        // A Russian sentence with an English product name stays Russian, and vice versa.
        if (cyrillic >= total * 0.6 && candidates.Contains("ru")) return "ru";
        if (latin >= total * 0.6 && han == 0 && candidates.Contains("en")) return "en";
        // Han without kana could be Chinese; it is Japanese only when the text has kana elsewhere.
        if (han == total && documentHasKana && candidates.Contains("ja")) return "ja";
        return null;
    }

    private static bool IsKana(char c) => c is >= '\u3040' and <= '\u30FF';
}
