using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace QuietSelection.App.Platform;

internal sealed class HotkeyManager : IDisposable
{
    private readonly HwndSource source = new(new HwndSourceParameters("QuietSelection.Commands") { ParentWindow = new nint(-3) });
    private readonly Dictionary<ReadingCommand, (int Id, string Chord)> registrations = [];
    private int nextId = 100;
    internal event Action<ReadingCommand>? Invoked;
    internal static readonly string[] Choices = ["", "Alt+1", "Alt+2", "Alt+3", "Alt+4", "Ctrl+Shift+F1", "Ctrl+Shift+F2", "Ctrl+Shift+F3", "Ctrl+Shift+F4", "Ctrl+Alt+F1", "Ctrl+Alt+F2", "Ctrl+Alt+F3", "Ctrl+Alt+F4"];
    internal HotkeyManager() { source.AddHook(WndProc); }
    internal bool Set(ReadingCommand command, string chord)
    {
        if (!Choices.Contains(chord)) return false;
        if (registrations.TryGetValue(command, out var old) && old.Chord == chord) return true;
        if (chord.Length == 0)
        {
            if (old.Id != 0) UnregisterHotKey(source.Handle, old.Id);
            registrations.Remove(command); return true;
        }
        if (registrations.Any(x => x.Key != command && x.Value.Chord == chord)) return false;
        bool numberChord = chord.StartsWith("Alt+", StringComparison.Ordinal);
        var modifiers = (uint)(0x4000 | (numberChord ? 0x0001 : 0x0002 | (chord.Contains("Shift") ? 0x0004 : 0x0001)));
        uint key = numberChord ? chord[^1] : 0x70u + (uint)(chord[^1] - '1');
        int id = nextId++;
        if (!RegisterHotKey(source.Handle, id, modifiers, key)) return false;
        if (old.Id != 0) UnregisterHotKey(source.Handle, old.Id);
        registrations[command] = (id, chord);
        return true;
    }
    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == 0x0312)
        {
            foreach (var entry in registrations)
                if (entry.Value.Id == (int)wParam) { Invoked?.Invoke(entry.Key); handled = true; break; }
        }
        return 0;
    }
    public void Dispose()
    {
        foreach (var entry in registrations.Values) UnregisterHotKey(source.Handle, entry.Id);
        source.Dispose();
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint hwnd, int id);
}
