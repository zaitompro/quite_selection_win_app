using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Threading.Channels;
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
    private NamedPipeServerStream? pipe;
    // Commands are written by one background loop, so the UI thread never waits on the pipe.
    private Channel<MagnifierMessage>? outgoing;
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
        try { job = new WorkerJob(process.Handle); } catch (Exception ex) when (ex is QuietException or Win32Exception or InvalidOperationException) { } // It also ends when the pipe closes.
        using var connect = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await pipe.WaitForConnectionAsync(connect.Token); }
        catch (OperationCanceledException) { Release(); return false; }
        outgoing = Channel.CreateUnbounded<MagnifierMessage>(new UnboundedChannelOptions { SingleReader = true });
        _ = Task.Run(() => WriteLoop(pipe, outgoing.Reader));
        _ = Task.Run(ReadLoop);
        Configure(settings);
        return true;
    }

    private static async Task WriteLoop(Stream stream, ChannelReader<MagnifierMessage> reader)
    {
        try
        {
            await foreach (var message in reader.ReadAllAsync().ConfigureAwait(false))
                await WorkerProtocol.WriteAsync(stream, message, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or QuietException) { } // The read loop reports the exit.
    }

    private async Task ReadLoop()
    {
        var stream = pipe;
        try
        {
            while (stream is not null)
            {
                var message = await WorkerProtocol.ReadAsync<MagnifierMessage>(stream, CancellationToken.None).ConfigureAwait(false);
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

    private void Send(MagnifierMessage message) => outgoing?.Writer.TryWrite(message);

    internal void Configure(MagnifierSettings settings) => Send(new("configure", Settings: settings));
    internal void Zoom(int notches) => Send(new("zoom", Notches: notches));
    internal void Toggle() => Send(new("toggle"));

    private void Release()
    {
        outgoing?.Writer.TryComplete(); outgoing = null;
        var stream = pipe; pipe = null;
        stream?.Dispose(); // The process sees the pipe close, restores the screen and exits.
        if (process is not null)
        {
            // A UIAccess process cannot be terminated from this one (access denied); it exits on its own.
            try { if (!process.HasExited && !process.WaitForExit(2000)) process.Kill(); }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
            process.Dispose(); process = null;
        }
        job?.Dispose(); job = null;
        scale = 1;
    }

    public void Dispose() => Release();
}
