using System.Windows.Threading;
using QuietSelection.App.Magnifier;

namespace QuietSelection.App;

internal sealed partial class AppShell
{
    // In the package the magnifier runs in its own UIAccess process (exact clicks on title bars);
    // without it, or if Windows refuses that process, it runs here.
    private readonly MagnifierService magnifier = new();
    private readonly MagnifierHostClient magnifierHost = new();
    private bool magnifierHostStarting;
    private DispatcherTimer? scaleSave;
    private bool magnifierEventsAttached;

    internal double MagnifierScale => magnifierHost.Running ? magnifierHost.Scale : magnifier.Scale;
    internal bool MagnifierRunning => magnifier.Running || magnifierHost.Running;
    internal bool MagnifierInSeparateProcess => magnifierHost.Running;
    /// <summary>Clicks on the magnified screen reach what is drawn there; null until the first zoom.</summary>
    internal bool? MagnifierInputTransform { get; private set; }

    /// <summary>Starts, reconfigures or stops the magnifier to match the saved settings.</summary>
    private void ApplyMagnifier()
    {
        if (!running || exiting) return;
        if (!magnifierEventsAttached)
        {
            magnifierEventsAttached = true;
            magnifier.Failed += message => application.Dispatcher.BeginInvoke(() => SetStatus(message));
            magnifier.ScaleChanged += scale => application.Dispatcher.BeginInvoke(() => OnMagnifierScale(scale));
            magnifier.InputTransformChanged += active => application.Dispatcher.BeginInvoke(() => { MagnifierInputTransform = active; RaiseChanged(); });
            magnifierHost.Failed += message => application.Dispatcher.BeginInvoke(() => SetStatus(message));
            magnifierHost.ScaleChanged += scale => application.Dispatcher.BeginInvoke(() => OnMagnifierScale(scale));
            magnifierHost.InputTransformChanged += active => application.Dispatcher.BeginInvoke(() => { MagnifierInputTransform = active; RaiseChanged(); });
            magnifierHost.Exited += () => application.Dispatcher.BeginInvoke(OnMagnifierHostExited);
        }
        var options = Settings.Magnifier;
        bool active = MagnifierRunning || magnifierHostStarting;
        if (options.Enabled && !active) _ = StartMagnifierAsync(options);
        else if (!options.Enabled && active) { magnifierHost.Dispose(); magnifier.Dispose(); MagnifierInputTransform = null; }
        else if (options.Enabled && magnifierHost.Running) magnifierHost.Configure(options);
        else if (options.Enabled && magnifier.Running) magnifier.Configure(options);
        RaiseChanged();
    }

    private async Task StartMagnifierAsync(MagnifierSettings options)
    {
        magnifierHostStarting = true;
        try
        {
            bool separate = Platform.NativeMethods.IsPackaged && await magnifierHost.StartAsync(options);
            if (!separate && Settings.Magnifier.Enabled && !exiting) magnifier.Start(Settings.Magnifier);
        }
        finally { magnifierHostStarting = false; }
        RaiseChanged();
    }

    // The separate process ended on its own: keep magnifying here (without exact title-bar clicks).
    private void OnMagnifierHostExited()
    {
        magnifierHost.Dispose();
        MagnifierInputTransform = null;
        if (exiting || !Settings.Magnifier.Enabled) return;
        magnifier.Start(Settings.Magnifier);
        SetStatus("Процесс лупы завершился; лупа продолжает работать внутри приложения.");
    }

    // The last zoom is remembered for the toggle shortcut; written once the wheel stops.
    private void OnMagnifierScale(double scale)
    {
        if (scale > 1) Settings.Magnifier.LastScale = scale;
        scaleSave ??= new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => { scaleSave!.Stop(); SaveQuietly(); }, application.Dispatcher);
        scaleSave.Stop(); scaleSave.Start();
        RaiseChanged();
    }

    private void SaveQuietly()
    {
        if (!SettingsWritable) return;
        try { store.Save(Settings); } catch { }
    }

    internal void ToggleMagnifier()
    {
        EnsureMagnifierEnabled();
        if (magnifierHost.Running) magnifierHost.Toggle(); else magnifier.Toggle();
    }

    internal void SetMagnifierInvert(bool value)
    {
        EnsureMagnifierEnabled();
        Settings.Magnifier.Invert = value; Save();
        SetStatus(value ? "Негатив включён." : "Негатив выключен.");
    }

    internal void SetMagnifierFollowCaret(bool value)
    {
        Settings.Magnifier.FollowCaret = value; Save();
        SetStatus(value ? "Лупа следует за кареткой при наборе." : "Лупа следует только за указателем.");
    }

    // A magnifier shortcut implies wanting the magnifier, even if it was off in settings.
    private void EnsureMagnifierEnabled()
    {
        if (Settings.Magnifier.Enabled) return;
        Settings.Magnifier.Enabled = true; Save();
    }

    private void RunCommand(ReadingCommand command)
    {
        switch (command)
        {
            case ReadingCommand.ToggleMagnifier: ToggleMagnifier(); break;
            case ReadingCommand.InvertColors: SetMagnifierInvert(!Settings.Magnifier.Invert); break;
            case ReadingCommand.FollowCaret: SetMagnifierFollowCaret(!Settings.Magnifier.FollowCaret); break;
            case ReadingCommand.DarkenWindow: _ = DarkenForegroundWindowAsync(); break;
            default: _ = ReadAsync(command); break;
        }
    }

    private void StopMagnifier()
    {
        scaleSave?.Stop();
        magnifierHost.Dispose(); // Its process restores the screen when the pipe closes.
        magnifier.Dispose(); // Restores 1× zoom, normal colors and the pointer.
    }
}
