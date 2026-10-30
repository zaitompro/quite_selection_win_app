using System.Globalization;

namespace QuietSelection.App.Views;

/// <summary>User-facing numbers always use Russian formatting, whatever the system culture.</summary>
internal static class TextFormat
{
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");
    internal static string Megabytes(long bytes) => (bytes / 1_000_000.0).ToString(bytes >= 100_000_000 ? "0" : "0.0", Russian) + " МБ";
    internal static string Voices(int count) => count + " " + (count % 10 == 1 && count % 100 != 11 ? "голос"
        : count % 10 is >= 2 and <= 4 && count % 100 is < 12 or > 14 ? "голоса" : "голосов");
}
