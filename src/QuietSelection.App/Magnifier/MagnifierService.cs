using System.Diagnostics;
using System.Runtime.InteropServices;
using static QuietSelection.App.Magnifier.MagnifierNative;

namespace QuietSelection.App.Magnifier;

/// <summary>
/// Full-screen magnifier on three threads, so nothing slow can make the pointer stutter:
/// <list type="bullet">
/// <item>render: once per DWM frame applies zoom, offset, color inversion and cursor visibility;
/// sleeps when the zoom is 1× and colors are normal;</item>
/// <item>mouse: a low-level hook that only turns the wheel into zoom while the modifier is held;</item>
/// <item>caret: accessibility events for the text caret (only while zoomed and caret following is on).</item>
/// </list>
/// DWM does the scaling and inversion on the GPU, like Windows Magnifier. Only caret rectangles
/// are read from other apps, never their text.
/// </summary>
internal sealed class MagnifierService : IDisposable
{
    private const int WmQuit = 0x0012, WmMask = 0x8001, WmMouseWheel = 0x020A, WmMiddleDown = 0x0207, WmMiddleUp = 0x0208, MouseLowLevel = 14;
    private const int CaretObject = -8;
    private const uint EventShow = 0x8002, EventLocationChange = 0x800B;
    private static readonly HashSet<string> ShellProcesses = new(StringComparer.OrdinalIgnoreCase)
        { "SearchHost", "StartMenuExperienceHost", "ShellExperienceHost", "TextInputHost" };

    private readonly object gate = new();
    private readonly AutoResetEvent wake = new(false);
    private readonly Dictionary<uint, bool> shellByProcess = [];
    private MagnifierSettings options = new();
    private double targetScale = 1, lastScale = 2;
    private bool invert;
    private MagRect? caret;
    private long caretVersion;
    private int wheelRemainder;
    private bool middleSwallowed; // The matching button-up is swallowed too.
    private volatile bool stopping;
    private Thread? render, mouse, caretThread;
    private uint mouseThreadId, caretThreadId;
    // Delegates kept alive for the native hooks.
    private HookProc? mouseProc;
    private WinEventProc? caretProc;

    /// <summary>Raised from a background thread; the text is a user-facing status.</summary>
    internal event Action<string>? Failed;
    /// <summary>Raised from a background thread when the target zoom changes.</summary>
    internal event Action<double>? ScaleChanged;
    internal double Scale { get { lock (gate) return targetScale; } }
    internal bool Inverted { get { lock (gate) return invert; } }
    internal bool Running => render is not null;

    internal void Start(MagnifierSettings settings)
    {
        if (render is not null) return;
        Configure(settings);
        if (Process.GetProcessesByName("Magnify").Length > 0)
            Failed?.Invoke("Закройте экранную лупу Windows (Win+Esc): две лупы не работают одновременно.");
        stopping = false;
        render = new Thread(RenderLoop) { IsBackground = true, Name = "magnifier-render", Priority = ThreadPriority.AboveNormal };
        mouse = new Thread(MouseLoop) { IsBackground = true, Name = "magnifier-mouse", Priority = ThreadPriority.Highest };
        caretThread = new Thread(CaretLoop) { IsBackground = true, Name = "magnifier-caret" };
        caretThread.SetApartmentState(ApartmentState.STA); // Accessibility COM calls.
        render.Start(); mouse.Start(); caretThread.Start();
    }

    internal void Configure(MagnifierSettings settings)
    {
        lock (gate)
        {
            options = new MagnifierSettings
            {
                Enabled = settings.Enabled, Modifier = settings.Modifier, Step = settings.Step, LastScale = settings.LastScale,
                Invert = settings.Invert, FollowCaret = settings.FollowCaret, HideCursorWhileTyping = settings.HideCursorWhileTyping,
                MiddleClick = settings.MiddleClick, InvertStyle = settings.InvertStyle
            };
            invert = settings.Invert;
            lastScale = Math.Max(1.25, settings.LastScale);
            if (!settings.FollowCaret) caret = null;
        }
        wake.Set();
    }

