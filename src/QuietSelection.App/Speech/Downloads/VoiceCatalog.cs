namespace QuietSelection.App.Speech.Downloads;

internal enum VoicePackageKind { Piper, Silero, Additional, RhVoice, Windows }

/// <summary>One installable voice set as shown in the voice installation window.</summary>
internal sealed record VoicePackageInfo(string Key, string Title, string Languages, int? VoiceCount, string Summary,
    string License, string LicenseBadge, bool Restricted, long? Bytes, string Source, VoicePackageKind Kind,
    SpeechEngine? Engine = null, RhVoicePackage? RhVoice = null, string? WindowsLanguage = null);

/// <summary>Display data only. Exact terms are confirmed in the dialog before each download.</summary>
internal static class VoiceCatalog
{
    internal static IReadOnlyList<VoicePackageInfo> All { get; } = Build();

    private static VoicePackageInfo[] Build()
    {
        static long Size(SpeechEngine engine) => AdditionalModels.Catalog(engine).Files.Sum(f => f.Bytes);
        static int Count(SpeechEngine engine) => AdditionalModels.Catalog(engine).Voices.Length;
        var list = new List<VoicePackageInfo>
        {
            new("silero", "Silero", "Русский", SileroModels.Catalog.Voices.Length,
                "Айдар, Байя, Ксения, Xenia, Евгений. Естественное звучание, одна общая модель.",
                "CC BY-NC-SA 4.0", "Некоммерческая", true, SileroModels.Catalog.File.Bytes, "Silero", VoicePackageKind.Silero),
            new("piper", "Piper", "Русский, английский", PiperModels.Catalog.Length,
                "Denis, Dmitri, Irina, Ruslan, LJSpeech. Компактные и быстрые.",
                "Движок GPL-3.0; корпуса CC0, public domain, Ruslan — CC BY-NC-SA 4.0", "Частично некоммерческая", true,
                PiperModels.DownloadBytes, "Hugging Face", VoicePackageKind.Piper),
            new("kokoro", "Kokoro", "Английский", Count(SpeechEngine.Kokoro),
                "Голоса США и Великобритании, одна модель INT8.", "Apache 2.0", "Свободная", false,
                Size(SpeechEngine.Kokoro), "GitHub", VoicePackageKind.Additional, SpeechEngine.Kokoro),
            new("kitten", "KittenTTS Mini", "Английский", Count(SpeechEngine.Kitten),
                "Четыре женских и четыре мужских голоса, быстрый отклик.", "Apache 2.0", "Свободная", false,
                Size(SpeechEngine.Kitten), "Hugging Face", VoicePackageKind.Additional, SpeechEngine.Kitten),
            new("pocket", "Pocket TTS", "Английский", Count(SpeechEngine.Pocket),
                "Потоковое чтение. Cosette и Jean — только некоммерческое использование.",
                "Модель CC BY 4.0, код MIT", "Частично некоммерческая", true,
                Size(SpeechEngine.Pocket), "Hugging Face", VoicePackageKind.Additional, SpeechEngine.Pocket),
            new("voicevox", "VOICEVOX", "Японский", Count(SpeechEngine.Voicevox),
                "四国めたん и 玄野武宏, обычные варианты. Для распространяемого аудио нужен кредит голоса.",
                "Условия VOICEVOX и персонажей", "Особые условия", true,
                Size(SpeechEngine.Voicevox), "GitHub", VoicePackageKind.Additional, SpeechEngine.Voicevox),
        };
        foreach (var package in RhVoiceInstallation.Packages)
            list.Add(new("rhvoice-" + package.Language, "RHVoice " + package.Name, AppSettings.SupportedLanguages[package.Language], 1,
                "Системный голос SAPI 5. Нужны права администратора.", package.License, "Свободная", false,
                package.Bytes, "GitHub", VoicePackageKind.RhVoice, RhVoice: package));
        foreach (var language in new[] { "ru", "en", "ja" })
            list.Add(new("windows-" + language, "Голос Windows", AppSettings.SupportedLanguages[language], null,
                "Компоненты речи из Windows Update. Нужны права администратора.", "Лицензия Windows", "Windows", false,
                null, "Windows Update", VoicePackageKind.Windows, WindowsLanguage: language));
        return [.. list];
    }

    /// <summary>Installed state from the current voice list; no files are hashed here.</summary>
    internal static bool IsInstalled(VoicePackageInfo package, IReadOnlyList<VoiceInfo> voices) => package.Kind switch
    {
        VoicePackageKind.Piper => voices.Any(v => SpeechEngines.FromVoiceId(v.Id) == SpeechEngine.Piper),
        VoicePackageKind.Silero => voices.Any(v => SileroModels.Catalog.Voices.Any(s => s.Id == v.Id)),
        VoicePackageKind.Additional => voices.Any(v => SpeechEngines.FromVoiceId(v.Id) == package.Engine),
        VoicePackageKind.RhVoice => voices.Any(v => v.Id.StartsWith("sapi5:", StringComparison.Ordinal)
            && v.Name.Contains(package.RhVoice!.Name, StringComparison.OrdinalIgnoreCase)),
        _ => voices.Any(v => SpeechEngines.FromVoiceId(v.Id) is null && !v.Id.StartsWith("sapi5:", StringComparison.Ordinal)
            && v.Language.Split('-')[0] == package.WindowsLanguage)
    };
}
