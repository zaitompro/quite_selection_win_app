using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using QuietSelection.App.Workers;

namespace QuietSelection.App.Speech.Downloads;

internal sealed record SileroFile(string Name, long Bytes, string Sha256, string Url);
internal sealed record SileroVoice(string Id, string Speaker, string Name);
internal sealed record SileroCatalog(SileroFile File, SileroVoice[] Voices, string Language = "ru-RU");

internal static class SileroModels
{
    internal static readonly SileroCatalog Catalog = Load("QuietSelection.Silero.json");
    internal static readonly SileroCatalog EnglishCatalog = Load("QuietSelection.SileroEnglish.json");
    internal static IEnumerable<SileroCatalog> Catalogs => [Catalog, EnglishCatalog];
    internal static IEnumerable<SileroVoice> AllVoices => Catalogs.SelectMany(x => x.Voices);
    internal static SileroCatalog ForLanguage(string language) => language switch { "ru" => Catalog, "en" => EnglishCatalog, _ => throw new ArgumentException("UNSUPPORTED_SILERO_LANGUAGE") };
    internal static string Root => Platform.AppPaths.Models("silero-v5");
    internal static bool RuntimeAvailable => File.Exists(Path.Combine(AppContext.BaseDirectory, "translation-runtime", "python.exe"))
        && File.Exists(Path.Combine(AppContext.BaseDirectory, "silero-runtime", "torch", "__init__.py"));
    private static SileroCatalog Load(string resource)
    {
        using var source = typeof(SileroModels).Assembly.GetManifestResourceStream(resource)!;
        return JsonSerializer.Deserialize<SileroCatalog>(source)!;
    }
    private static string FilePath(string root, SileroCatalog catalog)
    {
        string name = catalog.File.Name;
        if (Path.GetFileName(name) != name || name.Contains(':')) throw new InvalidDataException();
        return Path.Combine(root, name);
    }
    internal static IEnumerable<VoiceInfo> AvailableVoices()
    {
        if (!RuntimeAvailable) yield break;
        foreach (var catalog in Catalogs)
        {
            string path = FilePath(Root, catalog);
            if (!File.Exists(path) || new FileInfo(path).Length != catalog.File.Bytes) continue;
            string language = catalog.Language == "ru-RU" ? "русский" : "английский";
            foreach (var voice in catalog.Voices) yield return new(voice.Id, "Silero " + voice.Name + $" [{language}, локально]", catalog.Language);
        }
    }
    internal static async Task VerifyAsync(string root, CancellationToken token, string language = "ru")
    {
        var catalog = ForLanguage(language);
        string path = FilePath(root, catalog);
        if (!File.Exists(path) || new FileInfo(path).Length != catalog.File.Bytes) throw new QuietException(ErrorCode.SpeechModelMissing);
        await using var source = File.OpenRead(path);
        if (!Convert.ToHexString(await SHA256.HashDataAsync(source, token)).Equals(catalog.File.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new QuietException(ErrorCode.SpeechModelCorrupted);
    }
    internal static async Task PrepareAsync(string? cache, IProgress<string> progress, CancellationToken token, string language = "ru")
    {
        var catalog = ForLanguage(language);
        if (!RuntimeAvailable) throw new QuietException(ErrorCode.SpeechModelMissing);
        string parent = Path.GetDirectoryName(Root)!; Directory.CreateDirectory(parent);
        await using var lease = new FileStream(Path.Combine(parent, "silero-preparation.lock"), FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        string stage = Path.Combine(parent, "silero-stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            token.ThrowIfCancellationRequested();
            bool reuse = false;
            try { await VerifyAsync(Root, token, language); reuse = true; } catch (QuietException) { }
            if (reuse || cache is not null)
            {
                progress.Report("Проверка и подготовка голосов Silero…");
                string sourceRoot = reuse ? Root : cache!;
                await VerifyAsync(sourceRoot, token, language);
                await using var source = File.OpenRead(FilePath(sourceRoot, catalog));
                await using var output = new FileStream(FilePath(stage, catalog), FileMode.CreateNew, FileAccess.Write);
                await source.CopyToAsync(output, token);
            }
            else await DownloadAsync(FilePath(stage, catalog), catalog, progress, token);
            await VerifyAsync(stage, token, language);
            progress.Report($"Silero: проверка {catalog.Voices.Length} голосов локальным синтезом…");
            using (var worker = new SileroClient(stage, keepLoaded: true))
                foreach (var voice in catalog.Voices)
                    await worker.CallAsync(new("synthesize-test", Text: language == "en" ? "This is a voice test." : LanguagePlanner.TestPhrase("ru"), VoiceId: voice.Id), TimeSpan.FromMinutes(3), token);
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Root);
            // Publish only this language's verified file; never remove the other model.
            string destination = FilePath(Root, catalog);
            if (File.Exists(destination)) File.Replace(FilePath(stage, catalog), destination, null);
            else File.Move(FilePath(stage, catalog), destination);
        }
        finally
        {
            // Unique stage created immediately under the app's model directory.
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
        }
    }
    private static async Task DownloadAsync(string path, SileroCatalog catalog, IProgress<string> progress, CancellationToken token)
    {
        var file = catalog.File;
        Uri uri = new(file.Url);
        if (uri.Scheme != "https" || uri.Host != "models.silero.ai") throw new IOException("SILERO_DOWNLOAD_HOST_REFUSED");
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var request = new HttpRequestMessage(HttpMethod.Get, uri) { Version = System.Net.HttpVersion.Version11 };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode != System.Net.HttpStatusCode.OK || response.Content.Headers.ContentLength != file.Bytes)
            throw new IOException("SILERO_DOWNLOAD_SIZE");
        await using var input = await response.Content.ReadAsStreamAsync(token);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
        byte[] buffer = new byte[65536]; long total = 0; int lastPercent = -1;
        while (true)
        {
            int count = await input.ReadAsync(buffer, token);
            if (count == 0) break;
            total += count;
            if (total > file.Bytes) throw new IOException("SILERO_DOWNLOAD_SIZE");
            await output.WriteAsync(buffer.AsMemory(0, count), token);
            int percent = (int)(total * 100 / file.Bytes);
            if (percent != lastPercent) { lastPercent = percent; progress.Report($"Загрузка голосов Silero: {percent}%"); }
        }
        if (total != file.Bytes) throw new IOException("SILERO_DOWNLOAD_INCOMPLETE");
    }
}
