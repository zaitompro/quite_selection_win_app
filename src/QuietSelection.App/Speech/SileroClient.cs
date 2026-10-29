namespace QuietSelection.App.Speech;

internal sealed class SileroClient(string? modelRoot = null, bool keepLoaded = false)
    : PythonSpeechClient(SpeechEngine.Silero, modelRoot, keepLoaded);
