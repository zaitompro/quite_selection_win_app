using QuietSelection.App.Workers;
using QuietSelection.App.Speech.Downloads;

namespace QuietSelection.App.Speech;

internal static class SpeechChecks
{
    internal static async Task<bool> RunAsync(VoiceInfo[] voices, WorkerClient worker, List<object> checks, string[] args)
    {
        bool failed = false;
        async Task Test(string id, Func<Task<bool>> run)
        {
            try { bool passed = await run(); checks.Add(new { id, passed }); failed |= !passed; }
            catch (QuietException ex) { checks.Add(new { id, passed = false, error = ex.Code.ToString() }); failed = true; }
            catch { checks.Add(new { id, passed = false, error = "SPEECH_CHECK_FAILURE" }); failed = true; }
        }
        await Test("sapi.enumerate", () => Task.FromResult(voices.Any(x => x.Id.StartsWith("sapi5:", StringComparison.Ordinal))));
        foreach (var package in RhVoiceInstallation.Packages)
        {
            var voice = voices.FirstOrDefault(x => x.Id.StartsWith("sapi5:", StringComparison.Ordinal) &&
                x.Name.Contains(package.Name, StringComparison.OrdinalIgnoreCase) && x.Language.Split('-')[0] == package.Language);
            await Test("rhvoice.available_" + package.Language, () => Task.FromResult(voice is not null));
            if (voice is null) continue;
            long[] lengths = new long[3];
            for (int i = 0; i < 3; i++)
            {
                int index = i, speed = i * 50;
                await Test($"rhvoice.synthesize_{package.Language}_{speed}", async () =>
                {
                    var result = await worker.CallAsync(new("synthesize-test", Text: LanguagePlanner.TestPhrase(package.Language), VoiceId: voice.Id, Speed: speed), TimeSpan.FromSeconds(60), CancellationToken.None);
                    lengths[index] = result.AudioBytes;
                    checks.Add(new { id = $"rhvoice.memory_{package.Language}_{speed}", passed = result.PeakWorkingSetBytes > 0,
                        result.PeakWorkingSetBytes, result.PrivateBytes, result.AudioBytes });
                    return result.AudioBytes > 44 && result.PeakWorkingSetBytes > 0;
                });
            }
            await Test("rhvoice.speed_changes_duration_" + package.Language, () => Task.FromResult(lengths[0] > lengths[1] && lengths[1] > lengths[2]));
            await Test("rhvoice.literal_markup_" + package.Language, async () =>
                (await worker.CallAsync(new("synthesize-test", Text: "<speak>" + LanguagePlanner.TestPhrase(package.Language) + "</speak>", VoiceId: voice.Id), TimeSpan.FromSeconds(60), CancellationToken.None)).AudioBytes > 44);
            await Test("rhvoice.cancel_then_restart_" + package.Language, async () =>
            {
                using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(700));
                try
                {
                    await worker.CallAsync(new("speak", Text: string.Concat(Enumerable.Repeat(LanguagePlanner.TestPhrase(package.Language), 150)), VoiceId: voice.Id), TimeSpan.FromMinutes(1), cancellation.Token);
                    return false;
                }
                catch (OperationCanceledException) { }
                return (await worker.CallAsync(new("synthesize-test", Text: LanguagePlanner.TestPhrase(package.Language), VoiceId: voice.Id), TimeSpan.FromSeconds(30), CancellationToken.None)).AudioBytes > 44;
            });
            if (args.Contains("--play-rhvoice-test"))
                await Test("rhvoice.play_fixed_phrase_" + package.Language, async () =>
                    (await worker.CallAsync(new("speak", Text: LanguagePlanner.TestPhrase(package.Language), VoiceId: voice.Id), TimeSpan.FromSeconds(30), CancellationToken.None)).AudioBytes > 44);
        }
        await Test("sapi.missing_voice_no_fallback", async () =>
        {
            try { await worker.CallAsync(new("synthesize-test", Text: "Synthetic test.", VoiceId: "sapi5:missing-synthetic-token"), TimeSpan.FromSeconds(15), CancellationToken.None); return false; }
            catch (QuietException ex) { return ex.Code == ErrorCode.VoiceMissing; }
        });
        int cacheIndex = Array.IndexOf(args, "--rhvoice-cache");
        if (cacheIndex >= 0 && cacheIndex + 1 < args.Length)
        {
            string cache = Path.GetFullPath(args[cacheIndex + 1]);
            foreach (var package in RhVoiceInstallation.Packages)
                await Test("rhvoice.cached_integrity_" + package.Language, () => RhVoiceInstallation.VerifyAsync(Path.Combine(cache, package.FileName), package, CancellationToken.None));
            await Test("rhvoice.reject_corrupt_package", async () =>
            {
                string fixture = Path.Combine(cache, Guid.NewGuid().ToString("N") + ".fixture");
                try
                {
                    using (var output = File.Create(fixture)) output.SetLength(RhVoiceInstallation.Packages[0].Bytes);
                    return !await RhVoiceInstallation.VerifyAsync(fixture, RhVoiceInstallation.Packages[0], CancellationToken.None);
                }
                finally { File.Delete(fixture); }
            });
            await Test("rhvoice.cancel_cache_verification", async () =>
            {
                using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
                try { await RhVoiceInstallation.DownloadAsync(RhVoiceInstallation.Packages[0], cache, new Progress<string>(), cancellation.Token); return false; }
                catch (OperationCanceledException) { return true; }
            });
        }
        return !failed;
    }
}
