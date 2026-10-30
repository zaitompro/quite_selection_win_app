using System.Diagnostics;
using System.IO.Pipes;
using QuietSelection.App.Platform;
using QuietSelection.App.Speech;

namespace QuietSelection.App.Workers;

internal sealed record SpeechPlanResult(double FirstAudioMilliseconds, double ElapsedMilliseconds,
    double MaximumGapMilliseconds, int Chunks, long AudioBytes, WorkerResponse[] Syntheses);

internal sealed class WorkerClient(IReadOnlyDictionary<Speech.SpeechEngine, string>? modelRoots = null) : IDisposable
{
    // Speech engines and their resident state. Model preloading holds this slot for minutes,
    // so selection reads use their own slot and one-shot helpers (OCR, voice lists) use none.
    private readonly SemaphoreSlim workerSlot = new(1, 1);
    private readonly SemaphoreSlim selectionSlot = new(1, 1);
    private readonly Selection.SelectionClient selection = new();
    private readonly Speech.NativeSpeechClient native = new();
    // WinRT/SAPI return a whole WAV per request; this bounds one response at slow speed.
    private const int NativeRequestCharacters = 240;
    internal int? NativeSpeechProcessId => native.ProcessId;
    private readonly Speech.PiperClient piper = new(modelRoots?.GetValueOrDefault(Speech.SpeechEngine.Piper), keepLoaded: true);
    private readonly Speech.SileroClient silero = new(modelRoots?.GetValueOrDefault(Speech.SpeechEngine.Silero), keepLoaded: true);
    private string[] residentVoiceIds = [];
    private string[] residentSileroIds = [];
    private readonly Dictionary<Speech.SpeechEngine, Speech.PythonSpeechClient> additional = new()
    {
        [Speech.SpeechEngine.Kokoro] = new(Speech.SpeechEngine.Kokoro, modelRoots?.GetValueOrDefault(Speech.SpeechEngine.Kokoro), keepLoaded: true),
        [Speech.SpeechEngine.Voicevox] = new(Speech.SpeechEngine.Voicevox, modelRoots?.GetValueOrDefault(Speech.SpeechEngine.Voicevox), keepLoaded: true),
        [Speech.SpeechEngine.Kitten] = new(Speech.SpeechEngine.Kitten, modelRoots?.GetValueOrDefault(Speech.SpeechEngine.Kitten), keepLoaded: true),
        [Speech.SpeechEngine.Pocket] = new(Speech.SpeechEngine.Pocket, modelRoots?.GetValueOrDefault(Speech.SpeechEngine.Pocket), keepLoaded: true)
    };
    private string[] residentAdditionalIds = [];

    internal async Task ConfigureAdditionalAsync(string[] ids, CancellationToken token)
    {
        await workerSlot.WaitAsync(token);
        try
        {
            residentAdditionalIds = [];
            foreach (var pair in additional)
            {
                string prefix = pair.Key.Prefix();
                string[] selected = ids.Where(x => Speech.SpeechEngines.FromVoiceId(x) == pair.Key).ToArray();
                if (selected.Length == 0) await pair.Value.ResetAsync(token);
                else await pair.Value.CallAsync(new(prefix + "-configure", VoiceIds: selected), TimeSpan.FromMinutes(3), token);
            }
            residentAdditionalIds = ids.ToArray();
        }
        finally { workerSlot.Release(); }
    }

    internal async Task ConfigurePiperAsync(string[] ids, CancellationToken token)
    {
        await workerSlot.WaitAsync(token);
        try
        {
            if (ids.Length == 0) await piper.ResetAsync(token);
            else await piper.CallAsync(new("piper-configure", VoiceIds: ids), TimeSpan.FromMinutes(2), token);
            residentVoiceIds = ids.ToArray();
        }
        finally { workerSlot.Release(); }
    }

    internal async Task ConfigureSileroAsync(string[] ids, CancellationToken token)
    {
        await workerSlot.WaitAsync(token);
        try
        {
            if (ids.Length == 0) await silero.ResetAsync(token);
            else await silero.CallAsync(new("silero-configure", VoiceIds: ids), TimeSpan.FromMinutes(3), token);
            residentSileroIds = ids.ToArray();
        }
        finally { workerSlot.Release(); }
    }