    internal void Zoom(int notches)
    {
        double scale;
        lock (gate)
        {
            targetScale = MagnifierMath.NextScale(targetScale, notches, options.Step);
            if (targetScale > 1) lastScale = targetScale;
            scale = targetScale;
        }
        wake.Set(); ScaleChanged?.Invoke(scale);
    }

    /// <summary>Turns zoom off, or back on at the last used level.</summary>
    internal void Toggle()
    {
        double scale;
        lock (gate) { targetScale = targetScale > 1 ? 1 : lastScale; scale = targetScale; }
        wake.Set(); ScaleChanged?.Invoke(scale);
    }

    // ---- Render thread --------------------------------------------------------------------

    private void RenderLoop()
    {
        if (!MagInitialize()) { Failed?.Invoke("Не удалось запустить лупу Windows (Magnification API)."); render = null; return; }
        double scale = 1; string appliedStyle = "Classic"; bool invertApplied = false, cursorHidden = false, caretMode = false, reported = false;
        (float Level, int X, int Y) applied = (1, 0, 0);
        long caretSeen = 0; Point caretStart = default; MagPoint offset = default;
        try
        {
            while (!stopping)
            {
                double target; bool wantInvert; MagRect? caretRect; long version; bool follow, hide; string style;
                lock (gate)
                {
                    target = targetScale; wantInvert = invert; caretRect = caret; version = caretVersion;
                    follow = options.FollowCaret; hide = options.HideCursorWhileTyping; style = options.InvertStyle;
                }
                bool idle = scale == 1 && target == 1 && wantInvert == invertApplied && (!wantInvert || style == appliedStyle) && !cursorHidden && applied.Level == 1;
                if (idle) { wake.WaitOne(); continue; }
                if (DwmFlush() < 0) Thread.Sleep(16); // No composition (locked or display off).

                if (wantInvert != invertApplied || (wantInvert && style != appliedStyle))
                {
                    var effect = wantInvert ? ColorEffect.Invert(style == "Smart") : ColorEffect.Identity();
                    if (MagSetFullscreenColorEffect(ref effect)) { invertApplied = wantInvert; appliedStyle = style; }
                    else if (!reported) { reported = true; Failed?.Invoke("Не удалось включить негатив экрана."); }
                }
                // Short ease towards the new zoom; the pointer stays centered while it changes.
                scale = Math.Abs(target - scale) < 0.01 ? target : scale + (target - scale) * 0.35;

                GetCursorPos(out var cursor);
                var screen = new MagPoint(GetSystemMetrics(0), GetSystemMetrics(1));
                if (version != caretSeen)
                {
                    caretSeen = version;
                    if (follow && caretRect is not null && scale > 1) { caretMode = true; caretStart = cursor; }
                }
                // Moving the mouse returns control to the pointer.
                if (caretMode && (Math.Abs(cursor.X - caretStart.X) > 8 || Math.Abs(cursor.Y - caretStart.Y) > 8 || !follow || caretRect is null))
                    caretMode = false;
                offset = caretMode && caretRect is { } r
                    ? MagnifierMath.FollowCaret(offset, r, screen, scale)
                    : MagnifierMath.Center(new(cursor.X, cursor.Y), screen, scale);

                bool wantHidden = caretMode && hide && scale > 1;
                if (wantHidden != cursorHidden && MagShowSystemCursor(!wantHidden)) cursorHidden = wantHidden;

                var next = scale <= 1 ? (1f, 0, 0) : ((float)scale, (int)Math.Round(offset.X), (int)Math.Round(offset.Y));
                if (next != applied)
                {
                    if (MagSetFullscreenTransform(next.Item1, next.Item2, next.Item3)) applied = next;
                    else if (!reported)
                    {
                        reported = true;
                        int error = Marshal.GetLastWin32Error();
                        Failed?.Invoke($"Лупа недоступна (код {error}, {next.Item1:0.###}× {next.Item2},{next.Item3}). Закройте экранную лупу Windows (Win+Esc).");
                    }
                }
            }
        }
        finally
        {
            // Leave the screen exactly as it was.
            MagShowSystemCursor(true);
            var identity = ColorEffect.Identity();
            MagSetFullscreenColorEffect(ref identity);
            MagSetFullscreenTransform(1, 0, 0);
            MagUninitialize();
        }
    }

