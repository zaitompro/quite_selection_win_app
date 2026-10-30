using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using QuietSelection.App.Speech.Downloads;
using QuietSelection.App.Workers;

namespace QuietSelection.App.Speech;

internal static class SileroChecks
{
    internal static void Run(Application app, string[] args)
    {
        int index = Array.IndexOf(args, "--report-directory");
        if (index < 0 || index + 1 >= args.Length) throw new ArgumentException("REPORT_DIRECTORY_REQUIRED");
        string directory = Path.GetFullPath(args[index + 1]); Directory.CreateDirectory(directory);
        app.Startup += async (_, _) =>
        {
            var checks = new List<object>(); var measurements = new List<object>(); bool failed = false;
            bool packaged = Platform.NativeMethods.IsPackaged;
            async Task Report(bool complete) => await File.WriteAllTextAsync(Path.Combine(directory, "silero.json"), JsonSerializer.Serialize(new
            {
                syntheticDataOnly = true, packaged, complete, completedUtc = DateTimeOffset.UtcNow,
                measurementScope = "Silero v5_6_ru, PyTorch CPU, 2 threads, 24 kHz, one shared model. Working set excludes UI and English Piper. Peak counters are process-lifetime peaks. ClientFirstAudioMilliseconds is first PCM readiness including IPC, not acoustic latency. OS file cache may be warm.",
                checks, measurements
            }, new JsonSerializerOptions { WriteIndented = true }));
            async Task Test(string id, Func<Task<bool>> action)
            {
                try { bool passed = await action(); checks.Add(new { id, passed }); failed |= !passed; }
                catch (QuietException ex) { checks.Add(new { id, passed = false, error = ex.Code.ToString() }); failed = true; }
                catch (Exception ex) { checks.Add(new { id, passed = false, error = ex.GetType().Name }); failed = true; }
                await Report(false);
            }
            int cacheIndex = Array.IndexOf(args, "--prepare-silero-cache");
            if (cacheIndex >= 0)
                await Test("silero.prepare_from_approved_cache", async () =>
                {
                    await SileroModels.PrepareAsync(Path.GetFullPath(args[cacheIndex + 1]), new Progress<string>(), CancellationToken.None); return true;
                });
            await Test("silero.integrity", async () => { await SileroModels.VerifyAsync(SileroModels.Root, CancellationToken.None); return true; });
            await Test("silero.enumeration_and_english_present", async () =>
            {
                using var router = new WorkerClient();
                var voices = (await router.CallAsync(new("voices"), TimeSpan.FromSeconds(30), CancellationToken.None)).Voices ?? [];
                return SileroModels.Catalog.Voices.All(v => voices.Any(x => x.Id == v.Id && x.Language == "ru-RU"))
                    && voices.Any(v => v.Id == "piper:en_US-ljspeech-medium");
            });
            string phrase = LanguagePlanner.TestPhrase("ru");
            if (!failed && !args.Contains("--silero-smoke"))
            {
                using var normalized = new SileroClient(keepLoaded: true);
                foreach (var voice in SileroModels.Catalog.Voices)
                    await Test("silero.normalized_tokens_" + voice.Speaker, async () =>
                    {
                        foreach (string fixture in new[] { "Silero", "731", "0,16–0,21 с", "731–748 МиБ", "example development" })
                        {
                            var result = await normalized.CallAsync(new("synthesize-test", Text: fixture, VoiceId: voice.Id), TimeSpan.FromMinutes(3), CancellationToken.None);
                            measurements.Add(new { voice = voice.Speaker, scenario = "normalized-token", fixture, result });
                            if (result.AudioSeconds < 0.2 || result.ProcessedCharacters != fixture.Length) return false;
                        }
                        return true;
                    });
                string russian = SileroModels.Catalog.Voices.First(x => x.Speaker == "xenia").Id;
                const string example = "Также подключены **пять русских голосов Silero** — доступны в разделе «Речь». С моделью в памяти задержка короткой фразы составила **0,16–0,21 с**, расход памяти — **731–748 МиБ** без интерфейса. Английский Piper LJSpeech сохранён.";
                await Test("silero.user_example_normalized", async () =>
                {
                    var result = await normalized.CallAsync(new(args.Contains("--play-silero-test") ? "speak" : "synthesize-test", Text: example, VoiceId: russian), TimeSpan.FromMinutes(3), CancellationToken.None);
                    measurements.Add(new { voice = "xenia", scenario = "normalized-example", result });
                    return result.AudioSeconds > 15 && result.CacheHit && result.ProcessedCharacters == example.Length && result.Chunks >= 3;
                });
                await Test("silero.expansion_bounded_original_accounting", async () =>
                {
                    string text = string.Join(" ", Enumerable.Repeat("123456789", 8));
                    var result = await normalized.CallAsync(new("synthesize-test", Text: text, VoiceId: russian), TimeSpan.FromMinutes(3), CancellationToken.None);
                    measurements.Add(new { voice = "xenia", scenario = "expanded-numbers", result });
                    return result.ProcessedCharacters == text.Length && result.Chunks >= 3 && result.AudioSeconds > 10;
                });
            }
            if (!failed && !args.Contains("--silero-text-only"))
            {
                foreach (var voice in SileroModels.Catalog.Voices)
                {
                    using var worker = new SileroClient(keepLoaded: true);
                    await Test("silero.cold_" + voice.Speaker, async () =>
                    {
                        var result = await worker.CallAsync(new("synthesize-test", Text: phrase, VoiceId: voice.Id), TimeSpan.FromMinutes(3), CancellationToken.None);
                        measurements.Add(new { voice = voice.Speaker, scenario = "cold", result });
                        return result.AudioBytes > 44 && !result.CacheHit && result.ProcessedCharacters == phrase.Length;
                    });
                    if (args.Contains("--silero-smoke")) break;
                    int? pid = worker.ProcessId;
                    await Test("silero.warm_" + voice.Speaker, async () =>
                    {
                        for (int iteration = 0; iteration < 5; iteration++)
                        {
                            var result = await worker.CallAsync(new("synthesize-test", Text: phrase, VoiceId: voice.Id), TimeSpan.FromSeconds(45), CancellationToken.None);
                            measurements.Add(new { voice = voice.Speaker, scenario = "warm", iteration, result });
                            if (!result.CacheHit || result.ProcessId != pid || result.ProcessedCharacters != phrase.Length) return false;
                        }
                        return true;
                    });
                    await Test("silero.idle_" + voice.Speaker, async () =>
                    {
                        using var child = Process.GetProcessById(pid!.Value); var cpu = child.TotalProcessorTime;
                        await Task.Delay(1500); child.Refresh();
                        var result = await worker.CallAsync(new("silero-status"), TimeSpan.FromSeconds(10), CancellationToken.None);
                        measurements.Add(new { voice = voice.Speaker, scenario = "warm-idle", idleCpuMilliseconds = (child.TotalProcessorTime - cpu).TotalMilliseconds, idleSampleMilliseconds = 1500, result });
                        return result.RetainedModels == 1 && result.ProcessId == pid;
                    });
                    await Test("silero.speed_" + voice.Speaker, async () =>
                    {
                        var durations = new List<double>();
                        foreach (int speed in new[] { 0, 50, 100 })
                        {
                            var result = await worker.CallAsync(new("synthesize-test", Text: phrase, VoiceId: voice.Id, Speed: speed), TimeSpan.FromMinutes(1), CancellationToken.None);
                            measurements.Add(new { voice = voice.Speaker, scenario = "speed", speed, result }); durations.Add(result.AudioSeconds);
                        }
                        return durations[0] > durations[1] && durations[1] > durations[2];
                    });
                    await Test("silero.long_slow_phrase_" + voice.Speaker, async () =>
                    {
                        string text = string.Concat(Enumerable.Repeat("Синтетический пример для проверки длинной фразы и расхода памяти ", 5))[..220];
                        var result = await worker.CallAsync(new("synthesize-test", Text: text, VoiceId: voice.Id, Speed: 0), TimeSpan.FromMinutes(2), CancellationToken.None);
                        measurements.Add(new { voice = voice.Speaker, scenario = "long-phrase-slow", result });
                        return result.CacheHit && result.ProcessedCharacters == text.Length;
                    });
                    if (args.Contains("--play-silero-test"))
                        await Test("silero.play_" + voice.Speaker, async () =>
                        {
                            var result = await worker.CallAsync(new("speak", Text: phrase, VoiceId: voice.Id), TimeSpan.FromMinutes(1), CancellationToken.None);
                            measurements.Add(new { voice = voice.Speaker, scenario = "play", result }); return result.AudioBytes > 44;
                        });
                    await Test("silero.disable_releases_" + voice.Speaker, async () =>
                    {
                        using var child = Process.GetProcessById(pid!.Value);
                        await worker.ResetAsync(CancellationToken.None);
                        return worker.ProcessId is null && child.HasExited;
                    });
                }
            }
            if (!failed && !args.Contains("--silero-smoke") && !args.Contains("--silero-text-only"))
            {
                string russian = SileroModels.Catalog.Voices.First(x => x.Speaker == "xenia").Id;
                string english = "piper:en_US-ljspeech-medium";
                using var worker = new SileroClient(keepLoaded: true);
                await Test("silero.preload_and_switch_speaker_one_model", async () =>
                {
                    var ready = await worker.CallAsync(new("silero-configure", VoiceIds: [russian]), TimeSpan.FromMinutes(3), CancellationToken.None);
                    measurements.Add(new { voice = "xenia", scenario = "preloaded-idle", result = ready });
                    foreach (var voice in SileroModels.Catalog.Voices)
                    {
                        var configured = await worker.CallAsync(new("silero-configure", VoiceIds: [voice.Id]), TimeSpan.FromMinutes(1), CancellationToken.None);
                        var result = await worker.CallAsync(new("synthesize-test", Text: phrase, VoiceId: voice.Id), TimeSpan.FromMinutes(1), CancellationToken.None);
                        measurements.Add(new { voice = voice.Speaker, scenario = "speaker-switch", result });
                        if (!result.CacheHit || result.ProcessId != ready.ProcessId || configured.RetainedModels != 1) return false;
                    }
                    return true;
                });
                await Test("silero.long_text", async () =>
                {
                    string text = string.Join(" ", Enumerable.Repeat(phrase, 25));
                    var result = await worker.CallAsync(new("synthesize-test", Text: text, VoiceId: russian), TimeSpan.FromMinutes(3), CancellationToken.None);
                    measurements.Add(new { voice = "xenia", scenario = "long", result });
                    return result.CacheHit && result.ProcessedCharacters == text.Length && result.Chunks >= 25;
                });
                await Test("silero.literal_markup_cannot_add_pause", async () =>
                {
                    var result = await worker.CallAsync(new("synthesize-test", Text: "Тест **без закрытия <break time=\"999s\"/> завершён & проверен.", VoiceId: russian), TimeSpan.FromMinutes(1), CancellationToken.None);
                    return result.AudioBytes > 44 && result.AudioSeconds < 30;
                });
                await Test("silero.cancel_inference_then_restart", async () =>
                {
                    int? beforePid = worker.ProcessId;
                    using var stop = new CancellationTokenSource(100); var timer = Stopwatch.StartNew();
                    bool lateAudio = false;
                    try
                    {
                        await worker.CallAsync(new("synthesize-test", Text: string.Concat(Enumerable.Repeat(phrase, 50)), VoiceId: russian, Speed: 0), TimeSpan.FromMinutes(1), stop.Token, () => { if (stop.IsCancellationRequested) lateAudio = true; });
                        return false;
                    }
                    catch (OperationCanceledException) { }
                    double stopMilliseconds = Math.Max(0, timer.Elapsed.TotalMilliseconds - 100);
                    var result = await worker.CallAsync(new("synthesize-test", Text: phrase, VoiceId: russian), TimeSpan.FromMinutes(3), CancellationToken.None);
                    measurements.Add(new { voice = "xenia", scenario = "after-cancel-inference", stopMilliseconds, reloaded = beforePid != result.ProcessId, result });
                    return !lateAudio && stopMilliseconds < 1500 && result.AudioBytes > 44;
                });
                if (args.Contains("--play-silero-test"))
                    await Test("silero.cancel_playback_retains_model", async () =>
                    {
                        int? pid = worker.ProcessId;
                        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60)); var timer = new Stopwatch();
                        try
                        {
                            await worker.CallAsync(new("speak", Text: string.Concat(Enumerable.Repeat(phrase, 10)), VoiceId: russian), TimeSpan.FromMinutes(2), stop.Token,
                                () => { timer.Start(); stop.CancelAfter(300); });
                            return false;
                        }
                        catch (OperationCanceledException) { }
                        double stopMilliseconds = Math.Max(0, timer.Elapsed.TotalMilliseconds - 300);
                        var result = await worker.CallAsync(new("synthesize-test", Text: phrase, VoiceId: russian), TimeSpan.FromMinutes(1), CancellationToken.None);
                        measurements.Add(new { voice = "xenia", scenario = "after-cancel-playback", stopMilliseconds, result });
                        return timer.IsRunning && result.CacheHit && result.ProcessId == pid && stopMilliseconds < 750;
                    });
                await Test("silero.missing_id_no_fallback", async () =>
                {
                    try { await worker.CallAsync(new("synthesize-test", Text: phrase, VoiceId: "silero:missing"), TimeSpan.FromSeconds(10), CancellationToken.None); return false; }
                    catch (QuietException ex) { return ex.Code == ErrorCode.VoiceMissing; }
                });
                await worker.ResetAsync(CancellationToken.None);
                await Test("silero.english_piper_routing_and_residency", async () =>
                {
                    using var router = new WorkerClient();
                    await router.ConfigurePiperAsync([english], CancellationToken.None);
                    await router.ConfigureSileroAsync([russian], CancellationToken.None);
                    var ru = await router.CallAsync(new("synthesize-test", Text: phrase, VoiceId: russian), TimeSpan.FromMinutes(1), CancellationToken.None);
                    var en = await router.CallAsync(new("synthesize-test", Text: LanguagePlanner.TestPhrase("en"), VoiceId: english), TimeSpan.FromSeconds(30), CancellationToken.None);
                    using var ruProcess = Process.GetProcessById(ru.ProcessId); using var enProcess = Process.GetProcessById(en.ProcessId);
                    measurements.Add(new { voice = "Silero+LJSpeech", scenario = "both-idle", combinedWorkingSetBytes = ruProcess.WorkingSet64 + enProcess.WorkingSet64, ru, en });
                    await router.ConfigureSileroAsync([], CancellationToken.None);
                    var stillEnglish = await router.CallAsync(new("synthesize-test", Text: LanguagePlanner.TestPhrase("en"), VoiceId: english), TimeSpan.FromSeconds(30), CancellationToken.None);
                    var coldRussian = await router.CallAsync(new("synthesize-test", Text: phrase, VoiceId: russian), TimeSpan.FromMinutes(3), CancellationToken.None);
                    return ru.CacheHit && en.CacheHit && ru.ProcessId != en.ProcessId && stillEnglish.CacheHit
                        && stillEnglish.ProcessId == en.ProcessId && !coldRussian.CacheHit && ruProcess.HasExited;
                });
                string fixture = Path.Combine(directory, "silero-integrity-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(fixture);
                try
                {
                    await Test("silero.missing_model_no_fallback", async () =>
                    {
                        using var missing = new SileroClient(fixture);
                        try { await missing.CallAsync(new("synthesize-test", Text: phrase, VoiceId: russian), TimeSpan.FromSeconds(30), CancellationToken.None); return false; }
                        catch (QuietException ex) { return ex.Code == ErrorCode.SpeechModelMissing; }
                    });
                    await Test("silero.corrupt_model_never_loaded", async () =>
                    {
                        using (var file = File.Create(Path.Combine(fixture, SileroModels.Catalog.File.Name))) file.SetLength(SileroModels.Catalog.File.Bytes);
                        using var corrupt = new SileroClient(fixture);
                        try { await corrupt.CallAsync(new("synthesize-test", Text: phrase, VoiceId: russian), TimeSpan.FromSeconds(30), CancellationToken.None); return false; }
                        catch (QuietException ex) { return ex.Code == ErrorCode.SpeechModelCorrupted; }
                    });
                }
                finally { Directory.Delete(fixture, true); }
                await Test("silero.cancel_preparation_preserves_models", async () =>
                {
                    using var stop = new CancellationTokenSource(); stop.Cancel();
                    try { await SileroModels.PrepareAsync(null, new Progress<string>(), stop.Token); return false; }
                    catch (OperationCanceledException) { await SileroModels.VerifyAsync(SileroModels.Root, CancellationToken.None); return true; }
                });
                await Test("silero.light_dark_settings", () => ThemeChecks.RunAsync(app, directory, russian));
            }
            await Report(true); app.Shutdown(failed ? 1 : 0);
        };
    }
}
