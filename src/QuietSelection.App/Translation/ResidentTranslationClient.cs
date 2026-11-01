using System.Diagnostics;
using QuietSelection.App.Workers;

namespace QuietSelection.App.Translation;

internal sealed record ResidentTranslationResponse(bool Success, string Text, string Failure, int ProcessId);

/// <summary>
/// Keeps one translation worker (<c>worker.py --serve</c>) with its last model loaded between
/// readings: about 0.3 s per phrase instead of 1–3 s for a new process. Cancellation, timeout or
/// a broken stream kill the worker; it is released after three idle minutes, or when models change.
/// Text is held only for the request in progress.
/// </summary>
internal sealed class ResidentTranslationClient(string modelRoot) : IDisposable
{
    private static readonly TimeSpan IdleLifetime = TimeSpan.FromMinutes(3);
    private readonly SemaphoreSlim gate = new(1, 1);
    private Process? process;
    private WorkerJob? job;
    private Task? diagnostics;
    private long generation;
    private bool disposed;
    internal int? ProcessId => process is { HasExited: false } ? process.Id : null;

    internal async Task<string> TranslateAsync(IReadOnlyList<TranslationSegment> segments, string target, CancellationToken token)
    {
        if (segments.All(x => x.Source == target)) return string.Concat(segments.Select(x => x.Text));
        if (segments.Any(x => !TranslationPlanner.IsSupported(x.Source, target))) throw new QuietException(ErrorCode.UnsupportedPair);
        await gate.WaitAsync(token);
        Interlocked.Increment(ref generation);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromMinutes(10));
            try
            {
                Start();
                await WorkerProtocol.WriteAsync(process!.StandardInput.BaseStream, new TranslationRequest(segments, target), deadline.Token);
                var response = await WorkerProtocol.ReadAsync<ResidentTranslationResponse>(process.StandardOutput.BaseStream, deadline.Token);
                token.ThrowIfCancellationRequested();
                if (!response.Success || string.IsNullOrWhiteSpace(response.Text))
                {
                    if (response.Failure == "MEMORY_LIMIT") Release(); // Its state is no longer trusted.
                    throw new QuietException(ErrorCode.TranslationFailure);
                }
                if (response.Text.Length > TextLimits.Maximum) throw new QuietException(ErrorCode.TooLong);
                return response.Text;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { Release(); throw new QuietException(ErrorCode.TranslationTimeout); }
            catch (OperationCanceledException) { Release(); throw; } // Stops inference at once.
            catch (QuietException) { throw; }
            catch { Release(); throw new QuietException(ErrorCode.TranslationFailure); }
        }
        finally
        {
            if (!disposed && process is not null) ScheduleIdleRelease();
            gate.Release();
        }
    }

    private void Start()
    {
        if (process is { HasExited: false }) return;
        Release();
        var runtime = Path.Combine(AppContext.BaseDirectory, "translation-runtime", "python.exe");
        if (!File.Exists(runtime) || !Directory.Exists(modelRoot)) throw new QuietException(ErrorCode.ModelMissing);
        process = new Process { StartInfo = new(runtime)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(runtime)!
        } };
        foreach (var argument in new[] { "-I", "-B", Path.Combine(AppContext.BaseDirectory, "translation", "worker.py"), modelRoot, "--serve" })
            process.StartInfo.ArgumentList.Add(argument);
        process.StartInfo.Environment["OMP_NUM_THREADS"] = "2";
        process.StartInfo.Environment["OPENBLAS_NUM_THREADS"] = "1";
        try
        {
            process.Start();
            job = new WorkerJob(process.Handle, 1536UL * 1024 * 1024);
            // Drain directly to a discard sink. Do not buffer/log dependency diagnostics.
            diagnostics = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        }
        catch { Release(); throw; }
    }

    private void ScheduleIdleRelease()
    {
        long current = Interlocked.Read(ref generation);
        _ = Task.Delay(IdleLifetime).ContinueWith(async _ =>
        {
            if (Interlocked.Read(ref generation) != current || !await gate.WaitAsync(0)) return;
            try { if (Interlocked.Read(ref generation) == current) Release(); }
            finally { gate.Release(); }
        }, TaskScheduler.Default);
    }

    /// <summary>Unloads the model, for example before translation models are replaced.</summary>
    internal async Task ResetAsync()
    {
        await gate.WaitAsync();
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
        if (diagnostics is not null) { _ = diagnostics.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted); diagnostics = null; }
    }

    public void Dispose() { disposed = true; Release(); }
}