    // ---- Mouse thread ---------------------------------------------------------------------

    private void MouseLoop()
    {
        mouseThreadId = GetCurrentThreadId();
        mouseProc = MouseHook;
        nint hook = SetWindowsHookEx(MouseLowLevel, mouseProc, GetModuleHandle(null), 0);
        if (hook == 0) { Failed?.Invoke("Не удалось перехватить колесо мыши для лупы."); return; }
        try { PumpMessages(onMessage: id => { if (id == WmMask) SendMaskKey(); }); }
        finally { UnhookWindowsHookEx(hook); }
    }

    // Runs for every mouse event in the system: no allocations, locks held for microseconds.
    private nint MouseHook(int code, nint wParam, nint lParam)
    {
        if (code >= 0 && wParam == WmMouseWheel)
        {
            string modifier; bool enabled;
            lock (gate) { modifier = options.Modifier; enabled = options.Enabled; }
            if (enabled && ModifierHeld(modifier))
            {
                short delta = (short)(Marshal.PtrToStructure<MouseHookData>(lParam).MouseData >> 16);
                wheelRemainder += delta; // Touchpads send fractions of a notch.
                int notches = wheelRemainder / 120; wheelRemainder %= 120;
                if (notches != 0) Zoom(notches);
                // Alt released alone would open the menu bar of the app in front.
                if (modifier is "Alt" or "Shift+Alt") PostThreadMessage(mouseThreadId, WmMask, 0, 0);
                return 1; // The app under the pointer does not scroll.
            }
        }
        else if (code >= 0 && wParam == WmMiddleDown)
        {
            string modifier, middle; bool enabled;
            lock (gate) { modifier = options.Modifier; middle = options.MiddleClick; enabled = options.Enabled; }
            bool match = enabled && middle switch { "Alone" => NoModifierHeld(), "Modifier" => ModifierHeld(modifier), _ => false };
            if (match)
            {
                middleSwallowed = true;
                Toggle(); // Back to 1x, or to the remembered zoom.
                if (middle == "Modifier" && modifier is "Alt" or "Shift+Alt") PostThreadMessage(mouseThreadId, WmMask, 0, 0);
                return 1;
            }
        }
        else if (code >= 0 && wParam == WmMiddleUp && middleSwallowed)
        {
            middleSwallowed = false;
            return 1;
        }
        return CallNextHookEx(0, code, wParam, lParam);
    }

