using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuietSelection.Core;

public sealed class LanguageSettings
{
    public string Language { get; set; } = "ru";
    public string VoiceId { get; set; } = "";
    public int Speed { get; set; } = 50;
    public bool Enabled { get; set; } = true;
}

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;
    public LanguageSettings Primary { get; set; } = new();
    public bool AutoDetect { get; set; } = true;
    public bool KeepPiperLoaded { get; set; } = true;
    public bool KeepSileroLoaded { get; set; } = true;
    public bool KeepAdditionalSpeechLoaded { get; set; } = true;
    public List<LanguageSettings> Additional { get; set; } =
        [new() { Language = "en" }, new() { Language = "ja" }];
    public Dictionary<ReadingCommand, string> Hotkeys { get; set; } = [];
    public bool StartAtLogin { get; set; }
    public bool ShowReadingIndicator { get; set; } = true;
    public string OcrLanguage { get; set; } = "ru";

    public AppSettings Snapshot() => JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this))!;
    [JsonIgnore]
    public IEnumerable<LanguageSettings> ActiveLanguages =>
        new[] { Primary }.Concat(Additional.Where(x => x.Enabled));

    public void Validate()
    {
        if (SchemaVersion != 1 || Primary is null || Additional is null || Hotkeys is null)
            throw new QuietException(ErrorCode.InvalidSettings);
        var all = new[] { Primary }.Concat(Additional).ToArray();
        if (all.Any(x => x is null || !SupportedLanguages.ContainsKey(x.Language) || x.Speed is < 0 or > 100 || x.VoiceId is null) ||
            all.Select(x => x.Language).Distinct().Count() != all.Length ||
            !SupportedLanguages.ContainsKey(OcrLanguage) ||
            Hotkeys.Values.Where(x => x.Length > 0).Distinct().Count() != Hotkeys.Values.Count(x => x.Length > 0))
            throw new QuietException(ErrorCode.InvalidSettings);
    }

    public static readonly IReadOnlyDictionary<string, string> SupportedLanguages =
        new Dictionary<string, string> { ["en"] = "Английский", ["ru"] = "Русский", ["ja"] = "Японский" };
}

public sealed class SettingsStore(string path)
{
    public AppSettings Load()
    {
        if (!File.Exists(path)) return new();
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path))
                ?? throw new QuietException(ErrorCode.InvalidSettings);
            settings.Validate();
            return settings;
        }
        catch (QuietException) { throw; }
        catch { throw new QuietException(ErrorCode.InvalidSettings); }
    }

    /// <summary>
    /// Keeps an unreadable settings file for inspection and frees the path for defaults.
    /// Returns the new location, or null when there was no file.
    /// </summary>
    public string? MoveAside()
    {
        if (!File.Exists(path)) return null;
        string target = path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        for (int i = 1; File.Exists(target); i++) target = path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + "-" + i;
        File.Move(path, target);
        return target;
    }

    public void Save(AppSettings settings)
    {
        settings.Validate();
        var parent = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(parent);
        var temp = Path.Combine(parent, Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(settings, new JsonSerializerOptions { WriteIndented = true });
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temp, path, path + ".previous");
            else File.Move(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
