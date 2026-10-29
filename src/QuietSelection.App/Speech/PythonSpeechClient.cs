using System.Diagnostics;
using QuietSelection.App.Workers;

namespace QuietSelection.App.Speech;

internal enum SpeechEngine { Piper, Silero, Kokoro, Voicevox, Kitten, Pocket }

internal sealed record PythonSpeechResponse(bool Success, string? Failure, long AudioBytes, long PeakWorkingSetBytes,
    long PrivateBytes, long PeakPrivateBytes, long LoadWorkingSetBytes, long LoadPrivateBytes,
    double LoadMilliseconds, double FirstAudioMilliseconds, double SynthesisMilliseconds,
    double AudioSeconds, int Chunks, int ProcessedCharacters, string? Stage,
    long WorkingSetBytes, bool CacheHit, int ProcessId, int RetainedModels,
    double PlaybackUnderrunMilliseconds, double MaxPlaybackUnderrunMilliseconds,
    byte[]? AudioData, int SampleRate);

/// <summary>Serial requests to a bounded worker. Cancellation drains its acknowledgement before reuse.</summary>
internal class PythonSpeechClient(SpeechEngine engine, string? modelRoot = null, bool keepLoaded = false) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private Process? process;
    private WorkerJob? job;
    private EventWaitHandle? cancellationEvent;
    private Task? diagnostics;
    private bool disposed;
    internal int? ProcessId => process is { HasExited: false } ? process.Id : null;

    private string Prefix => engine.ToString().ToLowerInvariant();
    private bool KnownVoice(string id) => engine switch
    {
        SpeechEngine.Piper => Downloads.PiperModels.Catalog.Any(x => x.Id == id),
        SpeechEngine.Silero => Downloads.SileroModels.AllVoices.Any(x => x.Id == id),
        _ => Downloads.AdditionalModels.Catalog(engine).Voices.Any(x => x.Id == id)
    };

    private void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (process is { HasExited: false }) return;
        Release();
        bool available = engine switch { SpeechEngine.Piper => Downloads.PiperModels.RuntimeAvailable, SpeechEngine.Silero => Downloads.SileroModels.RuntimeAvailable, _ => Downloads.AdditionalModels.RuntimeAvailable(engine) };
        if (!available) throw new QuietException(ErrorCode.SpeechModelMissing);
        string eventName = "Local\\QuietSelection." + engine + ".Cancel." + Guid.NewGuid().ToString("N");
        cancellationEvent = new(false, EventResetMode.ManualReset, eventName);
        var runtime = Path.Combine(AppContext.BaseDirectory, "translation-runtime", "python.exe");
        process = new Process { StartInfo = new(runtime)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(runtime)!
        } };
        string script = engine switch { SpeechEngine.Piper => "worker.py", SpeechEngine.Silero => "silero_worker.py", _ => "additional_worker.py" };
        string root = modelRoot ?? (engine switch { SpeechEngine.Piper => Downloads.PiperModels.Root, SpeechEngine.Silero => Downloads.SileroModels.Root, _ => Downloads.AdditionalModels.Root(engine) });
        foreach (var argument in new[] { "-I", "-B", Path.Combine(AppContext.BaseDirectory, "speech", script), root, eventName, Prefix })
            process.StartInfo.ArgumentList.Add(argument);
        process.StartInfo.Environment["OMP_NUM_THREADS"] = engine is SpeechEngine.Kokoro or SpeechEngine.Voicevox
            ? Math.Min(8, Environment.ProcessorCount).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : engine == SpeechEngine.Kitten ? Math.Min(4, Environment.ProcessorCount).ToString(System.Globalization.CultureInfo.InvariantCulture) : "2";
        process.StartInfo.Environment["OPENBLAS_NUM_THREADS"] = "1";
        try
        {
            process.Start();
            job = new WorkerJob(process.Handle, (engine == SpeechEngine.Piper ? 1UL : 3UL) * 1024 * 1024 * 1024);
            diagnostics = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        }
        catch { Release(); throw; }
    }

    internal async Task<WorkerResponse> CallAsync(WorkerRequest request, TimeSpan timeout, CancellationToken token,
        Action? firstAudio = null, Func<PcmChunk, CancellationToken, ValueTask>? audio = null)
    {
        if (request.Text.Length > TextLimits.Maximum) throw new QuietException(ErrorCode.TooLong);
        if (request.Kind is "speak" or "synthesize-test" or "synthesize-stream" && !KnownVoice(request.VoiceId))
            throw new QuietException(ErrorCode.VoiceMissing);
        if (request.Kind == "synthesize-stream" && audio is null) throw new ArgumentException("AUDIO_CONSUMER_REQUIRED");
        if (request.Kind == Prefix + "-configure" && (request.VoiceIds is null || request.VoiceIds.Length > 2 || request.VoiceIds.Any(id => !KnownVoice(id))))
            throw new QuietException(ErrorCode.VoiceMissing);
        var elapsed = Stopwatch.StartNew();
        await gate.WaitAsync(token);
        try
        {
            Start();
            cancellationEvent!.Reset();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(timeout);
            using var cancellation = deadline.Token.Register(() => { try { cancellationEvent?.Set(); } catch (ObjectDisposedException) { } });
            Task<PythonSpeechResponse>? response = null;
            double clientFirstAudio = 0;
            var responseStream = process!.StandardOutput.BaseStream;
            async Task<PythonSpeechResponse> ReadResponse()
            {
                while (true)
                {
                    var item = await WorkerProtocol.ReadAsync<PythonSpeechResponse>(responseStream, CancellationToken.None);
                    if (item.Stage == "audio")
                    {
                        if (item.AudioData is null || audio is null) throw new QuietException(ErrorCode.SpeechFailure);
                        var chunk = new PcmChunk(item.AudioData, item.SampleRate);
                        chunk.Validate();
                        if (deadline.IsCancellationRequested) Array.Clear(chunk.Data);
                        else
                        {
                            try { await audio(chunk, deadline.Token); }
                            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
                            { Array.Clear(chunk.Data); } // Drain cancellation acknowledgement before reuse.
                        }
                        continue;
                    }
                    if (item.Stage != "first-audio") return item;
                    if (clientFirstAudio == 0) { clientFirstAudio = elapsed.Elapsed.TotalMilliseconds; firstAudio?.Invoke(); }
                }
            }
            try
            {
                await WorkerProtocol.WriteAsync(process!.StandardInput.BaseStream, request, deadline.Token);
                response = ReadResponse();
                var result = await response.WaitAsync(deadline.Token);
                token.ThrowIfCancellationRequested();
                if (!result.Success) throw new QuietException(result.Failure switch
                {
                    "VOICE_MISSING" => ErrorCode.VoiceMissing, "MODEL_MISSING" => ErrorCode.SpeechModelMissing,
                    "MODEL_CORRUPTED" => ErrorCode.SpeechModelCorrupted, _ => ErrorCode.SpeechFailure
                });
                return new(AudioBytes: result.AudioBytes, PeakWorkingSetBytes: result.PeakWorkingSetBytes, PrivateBytes: result.PrivateBytes,
                    PeakPrivateBytes: result.PeakPrivateBytes, LoadWorkingSetBytes: result.LoadWorkingSetBytes, LoadPrivateBytes: result.LoadPrivateBytes,
                    LoadMilliseconds: result.LoadMilliseconds, FirstAudioMilliseconds: result.FirstAudioMilliseconds,
                    SynthesisMilliseconds: result.SynthesisMilliseconds, AudioSeconds: result.AudioSeconds,
                    Chunks: result.Chunks, ProcessedCharacters: result.ProcessedCharacters, WorkingSetBytes: result.WorkingSetBytes,
                    CacheHit: result.CacheHit, ProcessId: result.ProcessId, RetainedModels: result.RetainedModels,
                    ClientFirstAudioMilliseconds: clientFirstAudio, ClientElapsedMilliseconds: elapsed.Elapsed.TotalMilliseconds,
                    PlaybackUnderrunMilliseconds: result.PlaybackUnderrunMilliseconds, MaxPlaybackUnderrunMilliseconds: result.MaxPlaybackUnderrunMilliseconds);
            }
            catch (OperationCanceledException)
            {
                // Cooperate between chunks/during playback; uninterruptible native inference is killed after the grace period.
                bool acknowledged = false;
                if (response is not null)
                    try { await response.WaitAsync(TimeSpan.FromMilliseconds(750)); acknowledged = true; } catch { }
                if (!acknowledged)
                {
                    Release();
                    if (response is not null) _ = response.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                }
                if (token.IsCancellationRequested) throw new OperationCanceledException(token);
                throw new QuietException(ErrorCode.SpeechFailure);
            }
            catch (QuietException) { if (request.Kind == "synthesize-stream") Release(); throw; }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Text.Json.JsonException)
            { Release(); throw new QuietException(ErrorCode.SpeechFailure); }
            finally { if (!keepLoaded) Release(); }
        }
        finally { gate.Release(); }
    }

    internal async Task ResetAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try { Release(); } finally { gate.Release(); }
    }

    private void Release()
    {
        var child = process; process = null;
        if (child is not null)
        {
            try { if (!child.HasExited) child.Kill(entireProcessTree: true); child.WaitForExit(); }
            catch (InvalidOperationException) { }
            finally { child.Dispose(); }
        }
        job?.Dispose(); job = null;
        cancellationEvent?.Dispose(); cancellationEvent = null;
        if (diagnostics is not null) { _ = diagnostics.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted); diagnostics = null; }
    }

    public void Dispose() { disposed = true; Release(); }
}