    public void Dispose() { selection.Dispose(); native.Dispose(); piper.Dispose(); silero.Dispose(); foreach (var client in additional.Values) client.Dispose(); }

    internal async Task<WorkerResponse> CallAsync(WorkerRequest request, TimeSpan timeout, CancellationToken token)
    {
        if (request.Kind is "voices" or "ocr" or "ocr-languages" or "ocr-fixture")
            return await ExecuteAsync(request, timeout, token); // Own short-lived process each time.
        var slot = request.Kind == "selection" ? selectionSlot : workerSlot;
        await slot.WaitAsync(token);
        try { return await ExecuteAsync(request, timeout, token); }
        finally { slot.Release(); }
    }

    internal async Task<SpeechPlanResult> SpeakPlanAsync(IReadOnlyList<SpeechSegment> segments, CancellationToken token,
        Func<Speech.PcmChunk, CancellationToken, Task>? playback = null, bool prefetch = true)
    {
        await workerSlot.WaitAsync(token);
        try
        {
            using var player = new Speech.PcmPlayer();
            var clock = Stopwatch.StartNew();
            double first = 0, ended = 0, maximumGap = 0;
            int chunks = 0; long bytes = 0;
            var responses = new List<WorkerResponse>();
            async Task Play(Speech.PcmChunk pcm, CancellationToken cancellation)
            {
                void Submitted()
                {
                    if (chunks == 0) first = clock.Elapsed.TotalMilliseconds;
                    else maximumGap = Math.Max(maximumGap, clock.Elapsed.TotalMilliseconds - ended);
                    chunks++; bytes += pcm.Data.Length;
                }
                if (playback is null) await player.PlayAsync(pcm, cancellation, Submitted);
                else { Submitted(); await playback(pcm, cancellation); }
                ended = clock.Elapsed.TotalMilliseconds;
            }
            async Task Produce(Func<Speech.PcmChunk, CancellationToken, ValueTask> send, CancellationToken cancellation)
            {
                foreach (var segment in LanguagePlanner.BatchForPrefetch(segments))
                {
                    var (client, resident) = Route(segment.VoiceId);
                    try
                    {
                        foreach (var part in BoundRequests(segment.Text, client is null ? NativeRequestCharacters : 1000))
                        {
                            cancellation.ThrowIfCancellationRequested();
                            if (string.IsNullOrWhiteSpace(part)) continue;
                            var request = new WorkerRequest(client is null ? "synthesize-wave" : "synthesize-stream",
                                Text: part, VoiceId: segment.VoiceId, Speed: segment.Speed);
                            if (client is not null)
                                responses.Add(await client.CallAsync(request, TimeSpan.FromMinutes(15), cancellation, audio: send));
                            else
                            {
                                var result = await native.SynthesizeAsync(request, TimeSpan.FromMinutes(2), cancellation);
                                var wav = result.AudioData ?? throw new QuietException(ErrorCode.SpeechFailure);
                                try
                                {
                                    foreach (var pcm in Speech.WavePcm.Read(wav)) await send(pcm, cancellation);
                                    responses.Add(result with { AudioData = null });
                                }
                                finally { Array.Clear(wav); }
                            }
                        }
                    }
                    finally
                    {
                        if (client is not null && !resident) await client.ResetAsync(CancellationToken.None);
                    }
                }
            }
            if (prefetch) await PrefetchPlayback.RunAsync<Speech.PcmChunk>(Produce, Play, pcm => Array.Clear(pcm.Data), token);
            else await Produce(async (pcm, cancellation) =>
            {
                try { await Play(pcm, cancellation); }
                finally { Array.Clear(pcm.Data); }
            }, token);
            if (playback is null) await player.DrainAsync(token); // The last buffer is still playing.
            if (chunks == 0) throw new QuietException(ErrorCode.SpeechFailure);
            return new(first, clock.Elapsed.TotalMilliseconds, maximumGap, chunks, bytes, responses.ToArray());
        }
        finally { workerSlot.Release(); }
    }

