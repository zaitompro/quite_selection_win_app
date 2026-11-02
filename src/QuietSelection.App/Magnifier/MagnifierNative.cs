using System.Runtime.InteropServices;

namespace QuietSelection.App.Magnifier;

/// <summary>Windows Magnification API (full-screen mode) and the input/accessibility calls the magnifier uses.</summary>
internal static class MagnifierNative
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct ColorEffect
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 25)] internal float[] Matrix;
        internal static ColorEffect Identity() => new() { Matrix = [1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1] };
        // Each channel becomes 1 − value; alpha is kept.
        internal static ColorEffect Invert() => new() { Matrix = [-1, 0, 0, 0, 0, 0, -1, 0, 0, 0, 0, 0, -1, 0, 0, 0, 0, 0, 1, 0, 1, 1, 1, 0, 1] };
    }

    [StructLayout(LayoutKind.Sequential)] internal struct Point { internal int X, Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { internal int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct GuiThreadInfo
    {
        internal int Size, Flags;
        internal nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        internal Rect CaretRect;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseHookData { internal Point Point; internal uint MouseData, Flags, Time; internal nuint ExtraInfo; }

    // INPUT with KEYBDINPUT on x64: the union starts at offset 8 and is 32 bytes (sized by MOUSEINPUT).
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    internal struct KeyboardInput
    {
        [FieldOffset(0)] internal uint Type;
        [FieldOffset(8)] internal ushort Vk;
        [FieldOffset(10)] internal ushort Scan;
        [FieldOffset(12)] internal uint Flags;
        [FieldOffset(16)] internal uint Time;
        [FieldOffset(24)] internal nuint Extra;
    }

    [DllImport("magnification.dll", SetLastError = true)] internal static extern bool MagInitialize();
    [DllImport("magnification.dll")] internal static extern bool MagUninitialize();
    [DllImport("magnification.dll", SetLastError = true)] internal static extern bool MagSetFullscreenTransform(float level, int x, int y);
    [DllImport("magnification.dll")] internal static extern bool MagGetFullscreenTransform(out float level, out int x, out int y);
    [DllImport("magnification.dll")] internal static extern bool MagSetFullscreenColorEffect(ref ColorEffect effect);
    [DllImport("magnification.dll")] internal static extern bool MagShowSystemCursor(bool show);
    [DllImport("dwmapi.dll")] internal static extern int DwmFlush();

    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] internal static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);

    internal delegate nint HookProc(int code, nint wParam, nint lParam);
    [DllImport("user32.dll", SetLastError = true)] internal static extern nint SetWindowsHookEx(int id, HookProc proc, nint module, uint thread);
    [DllImport("user32.dll")] internal static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] internal static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);

    internal delegate void WinEventProc(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint thread, uint time);
    [DllImport("user32.dll")] internal static extern nint SetWinEventHook(uint min, uint max, nint module, WinEventProc proc, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] internal static extern bool UnhookWinEvent(nint hook);

    [DllImport("user32.dll")] internal static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] internal static extern bool ClientToScreen(nint window, ref Point point);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll")] internal static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] internal static extern bool LogicalToPhysicalPointForPerMonitorDPI(nint window, ref Point point);
    [DllImport("user32.dll")] internal static extern uint SendInput(uint count, KeyboardInput[] inputs, int size);

    [StructLayout(LayoutKind.Sequential)] internal struct Message { internal nint Window; internal uint Id; internal nint W, L; internal uint Time; internal Point Point; }
    [DllImport("user32.dll")] internal static extern int GetMessage(out Message message, nint window, uint min, uint max);
    [DllImport("user32.dll")] internal static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] internal static extern nint DispatchMessage(ref Message message);
    [DllImport("user32.dll")] internal static extern bool PostThreadMessage(uint thread, uint message, nint w, nint l);
    [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();

    // AccessibleObjectFromEvent: caret location in screen coordinates for apps with an MSAA caret.
    [DllImport("oleacc.dll")]
    internal static extern int AccessibleObjectFromEvent(nint window, int objectId, int childId,
        [MarshalAs(UnmanagedType.Interface)] out Accessibility.IAccessible accessible, out object child);
}
