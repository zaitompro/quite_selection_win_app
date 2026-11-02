using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using QuietSelection.App.Workers;

namespace QuietSelection.App.Magnifier;

/// <summary>
/// Starts the UIAccess magnifier process from the package and forwards commands to it. Start
/// returns false when that process is absent or Windows refuses to start it (for example while its
/// signing certificate is not trusted as a root); the app then uses the in-process magnifier.
/// </summary>
internal sealed class MagnifierHostClient : IDisposable
{
    internal const string ExecutableName = "QuietSelection.Magnifier.exe";
    private readonly object writeLock = new();
    private NamedPipeServerStream? pipe;
    private Process? process;
    private WorkerJob? job;
    private double scale = 1;

    internal event Action<string>? Failed;
    internal event Action<double>? ScaleChanged;
    internal event Action<bool>? InputTransformChanged;
    /// <summary>The process ended unexpectedly; raised from a background thread.</summary>
    internal event Action? Exited;
    internal bool Running => process is { HasExited: false };
    internal double Scale => scale;

    internal static string ExecutablePath => Path.Combine(AppContext.BaseDirectory, ExecutableName);

    internal async Task<bool> StartAsync(MagnifierSettings settings)
    {
        if (!File.Exists(ExecutablePath)) return false;
        string name = "QuietSelection-Magnifier-" + Guid.NewGuid().ToString("N");
        pipe = new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            // uiAccess executables start only through ShellExecute (the AppInfo service checks them).
            process = Process.Start(new ProcessStartInfo(ExecutablePath) { UseShellExecute = true, ArgumentList = { "--magnifier-host", name } });
        }
        catch (Win32Exception) { Release(); return false; } // UIAccess refused: untrusted signature or location.
        if (process is null) { Release(); return false; }
        try { job = new WorkerJob(process.Handle); } catch (QuietException) { } // It also ends when the pipe closes.
        using var connect = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await pipe.WaitForConnectionAsync(connect.Token); }
        catch (OperationCanceledException) { Release(); return false; }
        _ = Task.Run(ReadLoop);
        Configure(settings);
        return true;
    }

    private async Task ReadLoop()
    {
        var stream = pipe;
        try
        {
            while (stream is not null)
            {
                var message = await WorkerProtocol.ReadAsync<MagnifierMessage>(stream, CancellationToken.None);
                switch (message.Kind)
                {
                    case "failed": Failed?.Invoke(message.Text); break;
                    case "scale": scale = message.Scale; ScaleChanged?.Invoke(message.Scale); break;
                    case "input": InputTransformChanged?.Invoke(message.Flag); break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or ObjectDisposedException or QuietException)
        {
            if (pipe == stream && stream is not null) Exited?.Invoke();
        }
    }

    private void Send(MagnifierMessage message)
    {
        lock (writeLock)
        {
            if (pipe is null) return;
            try { WorkerProtocol.WriteAsync(pipe, message, CancellationToken.None).GetAwaiter().GetResult(); }
            catch (IOException) { }
        }
    }

    internal void Configure(MagnifierSettings settings) => Send(new("configure", Settings: settings));
    internal void Zoom(int notches) => Send(new("zoom", Notches: notches));
    internal void Toggle() => Send(new("toggle"));

    private void Release()
    {
        var stream = pipe; pipe = null;
        stream?.Dispose(); // The process sees the pipe close and restores the screen.
        if (process is not null)
        {
            try { if (!process.HasExited && !process.WaitForExit(2000)) process.Kill(); }
            catch (InvalidOperationException) { }
            process.Dispose(); process = null;
        }
        job?.Dispose(); job = null;
        scale = 1;
    }

    public void Dispose() => Release();
}
