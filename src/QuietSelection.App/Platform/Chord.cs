using System.Windows.Input;

namespace QuietSelection.App.Platform;

/// <summary>
/// A global shortcut such as "Alt+1" or "Ctrl+Shift+F2". Ctrl or Alt is required so plain
/// typing never triggers reading; the Windows key is left to the system.
/// The text form is stored in settings and stays compatible with earlier versions.
/// </summary>
internal readonly record struct Chord(ModifierKeys Modifiers, Key Key)
{
    internal static Chord? Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        var modifiers = ModifierKeys.None;
        foreach (var part in parts[..^1])
            modifiers |= part switch { "Ctrl" => ModifierKeys.Control, "Alt" => ModifierKeys.Alt, "Shift" => ModifierKeys.Shift, _ => (ModifierKeys)(-1) };
        if ((int)modifiers < 0) return null;
        string name = parts[^1];
        Key key = name.Length == 1 && char.IsAsciiDigit(name[0]) ? Key.D0 + (name[0] - '0')
            : Enum.TryParse<Key>(name, ignoreCase: false, out var parsed) ? parsed : Key.None;
        var chord = new Chord(modifiers, key);
        return chord.IsValid ? chord : null;
    }

    /// <summary>Builds a chord from a key press; null for modifier-only presses or unsupported keys.</summary>
    internal static Chord? FromKeyPress(Key key, ModifierKeys modifiers)
    {
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin or Key.System or Key.ImeProcessed or Key.DeadCharProcessed) return null;
        var chord = new Chord(modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift), key);
        return chord.IsValid ? chord : null;
    }

    internal bool IsValid => Key != Key.None && (Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != 0
        && !(Modifiers == ModifierKeys.Alt && Key is Key.F4 or Key.Tab or Key.Space or Key.Escape)
        && !(Modifiers == (ModifierKeys.Control | ModifierKeys.Alt) && Key == Key.Delete)
        && KeyInterop.VirtualKeyFromKey(Key) != 0;

    internal uint NativeModifiers => (Modifiers.HasFlag(ModifierKeys.Alt) ? 0x1u : 0) | (Modifiers.HasFlag(ModifierKeys.Control) ? 0x2u : 0)
        | (Modifiers.HasFlag(ModifierKeys.Shift) ? 0x4u : 0) | 0x4000u; // MOD_NOREPEAT
    internal uint VirtualKey => (uint)KeyInterop.VirtualKeyFromKey(Key);

    public override string ToString()
    {
        var parts = new List<string>(4);
        if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        parts.Add(Key is >= Key.D0 and <= Key.D9 ? ((char)('0' + (Key - Key.D0))).ToString() : Key.ToString());
        return string.Join("+", parts);
    }
}
