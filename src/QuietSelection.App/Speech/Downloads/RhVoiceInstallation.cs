using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace QuietSelection.App.Speech.Downloads;

internal sealed record RhVoicePackage(string Language, string Name, string Version, string FileName,
    string Url, long Bytes, string Sha256, string License, string Source);

internal static class RhVoiceInstallation
{
    internal static IReadOnlyList<RhVoicePackage> Packages { get; } = LoadCatalog();
    private static RhVoicePackage[] LoadCatalog()
    {
        using var stream = typeof(RhVoiceInstallation).Assembly.GetManifestResourceStream("QuietSelection.RhVoice.json")!;
        return JsonSerializer.Deserialize<RhVoicePackage[]>(stream)!;
    }

    internal static async Task<bool> VerifyAsync(string path, RhVoicePackage package, CancellationToken token)
    {
        if (!File.Exists(path)) return false;
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        return await VerifyAsync(file, package, token);
    }
    private static async Task<bool> VerifyAsync(FileStream file, RhVoicePackage package, CancellationToken token) =>
        file.Length == package.Bytes && Convert.ToHexString(await SHA256.HashDataAsync(file, token))
            .Equals(package.Sha256, StringComparison.OrdinalIgnoreCase);

    internal static async Task<string> DownloadAsync(RhVoicePackage package, string directory, IProgress<string> progress, CancellationToken token)
    {
        if (!Packages.Contains(package)) throw new ArgumentException("UNKNOWN_VOICE_PACKAGE");
        Directory.CreateDirectory(directory);
        string destination = Path.Combine(directory, package.FileName);
        progress.Report("Проверка пакета RHVoice " + package.Name + "…");
        if (await VerifyAsync(destination, package, token)) return destination;
        string temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".partial");
        try
        {
            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("QuietSelection/0.1");
            var uri = new Uri(package.Url);
            HttpResponseMessage? response = null;
            try
            {
                for (int redirect = 0; redirect < 6; redirect++)
                {
                    if (uri.Scheme != "https" || uri.Host is not ("github.com" or "release-assets.githubusercontent.com"))
                        throw new IOException("VOICE_DOWNLOAD_HOST_REFUSED");
                    response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
                    if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308)) break;
                    var location = response.Headers.Location ?? throw new IOException("VOICE_REDIRECT_INVALID");
                    uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                    response.Dispose(); response = null;
                }
                if (response is null) throw new IOException("VOICE_REDIRECT_LIMIT");
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is long size && size != package.Bytes) throw new IOException("VOICE_SIZE_MISMATCH");
                await using (var input = await response.Content.ReadAsStreamAsync(token))
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                {
                    byte[] buffer = new byte[65536]; long total = 0; int previousPercent = -1;
                    int count;
                    while ((count = await input.ReadAsync(buffer, token)) != 0)
                    {
                        total += count;
                        if (total > package.Bytes) throw new IOException("VOICE_SIZE_MISMATCH");
                        await output.WriteAsync(buffer.AsMemory(0, count), token);
                        int percent = (int)(total * 100 / package.Bytes);
                        if (percent != previousPercent) { previousPercent = percent; progress.Report($"Загрузка RHVoice {package.Name}: {percent}%"); }
                    }
                    if (total != package.Bytes) throw new IOException("VOICE_DOWNLOAD_INCOMPLETE");
                }
            }
            finally { response?.Dispose(); }
            if (!await VerifyAsync(temporary, package, token)) throw new IOException("VOICE_HASH_MISMATCH");
            token.ThrowIfCancellationRequested();
            File.Move(temporary, destination, true);
            return destination;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static async Task<int> InstallAsync(string path, RhVoicePackage package)
    {
        if (!Packages.Contains(package)) throw new ArgumentException("UNKNOWN_VOICE_PACKAGE");
        // Keep the verified executable locked against replacement until its installer exits.
        await using var executable = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        if (!await VerifyAsync(executable, package, CancellationToken.None)) throw new IOException("VOICE_HASH_MISMATCH");
        var start = new ProcessStartInfo(path) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add("/S"); // Official NSIS bundle; includes both x86 and x64 SAPI engines.
        using var installer = Process.Start(start) ?? throw new IOException("VOICE_INSTALLER_FAILED");
        // Do not interrupt Windows Installer after handoff. No user content is ever passed here.
        await installer.WaitForExitAsync();
        return installer.ExitCode;
    }
}
