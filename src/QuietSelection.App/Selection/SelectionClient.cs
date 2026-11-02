using System.Diagnostics;
using System.IO.Pipes;
using QuietSelection.App.Platform;
using QuietSelection.App.Workers;

namespace QuietSelection.App.Selection;

/// <summary>A single MTA helper survives successful reads; a stuck provider is killed.</summary>
internal sealed class SelectionClient : IDisposable
{
    private NamedPipeServerStream? pipe;
    private Process? process;
    private WorkerJob? job;

    // WorkerClient serializes calls, including recovery after cancellation.
    internal async Task<WorkerResponse> ReadAsync(WorkerRequest request, TimeSpan timeout, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        if (timeout == TimeSpan.Zero) deadline.Cancel();
        else deadline.CancelAfter(timeout);
        using var watchCancellation = new CancellationTokenSource();
        bool sourceChanged = false;
        // Reading under the pointer does not depend on the foreground window.
        bool guardForeground = request.Kind == "selection";
        var watch = Task.Run(async () =>
        {
            try
            {
                while (guardForeground)
                {
                    if (NativeMethods.GetForegroundWindow() != (nint)request.SourceWindow)
                    {
                        sourceChanged = true;
                        deadline.Cancel();
                        return;
                    }
                    await Task.Delay(30, watchCancellation.Token);
                }
            }
            catch (OperationCanceledException) { }
        });
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            if (pipe is null || process is null || process.HasExited)
            {
                Dispose();
                string name = "QuietSelection-Selection-" + Guid.NewGuid().ToString("N");
                pipe = new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                process = new() { StartInfo = new(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true } };
                if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
                    process.StartInfo.ArgumentList.Add(typeof(SelectionClient).Assembly.Location);
                process.StartInfo.ArgumentList.Add("--selection-worker");
                process.StartInfo.ArgumentList.Add(name);
                process.Start();
                job = new(process.Handle);
                await pipe.WaitForConnectionAsync(deadline.Token);
            }
            await WorkerProtocol.WriteAsync(pipe, request, deadline.Token);
            var result = await WorkerProtocol.ReadAsync<WorkerResponse>(pipe, deadline.Token);
            token.ThrowIfCancellationRequested();
            if (sourceChanged || (guardForeground && NativeMethods.GetForegroundWindow() != (nint)request.SourceWindow))
                throw new QuietException(ErrorCode.SourceChanged);
            // A provider's normal error is a complete response, not a broken IPC stream.
            return result;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            Dispose();
            throw new QuietException(sourceChanged ? ErrorCode.SourceChanged : ErrorCode.SelectionTimeout);
        }
        catch
        {
            Dispose();
            throw;
        }
        finally
        {
            watchCancellation.Cancel();
            await watch;
        }
    }

    public void Dispose()
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
}
