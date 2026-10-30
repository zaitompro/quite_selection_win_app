using System.Text.Json;
using System.Windows;
using QuietSelection.App.Workers;

namespace QuietSelection.App;

/// <summary>
/// Windows OCR on synthetic images that resemble hard screen captures: small UI text,
/// dark and low-contrast themes, crops touching the glyphs and text in another language.
/// </summary>
internal static class OcrChecks
{
    internal static readonly (string Id, string Text, string? Fixture, string Language)[] Cases =
    [
        ("ocr.small_ru", "Настройки чтения экрана", "small", "ru"),
        ("ocr.dark_ru", "Тёмная тема интерфейса", "dark", "ru"),
        ("ocr.low_contrast_ru", "Подпись с низким контрастом", "low-contrast", "ru"),
        ("ocr.tight_crop_ru", "Обрезка вплотную к тексту", "tight", "ru"),
        ("ocr.small_en", "Small English settings label", "small", "en-US"),
        ("ocr.dark_en", "Dark mode window title", "dark", "en-US"),
        ("ocr.english_with_ru_setting", "Reading test for English", null, "ru"),
        ("ocr.japanese_with_ru_setting", "読み上げのテストです", null, "ru"),
        ("ocr.japanese_small", "設定画面の小さな文字", "small", "ja"),
    ];

    /// <summary>Spaces, case and ё/е differences are ignored; most characters must match in order.</summary>
    internal static bool Matches(string expected, string actual)
    {
        static string Normalize(string s) => new(s.ToLowerInvariant().Replace('ё', 'е').Where(char.IsLetterOrDigit).ToArray());
        string a = Normalize(expected), b = Normalize(actual);
        if (b.Contains(a, StringComparison.Ordinal)) return true;
        // Longest common subsequence ratio tolerates a single misread glyph.
        var row = new int[b.Length + 1];
        foreach (char x in a)
        {
            int previous = 0;
            for (int j = 1; j <= b.Length; j++) { int current = row[j]; row[j] = x == b[j - 1] ? previous + 1 : Math.Max(row[j], row[j - 1]); previous = current; }
        }
        return a.Length > 0 && row[b.Length] >= a.Length * 0.9;
    }

    internal static async Task<bool> RunAsync(WorkerClient worker, List<object> checks)
    {
        bool failed = false;
        foreach (var (id, text, fixture, language) in Cases)
        {
            string actual = ""; string error = "";
            try { actual = (await worker.CallAsync(new("ocr-fixture", Text: text, Language: language, Fixture: fixture), TimeSpan.FromSeconds(30), CancellationToken.None)).Text; }
            catch (QuietException ex) { error = ex.Code.ToString(); }
            bool passed = Matches(text, actual);
            failed |= !passed;
            // Synthetic text only, so the recognized text can be reported for diagnosis.
            checks.Add(new { id, passed, fixture, language, recognized = actual, error });
        }
        return !failed;
    }

    internal static void Run(Application app, string[] args)
    {
        int index = Array.IndexOf(args, "--report-directory");
        string directory = Path.GetFullPath(args[index + 1]);
        Directory.CreateDirectory(directory);
        app.Startup += async (_, _) =>
        {
            var checks = new List<object>();
            using var worker = new WorkerClient();
            bool passed = await RunAsync(worker, checks);
            await File.WriteAllTextAsync(Path.Combine(directory, "ocr.json"), JsonSerializer.Serialize(new { syntheticDataOnly = true, checks },
                new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            app.Shutdown(passed ? 0 : 1);
        };
    }
}
