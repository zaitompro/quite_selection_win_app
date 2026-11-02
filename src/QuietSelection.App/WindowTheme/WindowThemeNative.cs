using System.Runtime.InteropServices;

namespace QuietSelection.App.WindowTheme;

/// <summary>Win32, DWM and windowed Magnification API calls for dark window overlays.</summary>
internal static class WindowThemeNative
{
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { internal int Left, Top, Right, Bottom; internal readonly int Width => Right - Left; internal readonly int Height => Bottom - Top; }
    [StructLayout(LayoutKind.Sequential)] internal struct Message { internal nint Window; internal uint Id; internal nint W, L; internal uint Time; internal int X, Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WindowClass
    {
        internal uint Size, Style; internal WindowProc Proc; internal int ClassExtra, WindowExtra;
        internal nint Instance, Icon, Cursor, Background; internal string? MenuName; internal string ClassName; internal nint SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ColorEffect { [MarshalAs(UnmanagedType.ByValArray, SizeConst = 25)] internal float[] Matrix; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Transform { [MarshalAs(UnmanagedType.ByValArray, SizeConst = 9)] internal float[] Matrix; }

    internal delegate nint WindowProc(nint window, uint message, nint w, nint l);
    internal delegate void WinEventProc(nint hook, uint eventType, nint window, int idObject, int idChild, uint thread, uint time);

    [DllImport("magnification.dll")] internal static extern bool MagInitialize();
    [DllImport("magnification.dll")] internal static extern bool MagUninitialize();
    [DllImport("magnification.dll")] internal static extern bool MagSetWindowSource(nint window, Rect source);
    [DllImport("magnification.dll")] internal static extern bool MagSetWindowTransform(nint window, ref Transform transform);
    [DllImport("magnification.dll")] internal static extern bool MagSetColorEffect(nint window, ref ColorEffect effect);
    [DllImport("magnification.dll")] internal static extern bool MagSetWindowFilterList(nint window, int mode, int count, nint[] windows);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern ushort RegisterClassEx(ref WindowClass windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint CreateWindowEx(uint exStyle, string className, string? title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] internal static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] internal static extern nint DefWindowProc(nint window, uint message, nint w, nint l);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] internal static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] internal static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] internal static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] internal static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll")] internal static extern bool SetLayeredWindowAttributes(nint window, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] internal static extern bool InvalidateRect(nint window, nint rect, bool erase);
    [DllImport("user32.dll")] internal static extern nuint SetTimer(nint window, nuint id, uint milliseconds, nint callback);
    [DllImport("user32.dll")] internal static extern bool KillTimer(nint window, nuint id);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(nint window, char[] name, int length);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint FindWindowEx(nint parent, nint after, string? className, string? title);
    [DllImport("user32.dll")] internal static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll")] internal static extern bool PrintWindow(nint window, nint dc, uint flags);
    [DllImport("user32.dll")] internal static extern nint SetWinEventHook(uint min, uint max, nint module, WinEventProc proc, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] internal static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll")] internal static extern int GetMessage(out Message message, nint window, uint min, uint max);
    [DllImport("user32.dll")] internal static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] internal static extern nint DispatchMessage(ref Message message);
    [DllImport("user32.dll")] internal static extern bool PostThreadMessage(uint thread, uint message, nint w, nint l);
    [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern nint GetModuleHandle(string? name);
    [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(nint window, int attribute, out Rect value, int size);
    [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(nint window, int attribute, out int value, int size);

    internal static string ClassName(nint window)
    {
        var buffer = new char[256];
        int length = GetClassName(window, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : "";
    }

    /// <summary>Visible window bounds without the invisible resize border (physical pixels).</summary>
    internal static bool VisibleBounds(nint window, out Rect rect) =>
        DwmGetWindowAttribute(window, 9, out rect, Marshal.SizeOf<Rect>()) == 0 || GetWindowRect(window, out rect);

    /// <summary>Hidden by DWM: a window on another virtual desktop or a suspended app.</summary>
    internal static bool Cloaked(nint window) => DwmGetWindowAttribute(window, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0;
}
