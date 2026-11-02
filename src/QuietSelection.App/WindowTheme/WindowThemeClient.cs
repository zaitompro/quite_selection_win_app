using System.Diagnostics;
using System.IO.Pipes;
using QuietSelection.App.Workers;

namespace QuietSelection.App.WindowTheme;

/// <summary>Starts and talks to the dark-window helper. Its overlays end with it (kill-on-close job).</summary>
internal sealed class WindowThemeClient : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private NamedPipeServerStream? pipe;
    private Process? process;
    private WorkerJob? job;
    internal bool Running => process is { HasExited: false };
    internal int? ProcessId => Running ? process!.Id : null;

    internal async Task<ThemeResponse> SendAsync(ThemeRequest request, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                if (!Running || pipe is null) await StartAsync(deadline.Token);
                await WorkerProtocol.WriteAsync(pipe!, request, deadline.Token);
                return await WorkerProtocol.ReadAsync<ThemeResponse>(pipe!, deadline.Token);
            }
            catch { Release(); throw new QuietException(ErrorCode.WorkerFailure); }
        }
        finally { gate.Release(); }
    }

    private async Task StartAsync(CancellationToken token)
    {
        Release();
        string name = "QuietSelection-Theme-" + Guid.NewGuid().ToString("N");
        pipe = new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        process = new() { StartInfo = new(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true } };
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            process.StartInfo.ArgumentList.Add(typeof(WindowThemeClient).Assembly.Location);
        process.StartInfo.ArgumentList.Add("--window-theme-worker");
        process.StartInfo.ArgumentList.Add(name);
        process.Start();
        job = new(process.Handle);
        await pipe.WaitForConnectionAsync(token);
    }

    internal async Task StopAsync()
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
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); process.WaitForExit(2000); }
            catch (InvalidOperationException) { }
            process.Dispose(); process = null;
        }
    }

    public void Dispose() => Release();
}
