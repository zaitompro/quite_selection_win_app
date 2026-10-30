using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using QuietSelection.App.Speech.Downloads;
using QuietSelection.App.Workers;

namespace QuietSelection.App.Speech;

internal static class AdditionalSpeechChecks
{
    internal static void Run(Application app, string[] args)
    {
        int index = Array.IndexOf(args, "--report-directory");
        if (index < 0 || index + 1 >= args.Length) throw new ArgumentException("REPORT_DIRECTORY_REQUIRED");
        string directory = Path.GetFullPath(args[index + 1]); Directory.CreateDirectory(directory);
        bool kittenOnly = args.Contains("--kitten-only");
        bool pocketOnly = args.Contains("--pocket-only");
        bool candidateOnly = kittenOnly || pocketOnly;
        int cacheRootIndex = Array.IndexOf(args, "--model-cache-root");
        Dictionary<SpeechEngine, string>? roots = cacheRootIndex < 0 ? null : new()
        {
            [SpeechEngine.Kitten] = Path.Combine(Path.GetFullPath(args[cacheRootIndex + 1]), "kitten-mini-downloads"),
            [SpeechEngine.Pocket] = Path.Combine(Path.GetFullPath(args[cacheRootIndex + 1]), "pocket-tts-downloads"),
            [SpeechEngine.Kokoro] = Path.Combine(Path.GetFullPath(args[cacheRootIndex + 1]), "kokoro-downloads"),
            [SpeechEngine.Piper] = Path.Combine(Path.GetFullPath(args[cacheRootIndex + 1]), "piper-downloads")
        };
        string ModelRoot(SpeechEngine engine) => roots?.GetValueOrDefault(engine) ?? AdditionalModels.Root(engine);
        app.Startup += async (_, _) =>
        {
            var checks = new List<object>(); var measurements = new List<object>(); bool failed = false;
            bool packaged = Platform.NativeMethods.IsPackaged;
            async Task Report(bool complete) => await File.WriteAllTextAsync(Path.Combine(directory, "additional-speech.json"), JsonSerializer.Serialize(new
            {
                syntheticDataOnly = true, packaged, complete, completedUtc = DateTimeOffset.UtcNow,
                measurementScope = "CPU, per-engine tuned thread counts (see speech-tuning report). Worker working set/private commit excludes UI. Client first audio includes IPC, not acoustic latency. Cold means a new worker; OS file cache may be warm. Kokoro/Piper use the same English phrase and speed 50. Playback underrun measures waiting for the next PCM after waveOut completion, excluding driver/device latency.",
                checks, measurements
            }, new JsonSerializerOptions { WriteIndented = true }));
            async Task Test(string id, Func<Task<bool>> action)
            {
                try { bool passed = await action(); checks.Add(new { id, passed }); failed |= !passed; }
                catch (QuietException ex) { checks.Add(new { id, passed = false, error = ex.Code.ToString() }); failed = true; }
                catch (Exception ex) { checks.Add(new { id, passed = false, error = ex.GetType().Name }); failed = true; }
                await Report(false);
            }
            foreach (var engine in pocketOnly ? new[] { SpeechEngine.Pocket } : kittenOnly ? new[] { SpeechEngine.Kitten } : new[] { SpeechEngine.Kokoro, SpeechEngine.Voicevox, SpeechEngine.Kitten, SpeechEngine.Pocket })
            {
                string prefix = engine.ToString().ToLowerInvariant();
                var catalog = AdditionalModels.Catalog(engine);
                int cache = Array.IndexOf(args, "--prepare-" + prefix + "-cache");
                if (cache >= 0)
                    await Test(prefix + ".prepare_all_voices", async () =>
                    {
                        await AdditionalModels.PrepareAsync(engine, Path.GetFullPath(args[cache + 1]), new Progress<string>(), CancellationToken.None); return true;
                    });
                await Test(prefix + ".integrity", async () => { await PiperModels.VerifyAsync(ModelRoot(engine), catalog.Files, CancellationToken.None); return true; });
                if (roots is null)
                await Test(prefix + ".enumeration", async () =>
                {
                    using var router = new WorkerClient();
                    var voices = (await router.CallAsync(new("voices"), TimeSpan.FromSeconds(30), CancellationToken.None)).Voices ?? [];
                    return catalog.Voices.All(v => voices.Any(x => x.Id == v.Id && x.Language == v.Language))
                        && voices.Any(v => v.Id == "piper:en_US-ljspeech-medium")
                        && (engine != SpeechEngine.Voicevox || voices.Count(v => v.Id.StartsWith("voicevox:", StringComparison.Ordinal)) == 2);
                });
                string phrase = LanguagePlanner.TestPhrase(engine == SpeechEngine.Voicevox ? "ja" : "en");
                string[] selected = engine switch
                {
                    SpeechEngine.Kokoro => ["kokoro:af_heart", "kokoro:am_michael", "kokoro:bf_emma"],
                    SpeechEngine.Kitten => ["kitten:mini-0.8:bella", "kitten:mini-0.8:luna"],
                    SpeechEngine.Pocket => ["pocket:english-2026-09_2:alba", "pocket:english-2026-09_2:anna"],
                    _ => catalog.Voices.Select(v => v.Id).ToArray()
                };
                foreach (string id in selected)
                {
                    using var worker = new PythonSpeechClient(engine, ModelRoot(engine), keepLoaded: true);
                    await Test(id + ".cold", async () =>
                    {
                        var result = await worker.CallAsync(new("synthesize-test", Text: phrase, VoiceId: id), TimeSpan.FromMinutes(3), CancellationToken.None);
                        measurements.Add(new { voice = id, scenario = "cold", phrase, result });
                        return !result.CacheHit && result.AudioBytes > 44 && result.ProcessedCharacters == phrase.Length;
                    });
                    int? pid = worker.ProcessId;
                    await Test(id + ".warm", async () =>
                    {
                        for (int iteration = 0; iteration < 5; iteration++)
                        {
                            var result = await worker.CallAsync(new("synthesize-test", Text: phrase, VoiceId: id), TimeSpan.FromMinutes(2), CancellationToken.None);
                            measurements.Add(new { voice = id, scenario = "warm", iteration, result });
                            if (!result.CacheHit || result.ProcessId != pid || result.ProcessedCharacters != phrase.Length) return false;
                        }
                        return true;
                    });
                    await Test(id + ".idle", async () =>
                    {
                        using var child = Process.GetProcessById(worker.ProcessId!.Value); var cpu = child.TotalProcessorTime;
                        await Task.Delay(1500); child.Refresh();
                        var result = await worker.CallAsync(new(prefix + "-status"), TimeSpan.FromSeconds(10), CancellationToken.None);
                        double idleCpuMilliseconds = (child.TotalProcessorTime - cpu).TotalMilliseconds;
                        measurements.Add(new { voice = id, scenario = "idle", idleCpuMilliseconds, idleSampleMilliseconds = 1500, result });
                        return result.RetainedModels == 1 && idleCpuMilliseconds < 300;
                    });
                    await Test(id + ".speed", async () =>
                    {
                        var durations = new List<double>();
                        foreach (int speed in new[] { 0, 50, 100 })
                        {
                            var result = await worker.CallAsync(new("synthesize-test", Text: phrase, VoiceId: id, Speed: speed), TimeSpan.FromMinutes(2), CancellationToken.None);
                            durations.Add(result.AudioSeconds); measurements.Add(new { voice = id, scenario = "speed", speed, result });
                        }
                        return durations[0] > durations[1] && durations[1] > durations[2];
                    });
                    if (args.Contains("--play-additional-test"))
                        await Test(id + ".play", async () =>
                        {
                            var result = await worker.CallAsync(new("speak", Text: phrase, VoiceId: id), TimeSpan.FromMinutes(2), CancellationToken.None);
                            measurements.Add(new { voice = id, scenario = "play", result });
                            return result.AudioBytes > 44 && result.ProcessedCharacters == phrase.Length;
                        });
                    if (args.Contains("--play-additional-test") && id == selected[0])
                        await Test(id + ".streamed_paragraph", async () =>
                        {
                            string paragraph = string.Join(" ", Enumerable.Repeat(phrase, 3));
                            var result = await worker.CallAsync(new("speak", Text: paragraph, VoiceId: id), TimeSpan.FromMinutes(3), CancellationToken.None);
                            measurements.Add(new { voice = id, scenario = "streamed-paragraph", result });
                            return result.Chunks >= 6 && result.ProcessedCharacters == paragraph.Length
                                && result.ClientElapsedMilliseconds < result.SynthesisMilliseconds + result.AudioSeconds * 1000 - 500;
                        });
                }
                using var shared = new PythonSpeechClient(engine, ModelRoot(engine), keepLoaded: true);
                await Test(prefix + ".all_voices", async () =>
                {
                    foreach (var voice in catalog.Voices)
                    {
                        var result = await shared.CallAsync(new("synthesize-test", Text: phrase, VoiceId: voice.Id), TimeSpan.FromMinutes(3), CancellationToken.None);
                        measurements.Add(new { voice = voice.Id, scenario = "voice-check", result });
                        if (result.ProcessedCharacters != phrase.Length || result.AudioSeconds < 0.2) return false;
                    }
                    return true;
                });
                await Test(prefix + ".retain_only_selected", async () =>
                {
                    var result = await shared.CallAsync(new(prefix + "-configure", VoiceIds: [selected[0]]), TimeSpan.FromMinutes(3), CancellationToken.None);
                    return result.RetainedModels == 1;
                });
                await Test(prefix + ".numbers", async () =>
                {
                    string text = engine != SpeechEngine.Voicevox ? "Kitten uses 44.7 MB. The latency is 0.16 seconds." : "メモリーは120メガバイトです。今日は2026年10月20日です。";
                    var result = await shared.CallAsync(new("synthesize-test", Text: text, VoiceId: selected[0]), TimeSpan.FromMinutes(2), CancellationToken.None);
                    measurements.Add(new { voice = selected[0], scenario = "numbers", result });
                    return result.ProcessedCharacters == text.Length && result.AudioSeconds > 1;
                });
                await Test(prefix + ".cancel_inference_reuse", async () =>
                {
                    using var stop = new CancellationTokenSource(100); var timer = Stopwatch.StartNew(); bool lateAudio = false;
                    try { await shared.CallAsync(new("synthesize-test", Text: string.Concat(Enumerable.Repeat(phrase, 30)), VoiceId: selected[0], Speed: 0), TimeSpan.FromMinutes(3), stop.Token, () => { if (stop.IsCancellationRequested) lateAudio = true; }); return false; }
                    catch (OperationCanceledException) { }
                    double stopMilliseconds = Math.Max(0, timer.Elapsed.TotalMilliseconds - 100);
                    var result = await shared.CallAsync(new("synthesize-test", Text: phrase, VoiceId: selected[0]), TimeSpan.FromMinutes(3), CancellationToken.None);
                    measurements.Add(new { voice = selected[0], scenario = "after-cancel", stopMilliseconds, result });
                    return !lateAudio && stopMilliseconds < 1500 && result.AudioBytes > 44;
                });
                await Test(prefix + ".cancel_playback", async () =>
                {
                    using var stop = new CancellationTokenSource(); var timer = new Stopwatch();
                    try { await shared.CallAsync(new("speak", Text: string.Concat(Enumerable.Repeat(phrase, 5)), VoiceId: selected[0]), TimeSpan.FromMinutes(3), stop.Token, () => { timer.Start(); stop.CancelAfter(300); }); return false; }
                    catch (OperationCanceledException) { }
                    measurements.Add(new { voice = selected[0], scenario = "cancel-playback", stopMilliseconds = timer.Elapsed.TotalMilliseconds - 300 });
                    return timer.IsRunning && timer.ElapsedMilliseconds < 1300;
                });
                await Test(prefix + ".clear_cache", async () => { var result = await shared.CallAsync(new(prefix + "-configure", VoiceIds: []), TimeSpan.FromSeconds(30), CancellationToken.None); return result.RetainedModels == 0; });
                await Test(prefix + ".missing_model", async () =>
                {
                    using var missing = new PythonSpeechClient(engine, Path.Combine(directory, "missing-models"));
                    try { await missing.CallAsync(new("synthesize-test", Text: phrase, VoiceId: selected[0]), TimeSpan.FromMinutes(1), CancellationToken.None); return false; }
                    catch (QuietException ex) { return ex.Code == ErrorCode.SpeechModelMissing; }
                });
            }
            if (candidateOnly)
            {
                using var comparison = new PythonSpeechClient(SpeechEngine.Kokoro, ModelRoot(SpeechEngine.Kokoro), keepLoaded: true);
                await Test("kokoro.comparison", async () =>
                {
                    for (int i = 0; i < 6; i++)
                    {
                        var result = await comparison.CallAsync(new("synthesize-test", Text: LanguagePlanner.TestPhrase("en"), VoiceId: "kokoro:af_heart"), TimeSpan.FromMinutes(2), CancellationToken.None);
                        measurements.Add(new { voice = "kokoro:af_heart", scenario = i == 0 ? "cold" : "warm", result });
                    }
                    return true;
                });
                string candidateVoice = pocketOnly ? "pocket:english-2026-09_2:alba" : "kitten:mini-0.8:bella";
                await Test((pocketOnly ? "pocket" : "kitten") + ".stream_routing", async () =>
                {
                    using var router = new WorkerClient(roots);
                    const string text = "This is a reading test. Numbers: 123 and 4.5 MB.";
                    var result = await router.SpeakPlanAsync([new(text, "en", candidateVoice, 50)], CancellationToken.None,
                        (pcm, _) => { pcm.Validate(); return Task.CompletedTask; });
                    measurements.Add(new { voice = candidateVoice, scenario = "stream", result });
                    return result.Chunks > 0 && result.Syntheses.Sum(x => x.ProcessedCharacters) == text.Length
                        && result.AudioBytes == result.Syntheses.Sum(x => x.AudioBytes - x.Chunks * 44);
                });
            }
            if (!candidateOnly)
            using (var piper = new PiperClient(keepLoaded: true))
                await Test("piper.comparison", async () =>
                {
                    for (int i = 0; i < 6; i++)
                    {
                        var result = await piper.CallAsync(new("synthesize-test", Text: LanguagePlanner.TestPhrase("en"), VoiceId: "piper:en_US-ljspeech-medium"), TimeSpan.FromMinutes(2), CancellationToken.None);
                        measurements.Add(new { voice = "piper:en_US-ljspeech-medium", scenario = i == 0 ? "cold" : "warm", result });
                    }
                    return true;
                });
            await Test("themes", () => ThemeChecks.RunAsync(app, directory));
            await Report(true); app.Shutdown(failed ? 1 : 0);
        };
    }
}
