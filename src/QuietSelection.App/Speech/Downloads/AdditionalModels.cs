using System.Net.Http;
using System.Text.Json;
using QuietSelection.App.Workers;

namespace QuietSelection.App.Speech.Downloads;

internal sealed record AdditionalVoice(string Id, string Speaker, string Name, string Language, string? Model = null, int Style = 0);
internal sealed record AdditionalCatalog(PiperFile[] Files, AdditionalVoice[] Voices);

internal static class AdditionalModels
{
    internal static readonly AdditionalCatalog Kokoro = Load("Kokoro");
    internal static readonly AdditionalCatalog Voicevox = Load("Voicevox");
    internal static readonly AdditionalCatalog Kitten = Load("Kitten");
    internal static readonly AdditionalCatalog Pocket = Load("Pocket");
    internal static AdditionalCatalog Catalog(SpeechEngine engine) => engine switch
    {
        SpeechEngine.Kokoro => Kokoro, SpeechEngine.Voicevox => Voicevox, SpeechEngine.Kitten => Kitten, SpeechEngine.Pocket => Pocket,
        _ => throw new ArgumentOutOfRangeException(nameof(engine))
    };
    private static AdditionalCatalog Load(string name)
    {
        using var stream = typeof(AdditionalModels).Assembly.GetManifestResourceStream("QuietSelection." + name + ".json")!;
        return JsonSerializer.Deserialize<AdditionalCatalog>(stream)!;
    }
    internal static string Root(SpeechEngine engine) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuietSelection", "models",
        engine switch { SpeechEngine.Kokoro => "kokoro-v1", SpeechEngine.Kitten => "kitten-mini-0.8", SpeechEngine.Pocket => "pocket-english-2026-09_2", SpeechEngine.Voicevox => "voicevox-v1", _ => throw new ArgumentOutOfRangeException(nameof(engine)) });
    internal static bool RuntimeAvailable(SpeechEngine engine) => File.Exists(Path.Combine(AppContext.BaseDirectory, "translation-runtime", "python.exe"))
        && File.Exists(Path.Combine(AppContext.BaseDirectory, engine == SpeechEngine.Pocket ? "pocket-runtime/pocket_tts/__init__.py" : engine is SpeechEngine.Kokoro or SpeechEngine.Kitten ? "piper-runtime/piper/espeakbridge.pyd" : "voicevox-runtime/voicevox_core.dll"))
        && (engine != SpeechEngine.Pocket || File.Exists(Path.Combine(AppContext.BaseDirectory, "silero-runtime/torch/__init__.py")));
    internal static IEnumerable<VoiceInfo> AvailableVoices()
    {
        foreach (var engine in new[] { SpeechEngine.Kokoro, SpeechEngine.Voicevox, SpeechEngine.Kitten, SpeechEngine.Pocket })
            if (RuntimeAvailable(engine) && Catalog(engine).Files.All(f => File.Exists(Path.Combine(Root(engine), f.Name)) && new FileInfo(Path.Combine(Root(engine), f.Name)).Length == f.Bytes))
                foreach (var voice in Catalog(engine).Voices) yield return new(voice.Id, voice.Name, voice.Language);
    }
    internal static async Task PrepareAsync(SpeechEngine engine, string? cache, IProgress<string> progress, CancellationToken token)
    {
        if (!RuntimeAvailable(engine)) throw new QuietException(ErrorCode.SpeechModelMissing);
        var catalog = Catalog(engine);
        string root = Root(engine), parent = Path.GetDirectoryName(root)!;
        Directory.CreateDirectory(parent);
        await using var lease = new FileStream(root + ".lock", FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        string stage = root + ".stage-" + Guid.NewGuid().ToString("N"), previous = root + ".previous-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(stage);
        try
        {
            foreach (var file in catalog.Files)
            {
                token.ThrowIfCancellationRequested();
                string? sourceRoot = cache;
                try { await PiperModels.VerifyAsync(root, [file], token); sourceRoot = root; }
                catch (QuietException) { }
                string destination = Path.Combine(stage, file.Name);
                if (sourceRoot is not null)
                {
                    await PiperModels.VerifyAsync(sourceRoot, [file], token);
                    progress.Report("Подготовка " + file.Name + "…");
                    await using var input = File.OpenRead(Path.Combine(sourceRoot, file.Name));
                    await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write);
                    await input.CopyToAsync(output, token);
                }
                else await DownloadAsync(file, destination, progress, token);
            }
            await PiperModels.VerifyAsync(stage, catalog.Files, token);
            using (var probe = new PythonSpeechClient(engine, stage, keepLoaded: true))
                foreach (var voice in catalog.Voices)
                {
                    progress.Report("Проверка голоса " + voice.Name + "…");
                    await probe.CallAsync(new("synthesize-test", Text: LanguagePlanner.TestPhrase(voice.Language[..2]), VoiceId: voice.Id), TimeSpan.FromMinutes(3), token);
                }
            token.ThrowIfCancellationRequested();
            if (Directory.Exists(root)) Directory.Move(root, previous);
            try { Directory.Move(stage, root); }
            catch { if (Directory.Exists(previous)) Directory.Move(previous, root); throw; }
        }
        finally
        {
            // Both paths are unique siblings under the application's model directory.
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
            if (Directory.Exists(previous) && Directory.Exists(root)) Directory.Delete(previous, true);
        }
    }
    private static async Task DownloadAsync(PiperFile file, string path, IProgress<string> progress, CancellationToken token)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        Uri uri = new(file.Url);
        for (int redirect = 0; redirect < 6; redirect++)
        {
            if (uri.Scheme != "https" || !(uri.Host == "github.com" || uri.Host == "release-assets.githubusercontent.com" || uri.Host == "objects.githubusercontent.com"
                || uri.Host == "huggingface.co" || uri.Host.EndsWith(".hf.co", StringComparison.Ordinal) || uri.Host == "cdn-lfs.huggingface.co"))
                throw new IOException("SPEECH_DOWNLOAD_HOST_REFUSED");
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            { uri = new Uri(uri, response.Headers.Location ?? throw new IOException("SPEECH_REDIRECT_INVALID")); continue; }
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(token);
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            byte[] buffer = new byte[65536]; long received = 0; int lastPercent = -1, count;
            while ((count = await input.ReadAsync(buffer, token)) > 0)
            {
                received += count;
                if (received > file.Bytes) throw new IOException("SPEECH_DOWNLOAD_SIZE");
                await output.WriteAsync(buffer.AsMemory(0, count), token);
                int percent = (int)(received * 100 / file.Bytes);
                if (percent != lastPercent) { lastPercent = percent; progress.Report($"Загрузка {file.Name}: {percent}%"); }
            }
            if (received != file.Bytes) throw new IOException("SPEECH_DOWNLOAD_INCOMPLETE");
            return;
        }
        throw new IOException("SPEECH_REDIRECT_LIMIT");
    }
}
