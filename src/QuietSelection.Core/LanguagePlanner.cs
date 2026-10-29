using System.Text.RegularExpressions;

namespace QuietSelection.Core;

/// <summary>
/// Conservative script-based planner for the explicitly supported ru/en/ja set.
/// Han-only, short Latin, and unrecognised scripts are deliberately uncertain.
/// It is not a statistical detector and never infers document context.
/// </summary>
public static partial class LanguagePlanner
{
    [GeneratedRegex(@"(?:[^.!?。！？\r\n]|(?<=[0-9])\.(?=[0-9]))+[.!?。！？]*[\r\n]*|[.!?。！？\r\n]+")]
    private static partial Regex Sentences();

    public static IReadOnlyList<DetectedSegment> Detect(string text, IEnumerable<string> candidates)
    {
        var allowed = candidates.ToHashSet(StringComparer.Ordinal);
        return Sentences().Matches(text).Select(m => DetectSegment(m.Value, allowed)).ToArray();
    }

    private static DetectedSegment DetectSegment(string text, HashSet<string> allowed)
    {
        int cyrillic = 0, latin = 0, kana = 0, han = 0, other = 0;
        foreach (char c in text.Where(char.IsLetter))
        {
            if (c is >= '\u0400' and <= '\u04ff') cyrillic++;
            else if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z') latin++;
            else if (c is >= '\u3040' and <= '\u30ff') kana++;
            else if (c is >= '\u3400' and <= '\u9fff') han++;
            else other++;
        }
        bool letters = cyrillic + latin + kana + han + other > 0;
        string? language = null;
        if (other == 0)
        {
            if (kana > 0 && cyrillic == 0 && latin == 0 && allowed.Contains("ja")) language = "ja";
            else if (cyrillic >= 2 && kana + han + latin == 0 && allowed.Contains("ru")) language = "ru";
            else if (latin >= 6 && cyrillic + kana + han == 0 && allowed.Contains("en")) language = "en";
        }
        return new(text, language, letters);
    }

    public static IReadOnlyList<SpeechSegment> Plan(string text, AppSettings settings, bool primaryOnly,
        IReadOnlyList<VoiceInfo> installed)
    {
        settings.Validate();
        if (text.Length > TextLimits.Maximum) throw new QuietException(ErrorCode.TooLong);
        if (string.IsNullOrWhiteSpace(text)) throw new QuietException(ErrorCode.NoSelection);
        var active = settings.ActiveLanguages.ToDictionary(x => x.Language);
        var detected = Detect(text, active.Keys);
        var result = new List<SpeechSegment>();
        foreach (var fragment in detected)
        {
            var language = primaryOnly || !settings.AutoDetect || fragment.Language is null
                ? settings.Primary.Language : fragment.Language;
            var parts = primaryOnly || !settings.AutoDetect
                ? new[] { new DetectedSegment(fragment.Text, language, fragment.HasLetters) }
                : SplitSpeechScripts(fragment.Text, language, active.Keys.ToHashSet());
            foreach (var part in parts)
            {
                var selected = part.Language ?? language;
                var voice = active[selected];
                if (string.IsNullOrEmpty(voice.VoiceId) ||
                    !installed.Any(x => x.Id == voice.VoiceId && x.Language.Split('-')[0] == selected))
                    throw new QuietException(ErrorCode.VoiceMissing);
                result.Add(new(part.Text, selected, voice.VoiceId, voice.Speed));
            }
        }
        return result;
    }

    private static string? Script(char c) => !char.IsLetter(c) ? null : c switch
    {
        >= '\u0400' and <= '\u04ff' => "ru",
        >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '\u00c0' and <= '\u024f' => "en",
        >= '\u3040' and <= '\u30ff' or >= '\u3400' and <= '\u9fff' or >= '\uff66' and <= '\uff9d' => "ja",
        _ => null
    };

    // Speech alone uses script boundaries. Translation keeps its conservative
    // detector: a Han character is not proof that a source text is Japanese.
    private static IEnumerable<DetectedSegment> SplitSpeechScripts(string text, string fallback, HashSet<string> active)
    {
        var scripts = text.Select(Script).OfType<string>().Distinct().ToArray();
        if (scripts.Length < 2)
        {
            yield return new(text, fallback, true);
            yield break;
        }
        int start = 0;
        string? current = null;
        for (int i = 0; i < text.Length; i++)
        {
            var script = Script(text[i]);
            if (script is null) continue;
            string next = active.Contains(script) ? script : fallback;
            if (current is not null && next != current)
            {
                yield return new(text[start..i], current, true);
                start = i;
            }
            current = next;
        }
        if (start < text.Length) yield return new(text[start..], current ?? fallback, true);
    }

    public static double SpeakingRate(int value)
    {
        if (value is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(value));
        return value <= 50 ? 0.5 + value / 100.0 : 1.0 + (value - 50) / 10.0;
    }

    public static IEnumerable<SpeechSegment> BatchForPrefetch(IEnumerable<SpeechSegment> segments)
    {
        SpeechSegment? current = null;
        var text = new System.Text.StringBuilder();
        foreach (var segment in segments)
        {
            bool canMerge = current is not null && current.VoiceId == segment.VoiceId
                && current.Language == segment.Language && current.Speed == segment.Speed
                // Keep existing per-request timeouts meaningful for long documents.
                && text.Length + segment.Text.Length <= 1000
                && (segment.VoiceId.StartsWith("kokoro:", StringComparison.Ordinal)
                    || segment.VoiceId.StartsWith("kitten:", StringComparison.Ordinal)
                    || segment.VoiceId.StartsWith("pocket:", StringComparison.Ordinal)
                    || segment.VoiceId.StartsWith("voicevox:", StringComparison.Ordinal));
            if (!canMerge)
            {
                if (current is not null) yield return current with { Text = text.ToString() };
                current = segment;
                text.Clear();
            }
            text.Append(segment.Text);
        }
        if (current is not null) yield return current with { Text = text.ToString() };
    }

    public static string TestPhrase(string language) => language switch
    {
        "ru" => "Это тестовая фраза QuietSelection. Скорость можно изменить.",
        "en" => "This is a QuietSelection test sentence. You can change the speaking speed.",
        "ja" => "これは読み上げのテストです。話す速度を変更できます。",
        _ => throw new QuietException(ErrorCode.VoiceMissing)
    };
}
