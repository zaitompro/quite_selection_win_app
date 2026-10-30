namespace QuietSelection.App.Speech;

/// <summary>The one place that maps voice IDs (<c>engine:...</c>) to Python speech engines.</summary>
internal static class SpeechEngines
{
    /// <summary>Kitten, Pocket, Kokoro and VOICEVOX share one "keep loaded" option and worker script.</summary>
    internal static readonly SpeechEngine[] Additional = [SpeechEngine.Kokoro, SpeechEngine.Voicevox, SpeechEngine.Kitten, SpeechEngine.Pocket];

    internal static string Prefix(this SpeechEngine engine) => engine.ToString().ToLowerInvariant();

    internal static bool IsAdditional(this SpeechEngine engine) => Additional.Contains(engine);

    /// <summary>Python engine for a voice ID; null for WinRT and <c>sapi5:</c> voices.</summary>
    internal static SpeechEngine? FromVoiceId(string voiceId)
    {
        int colon = voiceId.IndexOf(':');
        if (colon <= 0) return null;
        string prefix = voiceId[..colon];
        return Enum.TryParse<SpeechEngine>(prefix, ignoreCase: true, out var engine) && engine.Prefix() == prefix ? engine : null;
    }

    internal static string DisplayName(this SpeechEngine engine) => engine switch
    {
        SpeechEngine.Voicevox => "VOICEVOX",
        SpeechEngine.Kitten => "Kitten",
        _ => engine.ToString()
    };

    /// <summary>Group label for a voice in pickers.</summary>
    internal static string Group(VoiceInfo voice) =>
        FromVoiceId(voice.Id)?.DisplayName() ?? (voice.Id.StartsWith("sapi5:", StringComparison.Ordinal) ? "SAPI 5" : "Windows");
}