    // Python worker for a voice and whether its model is kept loaded; (null, false) for WinRT/SAPI.
    private (Speech.PythonSpeechClient? Client, bool Resident) Route(string voiceId) => Speech.SpeechEngines.FromVoiceId(voiceId) switch
    {
        Speech.SpeechEngine.Piper => (piper, residentVoiceIds.Contains(voiceId)),
        Speech.SpeechEngine.Silero => (silero, residentSileroIds.Contains(voiceId)),
        { } engine => (additional[engine], residentAdditionalIds.Contains(voiceId)),
        null => (null, false)
    };

    private static IEnumerable<string> BoundRequests(string text, int limit)
    {
        for (int start = 0; start < text.Length;)
        {
            int length = Math.Min(limit, text.Length - start);
            if (start + length < text.Length)
            {
                int boundary = text.LastIndexOf(' ', start + length - 1, length);
                if (boundary > start + length / 2) length = boundary - start + 1;
                if (char.IsHighSurrogate(text[start + length - 1])) length--;
            }
            yield return text.Substring(start, length);
            start += length;
        }
    }

    private async Task<WorkerResponse> ExecuteAsync(WorkerRequest request, TimeSpan timeout, CancellationToken token)
    {
        if (request.Kind == "selection")
        {
            var result = await selection.ReadAsync(request, timeout, token);
            if (result.Error != ErrorCode.None) throw new QuietException(result.Error);
            return result;
        }
        if (request.Kind is "speak" or "synthesize-test" && Speech.SpeechEngines.FromVoiceId(request.VoiceId) is { } engine)
        {
            var (client, resident) = Route(request.VoiceId);
            if (resident) return await client!.CallAsync(request, timeout, token);
            // A voice that is not kept loaded runs in its own short-lived worker.
            using var cold = new Speech.PythonSpeechClient(engine, modelRoots?.GetValueOrDefault(engine));
            return await cold.CallAsync(request, timeout, token);
        }
        var name = "QuietSelection-" + Guid.NewGuid().ToString("N");
        await using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        using var process = new Process { StartInfo = new(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true } };
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            process.StartInfo.ArgumentList.Add(typeof(WorkerClient).Assembly.Location);
        process.StartInfo.ArgumentList.Add("--worker");
        process.StartInfo.ArgumentList.Add(name);
        process.Start();
        WorkerJob job;
        try { job = new WorkerJob(process.Handle); }
        catch { if (!process.HasExited) process.Kill(); throw; }
        using var processLifetime = job;
        bool sourceChanged = false;
        using var focusCancellation = new CancellationTokenSource();
        bool guardForeground = request.Kind is "selection" or "ocr";
        var watch = guardForeground ? Task.Run(async () =>
        {
            try
            {
                while (!focusCancellation.IsCancellationRequested)
                {
                    if (NativeMethods.GetForegroundWindow() != (nint)request.SourceWindow)
                    { sourceChanged = true; deadline.Cancel(); break; }
                    await Task.Delay(30, focusCancellation.Token);
                }
            }
            catch (OperationCanceledException) { }
        }) : Task.CompletedTask;
        try
        {
            await pipe.WaitForConnectionAsync(deadline.Token);
            await WorkerProtocol.WriteAsync(pipe, request, deadline.Token);
            var result = await WorkerProtocol.ReadAsync<WorkerResponse>(pipe, deadline.Token);
            token.ThrowIfCancellationRequested();
            if (guardForeground && NativeMethods.GetForegroundWindow() != (nint)request.SourceWindow)
                throw new QuietException(ErrorCode.SourceChanged);
            if (result.Error != ErrorCode.None) throw new QuietException(result.Error);
            return result;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new QuietException(sourceChanged ? ErrorCode.SourceChanged :
                request.Kind == "selection" ? ErrorCode.SelectionTimeout : ErrorCode.WorkerFailure);
        }
        finally
        {
            focusCancellation.Cancel();
            await watch;
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
        }
    }
}
