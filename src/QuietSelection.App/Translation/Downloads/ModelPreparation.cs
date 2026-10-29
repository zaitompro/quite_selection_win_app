using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Diagnostics;
using QuietSelection.App.Workers;

namespace QuietSelection.App.Translation.Downloads;

internal sealed record ModelFile(string Path, long Bytes, string Sha256, string? ArchivePath = null);
internal sealed record ModelArchive(string Name, long Bytes, string Sha256, string Url, ModelFile[] Files,
    string? Converter = null, ModelFile[]? SourceFiles = null);

/// <summary>Explicit translation model downloads. Receives no reading data.</summary>
internal static class ModelPreparation
{
    internal static readonly ModelArchive[] Catalog = LoadCatalog();
    internal static long DownloadBytes => Catalog.Sum(x => x.Bytes);
    private static ModelArchive[] LoadCatalog()
    {
        using var stream = typeof(ModelPreparation).Assembly.GetManifestResourceStream("QuietSelection.Models.json")!;
        return JsonSerializer.Deserialize<ModelArchive[]>(stream)!;
    }

    internal static async Task VerifyAsync(string root, CancellationToken token)
        => await VerifyFilesAsync(root, Catalog.SelectMany(x => x.Files), token);

    private static async Task VerifyFilesAsync(string root, IEnumerable<ModelFile> files, CancellationToken token)
    {
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            string path = SafePath(root, file.Path);
            if (!File.Exists(path) || new FileInfo(path).Length != file.Bytes) throw new QuietException(ErrorCode.ModelMissing);
            await using var stream = File.OpenRead(path);
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token));
            if (hash != file.Sha256) throw new QuietException(ErrorCode.ModelCorrupted);
        }
    }

    private static async Task<bool> CanReuseAsync(string root, ModelArchive model, CancellationToken token)
    {
        try { await VerifyFilesAsync(root, model.Files, token); return true; }
        catch (QuietException) { return false; }
    }

    internal static async Task PrepareAsync(string? localCache, IProgress<string> progress, CancellationToken token)
    {
        string root = TranslationClient.DefaultModelRoot;
        string parent = Path.GetDirectoryName(root)!;
        Directory.CreateDirectory(parent);
        // Exclusive preparation lease, including separately launched diagnostic instances.
        await using var lease = new FileStream(Path.Combine(parent, "preparation.lock"), FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        string stage = Path.Combine(parent, "preparing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        bool committed = false;
        try
        {
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            var reusable = new HashSet<string>();
            foreach (var model in Catalog)
                if (await CanReuseAsync(root, model, token)) reusable.Add(model.Name);
            long downloadBytes = Catalog.Where(x => !reusable.Contains(x.Name)).Sum(x => x.Bytes);
            long previous = 0;
            foreach (var model in Catalog)
            {
                if (reusable.Contains(model.Name))
                {
                    progress.Report("Сохранение уже проверенной модели…");
                    foreach (var file in model.Files)
                    {
                        string destination = SafePath(stage, file.Path);
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        await using var input = File.OpenRead(SafePath(root, file.Path));
                        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write);
                        await input.CopyToAsync(output, token);
                    }
                    continue;
                }
                string archive = Path.Combine(stage, model.Name);
                if (localCache is not null)
                {
                    progress.Report("Копирование проверенного локального архива…");
                    await using var source = File.OpenRead(Path.Combine(localCache, model.Name));
                    await using var target = File.Create(archive);
                    await TransferAsync(source, target, model.Bytes, previous, downloadBytes, progress, token);
                }
                else
                {
                    // URL comes exclusively from the embedded, fixed catalog. Never from an input span.
                    using var headersDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                    headersDeadline.CancelAfter(TimeSpan.FromMinutes(3));
                    using var response = await http.GetAsync(model.Url, HttpCompletionOption.ResponseHeadersRead, headersDeadline.Token);
                    response.EnsureSuccessStatusCode();
                    if (response.Content.Headers.ContentLength is long size && size != model.Bytes) throw new InvalidDataException("MODEL_SIZE");
                    await using var source = await response.Content.ReadAsStreamAsync(token);
                    await using var target = File.Create(archive);
                    await TransferAsync(source, target, model.Bytes, previous, downloadBytes, progress, token);
                }
                previous += model.Bytes;
                progress.Report("Проверка целостности и распаковка…");
                await using (var stream = File.OpenRead(archive))
                    if (Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token)) != model.Sha256) throw new InvalidDataException("MODEL_HASH");
                await ExtractAsync(archive, stage, model.SourceFiles ?? model.Files, token);
                File.Delete(archive);
                if (model.Converter is not null)
                {
                    if (model.Converter != "marian-ja-ru-v1" || model.SourceFiles is null) throw new InvalidDataException("MODEL_CONVERTER");
                    await VerifyFilesAsync(stage, model.SourceFiles, token);
                    progress.Report("Подготовка прямой модели японский → русский для этого компьютера…");
                    await ConvertMarianAsync(stage, token);
                    // Only the fixed source directory inside our disposable staging folder is removed.
                    Directory.Delete(SafePath(stage, "ja_ru_source"), recursive: true);
                }
            }
            await VerifyAsync(stage, token);
            progress.Report("Проверка: реальный локальный перевод трёх тестовых фраз…");
            await new TranslationClient(stage).ProbeAsync(token);
            token.ThrowIfCancellationRequested();
            // Retain an old installation until a fully verified replacement is ready.
            string? previousRoot = null;
            if (Directory.Exists(root)) { previousRoot = root + ".previous-" + Guid.NewGuid().ToString("N"); Directory.Move(root, previousRoot); }
            try { Directory.Move(stage, root); committed = true; }
            catch { if (previousRoot is not null && !Directory.Exists(root)) Directory.Move(previousRoot, root); throw; }
        }
        finally
        {
            if (!committed && Directory.Exists(stage))
            {
                string resolved = Path.GetFullPath(stage);
                if (resolved.StartsWith(Path.GetFullPath(parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    Directory.Delete(resolved, recursive: true);
            }
        }
    }

    private static async Task TransferAsync(Stream source, Stream target, long expected, long previous, long downloadBytes, IProgress<string> progress, CancellationToken token)
    {
        byte[] buffer = new byte[81920]; long total = 0; long lastReport = 0;
        while (true)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
            idle.CancelAfter(TimeSpan.FromMinutes(3)); // Per-read inactivity, not a total download deadline.
            int read = await source.ReadAsync(buffer, idle.Token);
            if (read == 0) break;
            total += read;
            if (total > expected) throw new InvalidDataException("MODEL_SIZE");
            await target.WriteAsync(buffer.AsMemory(0, read), token);
            if (Environment.TickCount64 - lastReport > 250)
            {
                progress.Report($"Получено {(previous + total) / 1_000_000.0:F1} из {downloadBytes / 1_000_000.0:F1} МБ ({(previous + total) * 100 / downloadBytes}%).");
                lastReport = Environment.TickCount64;
            }
        }
        if (total != expected) throw new InvalidDataException("MODEL_SIZE");
    }

    private static async Task ExtractAsync(string archive, string destination, ModelFile[] files, CancellationToken token)
    {
        using var zip = ZipFile.OpenRead(archive);
        if (zip.Entries.Count > 10000 || zip.Entries.Sum(x => x.Length) > 500_000_000) throw new InvalidDataException("ARCHIVE_LIMIT");
        foreach (var file in files)
        {
            var entries = zip.Entries.Where(x => x.FullName == (file.ArchivePath ?? file.Path)).ToArray();
            if (entries.Length != 1 || entries[0].Length != file.Bytes) throw new InvalidDataException("ARCHIVE_ENTRY");
            string path = SafePath(destination, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var input = entries[0].Open();
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            await input.CopyToAsync(output, token);
        }
    }

    private static async Task ConvertMarianAsync(string stage, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        using var process = new Process { StartInfo = new(Path.Combine(AppContext.BaseDirectory, "translation-runtime", "python.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (string argument in new[] { "-I", "-B", Path.Combine(AppContext.BaseDirectory, "translation", "prepare_marian.py"),
            SafePath(stage, "ja_ru_source"), SafePath(stage, "ja_ru") }) process.StartInfo.ArgumentList.Add(argument);
        process.StartInfo.Environment["OMP_NUM_THREADS"] = "2";
        process.StartInfo.Environment["OPENBLAS_NUM_THREADS"] = "1";
        process.Start();
        WorkerJob job;
        try { job = new WorkerJob(process.Handle, 3UL * 1024 * 1024 * 1024); }
        catch { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(CancellationToken.None); } throw; }
        using var lifetime = job;
        var output = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
        var diagnostics = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            if (process.ExitCode != 0) throw new QuietException(ErrorCode.TranslationFailure);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new QuietException(ErrorCode.ModelPreparationTimeout); }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(output, diagnostics);
        }
    }

    private static string SafePath(string root, string relative)
    {
        string path = Path.GetFullPath(Path.Combine(root, relative));
        if (Path.IsPathRooted(relative) || relative.Contains(':') ||
            !path.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("ARCHIVE_PATH");
        return path;
    }
}
