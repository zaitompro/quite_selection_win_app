using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace QuietSelection.App.Platform;

/// <summary>
/// Global shortcuts for reading commands. A replacement is registered before the old one is
/// released, so a taken combination never leaves a command without its previous shortcut.
/// </summary>
internal sealed class HotkeyManager : IDisposable
{
    private const int WmHotkey = 0x0312;
    private readonly HwndSource source = new(new HwndSourceParameters("QuietSelection.Commands") { ParentWindow = new nint(-3) });
    private readonly Dictionary<ReadingCommand, (int Id, Chord Chord)> registrations = [];
    private int nextId = 100;
    private bool suspended;
    internal event Action<ReadingCommand>? Invoked;

    internal HotkeyManager() { source.AddHook(WndProc); }

    /// <summary>Assigns a shortcut; an empty string removes it. False when invalid, duplicate or taken.</summary>
    internal bool Set(ReadingCommand command, string text)
    {
        registrations.TryGetValue(command, out var old);
        if (text.Length == 0)
        {
            if (old.Id != 0 && !suspended) UnregisterHotKey(source.Handle, old.Id);
            registrations.Remove(command); return true;
        }
        if (Chord.Parse(text) is not { } chord) return false;
        if (old.Id != 0 && old.Chord == chord) return true;
        if (registrations.Any(x => x.Key != command && x.Value.Chord == chord)) return false;
        int id = nextId++;
        if (!RegisterHotKey(source.Handle, id, chord.NativeModifiers, chord.VirtualKey)) return false;
        if (suspended) UnregisterHotKey(source.Handle, id); // Only checked for availability.
        if (old.Id != 0 && !suspended) UnregisterHotKey(source.Handle, old.Id);
        registrations[command] = (id, chord);
        return true;
    }

    /// <summary>Releases all shortcuts while a new combination is recorded, so pressing it does not start reading.</summary>
    internal void Suspend()
    {
        if (suspended) return;
        suspended = true;
        foreach (var entry in registrations.Values) UnregisterHotKey(source.Handle, entry.Id);
    }

    /// <summary>Registers the shortcuts again. Returns commands whose combination another app took meanwhile.</summary>
    internal IReadOnlyList<ReadingCommand> Resume()
    {
        if (!suspended) return [];
        suspended = false;
        var lost = new List<ReadingCommand>();
        foreach (var entry in registrations.ToArray())
            if (!RegisterHotKey(source.Handle, entry.Value.Id, entry.Value.Chord.NativeModifiers, entry.Value.Chord.VirtualKey))
            { registrations.Remove(entry.Key); lost.Add(entry.Key); }
        return lost;
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WmHotkey && !suspended)
        {
            foreach (var entry in registrations)
                if (entry.Value.Id == (int)wParam) { Invoked?.Invoke(entry.Key); handled = true; break; }
        }
        return 0;
    }

    public void Dispose()
    {
        if (!suspended) foreach (var entry in registrations.Values) UnregisterHotKey(source.Handle, entry.Id);
        source.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint hwnd, int id);
}
