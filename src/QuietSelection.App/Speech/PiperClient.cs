namespace QuietSelection.App.Speech;

internal sealed class PiperClient(string? modelRoot = null, bool keepLoaded = false)
    : PythonSpeechClient(SpeechEngine.Piper, modelRoot, keepLoaded);
