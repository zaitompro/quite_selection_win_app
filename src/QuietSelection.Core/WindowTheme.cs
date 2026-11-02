namespace QuietSelection.Core;

/// <summary>
/// Dark overlays for light windows. A rule is "process|window class", for example
/// "explorer|#32770". Rules only apply to windows that are actually light when they open.
/// </summary>
public sealed class WindowThemeSettings
{
    public const int MaximumRules = 64;
    /// <summary>Automatically darken classic property sheets (file, folder and drive properties).</summary>
    public bool Properties { get; set; }
    /// <summary>Remember the window type when a window is darkened with the shortcut.</summary>
    public bool Remember { get; set; } = true;
    public List<string> Rules { get; set; } = [];

    public static string Rule(string process, string windowClass) => process.ToLowerInvariant() + "|" + windowClass;

    public static bool IsValidRule(string? rule) =>
        rule is { Length: > 2 and <= 300 } && rule.Count(c => c == '|') == 1 && rule[0] != '|' && rule[^1] != '|';

    public bool Validate() => Rules is not null && Rules.Count <= MaximumRules && Rules.All(IsValidRule) && Rules.Distinct().Count() == Rules.Count;
}
