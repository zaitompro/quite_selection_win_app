using System.Diagnostics;
using System.IO.Pipes;
using QuietSelection.App.Workers;

namespace QuietSelection.App.Speech;

/// <summary>
/// One resident WinRT/SAPI helper process. Native synthesis cannot be interrupted,
/// so cancellation, timeout or an IPC failure kill it; the next request starts a new one.
/// </summary>
internal sealed class NativeSpeechClient : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private NamedPipeServerStream? pipe;
    private Process? process;
    private WorkerJob? job;
    private bool disposed;
    private long generation;
    private static readonly TimeSpan IdleLifetime = TimeSpan.FromMinutes(2);
    internal int? ProcessId => process is { HasExited: false } ? process.Id : null;

    // Keeps the helper warm between nearby readings without holding its memory indefinitely.
    private void ScheduleIdleRelease()
    {
        long current = Interlocked.Increment(ref generation);
        _ = Task.Delay(IdleLifetime).ContinueWith(async _ =>
        {
            if (Interlocked.Read(ref generation) != current || !await gate.WaitAsync(0)) return;
            try { if (Interlocked.Read(ref generation) == current) Release(); }
            finally { gate.Release(); }
        }, TaskScheduler.Default);
    }

    internal async Task<WorkerResponse> SynthesizeAsync(WorkerRequest request, TimeSpan timeout, CancellationToken token)
    {
        if (request.Kind != "synthesize-wave") throw new ArgumentException("NATIVE_REQUEST_KIND");
        await gate.WaitAsync(token);
        Interlocked.Increment(ref generation);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(timeout);
            try
            {
                if (pipe is null || process is null || process.HasExited) await StartAsync(deadline.Token);
                await WorkerProtocol.WriteAsync(pipe!, request, deadline.Token);
                var result = await WorkerProtocol.ReadAsync<WorkerResponse>(pipe!, deadline.Token);
                token.ThrowIfCancellationRequested();
                if (result.Error != ErrorCode.None) throw new QuietException(result.Error);
                return result;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { Release(); throw new QuietException(ErrorCode.SpeechFailure); }
            catch (QuietException) { throw; } // A complete error response keeps the helper.
            catch { Release(); throw; }
        }
        finally
        {
            if (!disposed && process is not null) ScheduleIdleRelease();
            gate.Release();
        }
    }

    private async Task StartAsync(CancellationToken token)
    {
        Release();
        string name = "QuietSelection-Speech-" + Guid.NewGuid().ToString("N");
        pipe = new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        process = new() { StartInfo = new(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true } };
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            process.StartInfo.ArgumentList.Add(typeof(NativeSpeechClient).Assembly.Location);
        process.StartInfo.ArgumentList.Add("--native-speech-worker");
        process.StartInfo.ArgumentList.Add(name);
        process.Start();
        job = new(process.Handle);
        await pipe.WaitForConnectionAsync(token);
    }

    internal async Task ResetAsync()
    {
        await gate.WaitAsync();
        try { Release(); } finally { gate.Release(); }
    }

    private void Release()
    {
        pipe?.Dispose(); pipe = null;
        job?.Dispose(); job = null;
        if (process is not null)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                process.WaitForExit();
            }
            catch (InvalidOperationException) { } // Start failed.
            process.Dispose(); process = null;
        }
    }

    public void Dispose() { disposed = true; Release(); }
}
