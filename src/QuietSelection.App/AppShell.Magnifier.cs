using System.Windows.Threading;
using QuietSelection.App.Magnifier;

namespace QuietSelection.App;

internal sealed partial class AppShell
{
    private readonly MagnifierService magnifier = new();
    private DispatcherTimer? scaleSave;
    private bool magnifierEventsAttached;

    internal double MagnifierScale => magnifier.Scale;
    internal bool MagnifierRunning => magnifier.Running;

    /// <summary>Starts, reconfigures or stops the magnifier to match the saved settings.</summary>
    private void ApplyMagnifier()
    {
        if (!running || exiting) return;
        if (!magnifierEventsAttached)
        {
            magnifierEventsAttached = true;
            magnifier.Failed += message => application.Dispatcher.BeginInvoke(() => SetStatus(message));
            magnifier.ScaleChanged += scale => application.Dispatcher.BeginInvoke(() => OnMagnifierScale(scale));
        }
        var options = Settings.Magnifier;
        if (options.Enabled && !magnifier.Running) magnifier.Start(options);
        else if (!options.Enabled && magnifier.Running) magnifier.Dispose();
        else if (options.Enabled) magnifier.Configure(options);
        RaiseChanged();
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
        magnifier.Toggle();
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
            default: _ = ReadAsync(command); break;
        }
    }

    private void StopMagnifier()
    {
        scaleSave?.Stop();
        magnifier.Dispose(); // Restores 1× zoom, normal colors and the pointer.
    }
}
