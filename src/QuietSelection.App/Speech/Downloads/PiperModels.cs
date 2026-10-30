using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using QuietSelection.App.Workers;

namespace QuietSelection.App.Speech.Downloads;

internal sealed record PiperFile(string Name, long Bytes, string Sha256, string Url);
internal sealed record PiperModel(string Id, string Language, string Name, PiperFile[] Files);

internal static class PiperModels
{
    internal static readonly PiperModel[] Catalog = Load();
    internal static string Root => Platform.AppPaths.Models("piper-v1");
    internal static long DownloadBytes => Catalog.Sum(x => x.Files.Sum(f => f.Bytes));
    internal static bool RuntimeAvailable => File.Exists(Path.Combine(AppContext.BaseDirectory, "translation-runtime", "python.exe"))
        && File.Exists(Path.Combine(AppContext.BaseDirectory, "piper-runtime", "piper", "voice.py"));
    private static PiperModel[] Load()
    {
        using var source = typeof(PiperModels).Assembly.GetManifestResourceStream("QuietSelection.Piper.json")!;
        return JsonSerializer.Deserialize<PiperModel[]>(source)!;
    }
    private static string FilePath(string root, PiperFile file)
    {
        if (Path.GetFileName(file.Name) != file.Name || file.Name.Contains(':')) throw new InvalidDataException();
        return Path.Combine(root, file.Name);
    }
    internal static IEnumerable<VoiceInfo> AvailableVoices()
    {
        if (!RuntimeAvailable) yield break;
        foreach (var model in Catalog)
            if (model.Files.All(f => File.Exists(FilePath(Root, f)) && new FileInfo(FilePath(Root, f)).Length == f.Bytes))
                yield return new(model.Id, "Piper " + model.Name + " [локальная модель]", model.Language);
    }
    internal static async Task VerifyAsync(string root, IEnumerable<PiperFile> files, CancellationToken token)
    {
        foreach (var file in files)
        {
            string path = FilePath(root, file);
            if (!File.Exists(path) || new FileInfo(path).Length != file.Bytes) throw new QuietException(ErrorCode.SpeechModelMissing);
            await using var source = File.OpenRead(path);
            if (!Convert.ToHexString(await SHA256.HashDataAsync(source, token)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new QuietException(ErrorCode.SpeechModelCorrupted);
        }
    }
    internal static async Task PrepareAsync(string? cache, IProgress<string> progress, CancellationToken token)
    {
        if (!RuntimeAvailable) throw new QuietException(ErrorCode.SpeechModelMissing);
        string parent = Path.GetDirectoryName(Root)!; Directory.CreateDirectory(parent);
        await using var lease = new FileStream(Path.Combine(parent, "piper-preparation.lock"), FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        string stage = Path.Combine(parent, "piper-stage-" + Guid.NewGuid().ToString("N"));
        string previous = Path.Combine(parent, "piper-previous-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            foreach (var model in Catalog)
            {
                bool reuse = false;
                try { await VerifyAsync(Root, model.Files, token); reuse = true; }
                catch (QuietException) { }
                foreach (var file in model.Files)
                {
                    token.ThrowIfCancellationRequested();
                    string destination = FilePath(stage, file);
                    if (reuse || cache is not null)
                    {
                        progress.Report("Проверка и подготовка Piper " + model.Name + "…");
                        string sourceRoot = reuse ? Root : cache!;
                        await VerifyAsync(sourceRoot, [file], token);
                        await using var source = File.OpenRead(FilePath(sourceRoot, file));
                        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write);
                        await source.CopyToAsync(output, token);
                    }
                    else await DownloadAsync(client, file, destination, progress, token);
                }
            }
            await VerifyAsync(stage, Catalog.SelectMany(x => x.Files), token);
            progress.Report("Проверка Piper: синтез тестовых фраз…");
            foreach (var model in Catalog)
                await new PiperClient(stage).CallAsync(new WorkerRequest("synthesize-test", Text: LanguagePlanner.TestPhrase(model.Language.Split('-')[0]), VoiceId: model.Id), TimeSpan.FromMinutes(2), token);
            token.ThrowIfCancellationRequested();
            if (Directory.Exists(Root)) Directory.Move(Root, previous);
            try { Directory.Move(stage, Root); }
            catch { if (Directory.Exists(previous)) Directory.Move(previous, Root); throw; }
        }
        finally
        {
            // Unique directories generated immediately under our own model root.
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
            if (Directory.Exists(previous) && Directory.Exists(Root)) Directory.Delete(previous, true);
        }
    }
    internal static async Task DownloadAsync(PiperFile file, string path, IProgress<string> progress, CancellationToken token)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        await DownloadAsync(client, file, path, progress, token);
    }
    private static async Task DownloadAsync(HttpClient client, PiperFile file, string path, IProgress<string> progress, CancellationToken token)
    {
        const int blockSize = 4 * 1024 * 1024;
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
        byte[] buffer = new byte[65536]; long total = 0; int lastPercent = -1;
        for (long offset = 0; offset < file.Bytes; offset += blockSize)
        {
            long end = Math.Min(file.Bytes - 1, offset + blockSize - 1);
            Uri uri = new(file.Url); HttpResponseMessage? response = null;
            try
            {
                for (int i = 0; i < 6; i++)
                {
                    if (uri.Scheme != "https" || !(uri.Host == "huggingface.co" || uri.Host.EndsWith(".hf.co", StringComparison.Ordinal) || uri.Host == "cdn-lfs.huggingface.co"))
                        throw new IOException("PIPER_DOWNLOAD_HOST_REFUSED");
                    using var request = new HttpRequestMessage(HttpMethod.Get, uri) { Version = System.Net.HttpVersion.Version11 };
                    request.Headers.Range = new(offset, end);
                    response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                    if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308)) break;
                    uri = new Uri(uri, response.Headers.Location ?? throw new IOException("PIPER_REDIRECT_INVALID"));
                    response.Dispose(); response = null;
                }
                if (response is null) throw new IOException("PIPER_REDIRECT_LIMIT");
                response.EnsureSuccessStatusCode();
                var range = response.Content.Headers.ContentRange;
                if (response.StatusCode == System.Net.HttpStatusCode.PartialContent)
                {
                    if (range?.From != offset || range.To != end || range.Length != file.Bytes) throw new IOException("PIPER_DOWNLOAD_RANGE");
                }
                else if (offset != 0 || file.Bytes > blockSize) throw new IOException("PIPER_RANGE_REQUIRED");
                await using var input = await response.Content.ReadAsStreamAsync(token);
                long received = 0; int count;
                while ((count = await input.ReadAsync(buffer, token)) > 0)
                {
                    received += count; total += count;
                    if (received > end - offset + 1) throw new IOException("PIPER_DOWNLOAD_SIZE");
                    await output.WriteAsync(buffer.AsMemory(0, count), token);
                    int percent = (int)(total * 100 / file.Bytes);
                    if (percent != lastPercent) { lastPercent = percent; progress.Report($"Загрузка {file.Name}: {percent}%"); }
                }
                if (received != end - offset + 1) throw new IOException("PIPER_DOWNLOAD_INCOMPLETE");
            }
            finally { response?.Dispose(); }
        }
    }
}
