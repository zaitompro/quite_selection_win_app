using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using QuietSelection.App.Speech.Downloads;
using QuietSelection.App.Workers;

namespace QuietSelection.App.Speech;

internal static class SileroEnglishChecks
{
    internal static void Run(Application app, string[] args)
    {
        int index = Array.IndexOf(args, "--report-directory");
        if (index < 0 || index + 1 >= args.Length) throw new ArgumentException("REPORT_DIRECTORY_REQUIRED");
        string directory = Path.GetFullPath(args[index + 1]); Directory.CreateDirectory(directory);
        app.Startup += async (_, _) =>
        {
            var checks = new List<object>(); var measurements = new List<object>(); bool failed = false;
            uint length = 0; bool packaged = Platform.NativeMethods.GetCurrentPackageFullName(ref length, 0) != 15700;
            async Task Report(bool complete) => await File.WriteAllTextAsync(Path.Combine(directory, "silero-english.json"), JsonSerializer.Serialize(new
            {
                syntheticDataOnly = true, packaged, complete, completedUtc = DateTimeOffset.UtcNow,
                measurementScope = "Silero v3_en / v5_6_ru, CPU 2 threads, 24 kHz. English Piper comparison uses the same phrase and speed 50. Working set excludes UI. Peak counters are process-lifetime peaks. ClientFirstAudioMilliseconds includes IPC, not acoustic latency. OS file cache may be warm.",
                phrase = LanguagePlanner.TestPhrase("en"), checks, measurements
            }, new JsonSerializerOptions { WriteIndented = true }));
            async Task Test(string id, Func<Task<bool>> action)
            {
                try { bool passed = await action(); checks.Add(new { id, passed }); failed |= !passed; }
                catch (QuietException ex) { checks.Add(new { id, passed = false, error = ex.Code.ToString() }); failed = true; }
                catch (Exception ex) { checks.Add(new { id, passed = false, error = ex.GetType().Name }); failed = true; }
                await Report(false);
            }
            int cache = Array.IndexOf(args, "--prepare-silero-cache");
            if (cache >= 0)
                await Test("english.prepare_and_synthesize_all_118", async () =>
                {
                    await SileroModels.PrepareAsync(Path.GetFullPath(args[cache + 1]), new Progress<string>(), CancellationToken.None, "en");
                    await SileroModels.VerifyAsync(SileroModels.Root, CancellationToken.None, "ru"); return true;
                });
            await Test("english.both_model_integrity", async () =>
            {
                foreach (string language in new[] { "en", "ru" }) await SileroModels.VerifyAsync(SileroModels.Root, CancellationToken.None, language);
                return true;
            });
            await Test("english.enumeration", async () =>
            {
                using var router = new WorkerClient();
                var voices = (await router.CallAsync(new("voices"), TimeSpan.FromSeconds(30), CancellationToken.None)).Voices ?? [];
                return SileroModels.EnglishCatalog.Voices.Length == 118
                    && SileroModels.EnglishCatalog.Voices.All(v => voices.Any(x => x.Id == v.Id && x.Language == "en-US"))
                    && SileroModels.Catalog.Voices.All(v => voices.Any(x => x.Id == v.Id && x.Language == "ru-RU"))
                    && voices.Any(x => x.Id == "piper:en_US-ljspeech-medium");
            });
            string phrase = LanguagePlanner.TestPhrase("en");
            const string english = "silero:v3_en:en_0", russian = "silero:v5_6_ru:kseniya", piper = "piper:en_US-ljspeech-medium";
            if (!failed)
            {
                foreach (string speaker in new[] { "en_0", "en_50", "en_117" })
                {
                    string id = "silero:v3_en:" + speaker;
                    using var worker = new SileroClient(keepLoaded: true);
                    await Test("english.cold_" + speaker, async () =>
                    {
                        var result = await worker.CallAsync(new("synthesize-test", Text: phrase, VoiceId: id), TimeSpan.FromMinutes(3), CancellationToken.None);
                        measurements.Add(new { voice = speaker, scenario = "cold", result });
                        return !result.CacheHit && result.AudioBytes > 44 && result.ProcessedCharacters == phrase.Length;
                    });
                    int? pid = worker.ProcessId;
                    await Test("english.warm_" + speaker, async () =>
                    {
                        for (int iteration = 0; iteration < 5; iteration++)
                        {
                            var result = await worker.CallAsync(new("synthesize-test", Text: phrase, VoiceId: id), TimeSpan.FromMinutes(1), CancellationToken.None);
                            measurements.Add(new { voice = speaker, scenario = "warm", iteration, result });
                            if (!result.CacheHit || result.ProcessId != pid || result.ProcessedCharacters != phrase.Length) return false;
                        }
                        return true;
                    });
                    await Test("english.idle_" + speaker, async () =>
                    {
                        using var child = Process.GetProcessById(pid!.Value); var cpu = child.TotalProcessorTime;
                        await Task.Delay(1500); child.Refresh();
                        var result = await worker.CallAsync(new("silero-status"), TimeSpan.FromSeconds(10), CancellationToken.None);
                        measurements.Add(new { voice = speaker, scenario = "warm-idle", idleCpuMilliseconds = (child.TotalProcessorTime - cpu).TotalMilliseconds, idleSampleMilliseconds = 1500, result });
                        return result.RetainedModels == 1 && result.ProcessId == pid;
                    });
                    await Test("english.speed_" + speaker, async () =>
                    {
                        var durations = new List<double>();
                        foreach (int speed in new[] { 0, 50, 100 })
                        {
                            var result = await worker.CallAsync(new("synthesize-test", Text: phrase, VoiceId: id, Speed: speed), TimeSpan.FromMinutes(1), CancellationToken.None);
                            measurements.Add(new { voice = speaker, scenario = "speed", speed, result }); durations.Add(result.AudioSeconds);
                        }
                        return durations[0] > durations[1] && durations[1] > durations[2];
                    });
                    if (args.Contains("--play-silero-test"))
                        await Test("english.play_" + speaker, async () =>
                        {
                            var result = await worker.CallAsync(new("speak", Text: phrase, VoiceId: id), TimeSpan.FromMinutes(1), CancellationToken.None);
                            measurements.Add(new { voice = speaker, scenario = "play", result }); return result.AudioBytes > 44;
                        });
                }
                using var shared = new SileroClient(keepLoaded: true);
                await Test("english.numbers_and_literal_markup", async () =>
                {
                    foreach (string text in new[] { "731", "0.16–0.21 s", "57.2 MB", "Silero **works** & <break time=\"999s\"/> is literal." })
                    {
                        var result = await shared.CallAsync(new("synthesize-test", Text: text, VoiceId: english), TimeSpan.FromMinutes(3), CancellationToken.None);
                        measurements.Add(new { voice = "en_0", scenario = "normalized-text", fixture = text, result });
                        if (result.AudioSeconds <= 0.2 || result.AudioSeconds > 30 || result.ProcessedCharacters != text.Length) return false;
                    }
                    return true;
                });
                await Test("english.speaker_switch_keeps_model", async () =>
                {
                    int? pid = shared.ProcessId;
                    var configured = await shared.CallAsync(new("silero-configure", VoiceIds: ["silero:v3_en:en_117"]), TimeSpan.FromMinutes(1), CancellationToken.None);
                    var result = await shared.CallAsync(new("synthesize-test", Text: phrase, VoiceId: "silero:v3_en:en_117"), TimeSpan.FromMinutes(1), CancellationToken.None);
                    return configured.RetainedModels == 1 && result.CacheHit && result.ProcessId == pid;
                });
                await Test("english.long_slow_phrase", async () =>
                {
                    string text = string.Concat(Enumerable.Repeat("This is a synthetic sentence for measuring memory during English speech. ", 4))[..220];
                    var result = await shared.CallAsync(new("synthesize-test", Text: text, VoiceId: english, Speed: 0), TimeSpan.FromMinutes(2), CancellationToken.None);
                    measurements.Add(new { voice = "en_0", scenario = "long-phrase-slow", result });
                    return result.ProcessedCharacters == text.Length && result.AudioBytes > 44;
                });
                await Test("english.cancel_inference_and_reuse", async () =>
                {
                    using var stop = new CancellationTokenSource(100); var timer = Stopwatch.StartNew(); bool lateAudio = false;
                    try
                    {
                        await shared.CallAsync(new("synthesize-test", Text: string.Concat(Enumerable.Repeat(phrase, 50)), VoiceId: english, Speed: 0), TimeSpan.FromMinutes(1), stop.Token, () => { if (stop.IsCancellationRequested) lateAudio = true; });
                        return false;
                    }
                    catch (OperationCanceledException) { }
                    double stopMilliseconds = Math.Max(0, timer.Elapsed.TotalMilliseconds - 100);
                    var result = await shared.CallAsync(new("synthesize-test", Text: phrase, VoiceId: english), TimeSpan.FromMinutes(3), CancellationToken.None);
                    measurements.Add(new { voice = "en_0", scenario = "after-cancel-inference", stopMilliseconds, result });
                    return !lateAudio && stopMilliseconds < 1500 && result.AudioBytes > 44;
                });
                if (args.Contains("--play-silero-test"))
                    await Test("english.cancel_playback_retains_model", async () =>
                    {
                        int? pid = shared.ProcessId;
                        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60)); var timer = new Stopwatch();
                        try
                        {
                            await shared.CallAsync(new("speak", Text: string.Concat(Enumerable.Repeat(phrase, 10)), VoiceId: english), TimeSpan.FromMinutes(2), stop.Token,
                                () => { timer.Start(); stop.CancelAfter(300); });
                            return false;
                        }
                        catch (OperationCanceledException) { }
                        double stopMilliseconds = Math.Max(0, timer.Elapsed.TotalMilliseconds - 300);
                        var result = await shared.CallAsync(new("synthesize-test", Text: phrase, VoiceId: english), TimeSpan.FromMinutes(1), CancellationToken.None);
                        measurements.Add(new { voice = "en_0", scenario = "after-cancel-playback", stopMilliseconds, result });
                        return timer.IsRunning && result.CacheHit && result.ProcessId == pid && stopMilliseconds < 750;
                    });
                await shared.ResetAsync(CancellationToken.None);
                await Test("english.both_languages_shared_process", async () =>
                {
                    using var router = new WorkerClient();
                    await router.ConfigureSileroAsync([russian, english], CancellationToken.None);
                    var ru = await router.CallAsync(new("synthesize-test", Text: LanguagePlanner.TestPhrase("ru"), VoiceId: russian), TimeSpan.FromMinutes(1), CancellationToken.None);
                    for (int iteration = 0; iteration < 5; iteration++)
                    {
                        var en = await router.CallAsync(new("synthesize-test", Text: phrase, VoiceId: english), TimeSpan.FromMinutes(1), CancellationToken.None);
                        measurements.Add(new { voice = "Ksenia+en_0", scenario = "both-idle", iteration, ru, en });
                        if (!ru.CacheHit || !en.CacheHit || ru.ProcessId != en.ProcessId || en.RetainedModels != 2) return false;
                    }
                    await router.ConfigureSileroAsync([russian], CancellationToken.None);
                    var stillRu = await router.CallAsync(new("synthesize-test", Text: LanguagePlanner.TestPhrase("ru"), VoiceId: russian), TimeSpan.FromMinutes(1), CancellationToken.None);
                    measurements.Add(new { voice = "Ksenia", scenario = "after-unload-english", result = stillRu });
                    using var process = Process.GetProcessById(stillRu.ProcessId);
                    await router.ConfigureSileroAsync([], CancellationToken.None);
                    return stillRu.CacheHit && stillRu.ProcessId == ru.ProcessId && stillRu.RetainedModels == 1 && process.HasExited;
                });
                await Test("english.piper_comparison", async () =>
                {
                    using var worker = new PiperClient(keepLoaded: true);
                    var cold = await worker.CallAsync(new("synthesize-test", Text: phrase, VoiceId: piper), TimeSpan.FromMinutes(1), CancellationToken.None);
                    measurements.Add(new { voice = "LJSpeech", scenario = "cold", result = cold });
                    for (int iteration = 0; iteration < 5; iteration++)
                    {
                        var result = await worker.CallAsync(new("synthesize-test", Text: phrase, VoiceId: piper), TimeSpan.FromMinutes(1), CancellationToken.None);
                        measurements.Add(new { voice = "LJSpeech", scenario = "warm", iteration, result });
                        if (!result.CacheHit || result.ProcessId != cold.ProcessId) return false;
                    }
                    return true;
                });
                await Test("english.cancel_preparation_preserves_both", async () =>
                {
                    using var stop = new CancellationTokenSource(); stop.Cancel();
                    try { await SileroModels.PrepareAsync(null, new Progress<string>(), stop.Token, "en"); return false; }
                    catch (OperationCanceledException)
                    {
                        foreach (string language in new[] { "en", "ru" }) await SileroModels.VerifyAsync(SileroModels.Root, CancellationToken.None, language);
                        return true;
                    }
                });
                string fixture = Path.Combine(directory, "english-integrity-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(fixture);
                try
                {
                    await Test("english.missing_model_no_fallback", async () =>
                    {
                        using var missing = new SileroClient(fixture);
                        try { await missing.CallAsync(new("synthesize-test", Text: phrase, VoiceId: english), TimeSpan.FromSeconds(30), CancellationToken.None); return false; }
                        catch (QuietException ex) { return ex.Code == ErrorCode.SpeechModelMissing; }
                    });
                    await Test("english.corrupt_model_never_loaded", async () =>
                    {
                        using (var file = File.Create(Path.Combine(fixture, SileroModels.EnglishCatalog.File.Name))) file.SetLength(SileroModels.EnglishCatalog.File.Bytes);
                        using var corrupt = new SileroClient(fixture);
                        try { await corrupt.CallAsync(new("synthesize-test", Text: phrase, VoiceId: english), TimeSpan.FromSeconds(30), CancellationToken.None); return false; }
                        catch (QuietException ex) { return ex.Code == ErrorCode.SpeechModelCorrupted; }
                    });
                }
                finally { Directory.Delete(fixture, true); }
                await Test("english.light_dark_settings", () => ThemeChecks.RunAsync(app, directory));
            }
            await Report(true); app.Shutdown(failed ? 1 : 0);
        };
    }
}