    // A plain middle click: with Ctrl, Alt, Shift or Win it still reaches the app (for example Ctrl+click).
    private static bool NoModifierHeld()
    {
        static bool Down(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;
        return !Down(0x11) && !Down(0x12) && !Down(0x10) && !Down(0x5B) && !Down(0x5C);
    }

    private static bool ModifierHeld(string modifier)
    {
        static bool Down(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;
        bool ctrl = Down(0x11), alt = Down(0x12), shift = Down(0x10), win = Down(0x5B) || Down(0x5C);
        return !win && modifier switch
        {
            "Ctrl+Alt" => ctrl && alt && !shift,
            "Alt" => alt && !ctrl && !shift,
            "Shift+Alt" => shift && alt && !ctrl,
            "Ctrl+Shift" => ctrl && shift && !alt,
            _ => false
        };
    }

    // An unassigned virtual key (0xE8) marks Alt as used, so its release does not activate menus.
    private static void SendMaskKey()
    {
        KeyboardInput[] inputs = [new() { Type = 1, Vk = 0xE8 }, new() { Type = 1, Vk = 0xE8, Flags = 2 }];
        SendInput(2, inputs, Marshal.SizeOf<KeyboardInput>());
    }

    // ---- Caret thread ---------------------------------------------------------------------

    private void CaretLoop()
    {
        caretThreadId = GetCurrentThreadId();
        caretProc = CaretEvent;
        nint hook = SetWinEventHook(EventShow, EventLocationChange, 0, caretProc, 0, 0, 0); // Out of context.
        try { PumpMessages(onMessage: null); }
        finally { if (hook != 0) UnhookWinEvent(hook); }
    }

    private void CaretEvent(nint hook, uint eventType, nint window, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != CaretObject || eventType is not (EventShow or EventLocationChange) || window == 0) return;
        lock (gate) { if (!options.Enabled || !options.FollowCaret || targetScale <= 1) return; }
        if (IsShellWindow(window)) return; // Start, search and the touch keyboard never take over the view.
        if (CaretRectangle(window) is not { } rect) return;
        lock (gate)
        {
            if (caret == rect) return; // A blinking caret reappears in place; only movement counts.
            caret = rect; caretVersion++;
        }
        wake.Set();
    }

    private bool IsShellWindow(nint window)
    {
        GetWindowThreadProcessId(window, out uint process);
        if (shellByProcess.TryGetValue(process, out bool shell)) return shell;
        try { using var p = Process.GetProcessById((int)process); shell = ShellProcesses.Contains(p.ProcessName); }
        catch (ArgumentException) { shell = false; }
        if (shellByProcess.Count > 256) shellByProcess.Clear();
        shellByProcess[process] = shell;
        return shell;
    }

    /// <summary>Caret in physical screen pixels: the MSAA caret object first, then the Win32 caret.</summary>
    internal static MagRect? CaretRectangle(nint window)
    {
        var root = GetAncestor(window, 2);
        if (!GetWindowRect(root == 0 ? window : root, out var bounds)) return null;
        bool Inside(MagRect r) => r.X >= bounds.Left - 64 && r.Right <= bounds.Right + 64 && r.Y >= bounds.Top - 64 && r.Bottom <= bounds.Bottom + 64;
        MagRect? Accept(int left, int top, int right, int bottom)
        {
            var raw = new MagRect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
            if (Inside(raw)) return raw;
            // A DPI-unaware app reports logical pixels; convert them for this per-monitor aware process.
            Point a = new() { X = left, Y = top }, b = new() { X = right, Y = bottom };
            if (LogicalToPhysicalPointForPerMonitorDPI(window, ref a) && LogicalToPhysicalPointForPerMonitorDPI(window, ref b))
            {
                var converted = new MagRect(a.X, a.Y, Math.Max(1, b.X - a.X), Math.Max(1, b.Y - a.Y));
                if (Inside(converted)) return converted;
            }
            return null;
        }
        try
        {
            if (AccessibleObjectFromEvent(window, CaretObject, 0, out var accessible, out _) == 0 && accessible is not null)
            {
                try
                {
                    accessible.accLocation(out int left, out int top, out int width, out int height, 0);
                    if (width > 0 || height > 0)
                        if (Accept(left, top, left + width, top + height) is { } found) return found;
                }
                finally { Marshal.ReleaseComObject(accessible); }
            }
        }
        catch (COMException) { }
        catch (InvalidCastException) { }
        var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        if (!GetGUIThreadInfo(GetWindowThreadProcessId(window, out _), ref info) || info.Caret == 0) return null;
        Point topLeft = new() { X = info.CaretRect.Left, Y = info.CaretRect.Top }, bottomRight = new() { X = info.CaretRect.Right, Y = info.CaretRect.Bottom };
        if (!ClientToScreen(info.Caret, ref topLeft) || !ClientToScreen(info.Caret, ref bottomRight)) return null;
        return Accept(topLeft.X, topLeft.Y, bottomRight.X, bottomRight.Y);
    }

    private void PumpMessages(Action<uint>? onMessage)
    {
        while (!stopping && GetMessage(out var message, 0, 0, 0) > 0)
        {
            if (message.Window == 0) onMessage?.Invoke(message.Id);
            TranslateMessage(ref message); DispatchMessage(ref message);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);

    public void Dispose()
    {
        stopping = true;
        if (mouseThreadId != 0) PostThreadMessage(mouseThreadId, WmQuit, 0, 0);
        if (caretThreadId != 0) PostThreadMessage(caretThreadId, WmQuit, 0, 0);
        wake.Set();
        render?.Join(2000); mouse?.Join(2000); caretThread?.Join(2000);
        render = mouse = caretThread = null;
        mouseThreadId = caretThreadId = 0;
    }
}
