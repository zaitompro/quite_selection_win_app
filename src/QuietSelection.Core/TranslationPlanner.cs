namespace QuietSelection.Core;

public sealed record TranslationSegment(string Text, string Source);

public static class TranslationPlanner
{
    public static bool IsSupported(string source, string target) => source == target ||
        (source, target) is ("en", "ru") or ("ja", "en") or ("ja", "ru");

    public static IReadOnlyList<TranslationSegment> Plan(string text, AppSettings settings, string? uncertainSource = null)
    {
        settings.Validate();
        if (text.Length > TextLimits.Maximum) throw new QuietException(ErrorCode.TooLong);
        if (string.IsNullOrWhiteSpace(text)) throw new QuietException(ErrorCode.NoSelection);
        var candidates = settings.ActiveLanguages.Select(x => x.Language).ToArray();
        if (uncertainSource is not null && !candidates.Contains(uncertainSource))
            throw new QuietException(ErrorCode.SourceLanguageRequired);
        var segments = LanguagePlanner.Detect(text, candidates).Select(x => new TranslationSegment(x.Text,
            !x.HasLetters ? settings.Primary.Language : x.Language ?? uncertainSource ?? throw new QuietException(ErrorCode.SourceLanguageRequired))).ToArray();
        if (segments.Any(x => !IsSupported(x.Source, settings.Primary.Language))) throw new QuietException(ErrorCode.UnsupportedPair);
        return segments;
    }
}
