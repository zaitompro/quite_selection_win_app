using System.IO.Pipes;
using QuietSelection.App.Workers;

namespace QuietSelection.App.Magnifier;

/// <summary>Messages between the app and the magnifier process. No screen or text content.</summary>
internal sealed record MagnifierMessage(string Kind, MagnifierSettings? Settings = null, int Notches = 0, double Scale = 0, string Text = "", bool Flag = false);

/// <summary>
/// The magnifier in its own process (<c>QuietSelection.Magnifier.exe --magnifier-host</c>). In the
/// package that executable is the app's launcher with <c>uiAccess="true"</c> in its manifest, so only
/// the magnifier gets UIAccess, which MagSetInputTransform needs for title bars and caption buttons
/// to receive clicks where they are drawn. It ends when the app closes the pipe; Windows then
/// restores 1x zoom and normal colors.
/// </summary>
internal static class MagnifierHost
{
    internal static int Run(string name)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            pipe.Connect(5_000);
            var writeLock = new object();
            void Send(MagnifierMessage message)
            {
                lock (writeLock)
                {
                    try { WorkerProtocol.WriteAsync(pipe, message, CancellationToken.None).GetAwaiter().GetResult(); }
                    catch (IOException) { } // The app is gone; the read loop ends too.
                }
            }
            using var service = new MagnifierService();
            service.Failed += text => Send(new("failed", Text: text));
            service.ScaleChanged += scale => Send(new("scale", Scale: scale));
            service.InputTransformChanged += active => Send(new("input", Flag: active));
            try
            {
                while (true)
                {
                    var message = WorkerProtocol.ReadAsync<MagnifierMessage>(pipe, CancellationToken.None).GetAwaiter().GetResult();
                    switch (message.Kind)
                    {
                        case "configure" when message.Settings is not null:
                            if (service.Running) service.Configure(message.Settings); else service.Start(message.Settings);
                            break;
                        case "zoom": service.Zoom(message.Notches); break;
                        case "toggle": service.Toggle(); break;
                    }
                }
            }
            catch (EndOfStreamException) { }
            return 0;
        }
        catch { return 1; }
    }
}
