using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO.Pipes;
using QuietSelection.App.Workers;
using static QuietSelection.App.WindowTheme.WindowThemeNative;

namespace QuietSelection.App.WindowTheme;

/// <summary>
/// Helper process (<c>--window-theme-worker</c>) that keeps dark overlays above chosen windows.
/// Each overlay is a click-through layered window holding a Magnification control that shows the
/// same screen area 1:1 through the hue-preserving inversion matrix. Overlays are excluded from
/// their own source, sit directly above their window in z-order and follow it. When this process
/// ends, every overlay disappears with it. Only window bounds, classes and process names are read;
/// the lightness check samples pixels in memory and discards them.
/// </summary>
internal sealed partial class WindowThemeHost
{
    private const uint WmApp = 0x8001, WmTimer = 0x0113, WmNcHitTest = 0x0084;
    private const uint EventForeground = 0x0003, EventDestroy = 0x8001, EventShow = 0x8002, EventLocationChange = 0x800B;
    private const uint SwpNoSize = 0x1, SwpNoMove = 0x2, SwpNoZOrder = 0x4, SwpNoActivate = 0x10, SwpShowWindow = 0x40;
    private const string HostClass = "QuietSelection.DarkOverlay";

    private sealed class Overlay
    {
        internal nint Host, Magnifier;
        internal Rect Bounds;
        internal bool Visible, Automatic;
    }

    private readonly ConcurrentQueue<ThemeRequest> requests = new();
    private readonly Dictionary<nint, Overlay> overlays = [];
    // Newly shown windows waiting for the lightness check: first seen, next check.
    private readonly Dictionary<nint, (long Shown, long Next)> pending = [];
    private readonly Dictionary<uint, string> processNames = [];
    private readonly Stream pipe;
    private bool properties;
    private HashSet<string> rules = [];
    private uint thread;
    private nuint timer;
    private nint instance;
    // Kept alive for native callbacks.
    private WindowProc? hostProc;
    private WinEventProc? eventProc;

    private WindowThemeHost(Stream pipe) => this.pipe = pipe;

    internal static int Run(string name)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            pipe.Connect(5_000);
            var host = new WindowThemeHost(pipe);
            var ui = new Thread(host.Loop) { Name = "window-theme-ui" };
            ui.SetApartmentState(ApartmentState.STA);
            ui.Start();
            while (host.thread == 0) Thread.Sleep(5);
            try
            {
                while (true)
                {
                    host.requests.Enqueue(WorkerProtocol.ReadAsync<ThemeRequest>(pipe, CancellationToken.None).GetAwaiter().GetResult());
                    PostThreadMessage(host.thread, WmApp, 0, 0);
                }
            }
            catch (EndOfStreamException) { } // The app closed the pipe.
            PostThreadMessage(host.thread, 0x0012, 0, 0); // WM_QUIT
            ui.Join(3000);
            return 0;
        }
        catch { return 1; }
    }

    private void Loop()
    {
        thread = GetCurrentThreadId();
        if (!MagInitialize()) return;
        instance = GetModuleHandle(null);
        hostProc = HostWindowProc;
        var windowClass = new WindowClass { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<WindowClass>(), Proc = hostProc, Instance = instance, ClassName = HostClass };
        RegisterClassEx(ref windowClass);
        eventProc = OnWinEvent;
        nint hook = SetWinEventHook(EventForeground, EventLocationChange, 0, eventProc, 0, 0, 0);
        try
        {
            while (GetMessage(out var message, 0, 0, 0) > 0)
            {
                if (message.Window == 0 && message.Id == WmApp) HandleRequests();
                else if (message.Window == 0 && message.Id == WmTimer) Tick();
                TranslateMessage(ref message); DispatchMessage(ref message);
            }
        }
        finally
        {
            foreach (var target in overlays.Keys.ToArray()) Remove(target);
            if (hook != 0) UnhookWinEvent(hook);
            MagUninitialize();
        }
    }

    private static nint HostWindowProc(nint window, uint message, nint w, nint l) =>
        message == WmNcHitTest ? -1 : DefWindowProc(window, message, w, l); // HTTRANSPARENT: clicks reach the window below.

    // ---- Requests -------------------------------------------------------------------------

    private void HandleRequests()
    {
        while (requests.TryDequeue(out var request))
        {
            ThemeResponse response;
            switch (request.Kind)
            {
                case "theme-configure":
                    properties = request.Properties;
                    rules = (request.Rules ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    response = new(true, Overlays: overlays.Count);
                    break;
                case "theme-toggle":
                    var target = GetAncestor((nint)request.Window, 2);
                    if (target == 0 || !IsWindow(target) || Own(target)) { response = new(false, Failure: "NO_WINDOW"); break; }
                    string rule = RuleOf(target);
                    if (overlays.ContainsKey(target)) { Remove(target); response = new(true, false, rule, overlays.Count); }
                    else response = Add(target, automatic: false) ? new(true, true, rule, overlays.Count) : new(false, Rule: rule, Failure: "OVERLAY_FAILED");
                    break;
                case "theme-status":
                    response = new(true, Overlays: overlays.Count);
                    break;
                default:
                    response = new(false, Failure: "UNKNOWN_REQUEST");
                    break;
            }
            WorkerProtocol.WriteAsync(pipe, response, CancellationToken.None).GetAwaiter().GetResult();
        }
    }

    // ---- Overlays -------------------------------------------------------------------------

    private bool Add(nint target, bool automatic)
    {
        if (!VisibleBounds(target, out var bounds) || bounds.Width <= 0 || bounds.Height <= 0) return false;
        // Layered, click-through, no taskbar button, never activated.
        nint host = CreateWindowEx(0x80000 | 0x20 | 0x80 | 0x08000000, HostClass, null, 0x80000000,
            bounds.Left, bounds.Top, bounds.Width, bounds.Height, 0, 0, instance, 0);
        if (host == 0) return false;
        SetLayeredWindowAttributes(host, 0, 255, 2);
        nint magnifier = CreateWindowEx(0, "Magnifier", null, 0x40000000 | 0x10000000, 0, 0, bounds.Width, bounds.Height, host, 0, instance, 0);
        if (magnifier == 0) { DestroyWindow(host); return false; }
        var identity = new Transform { Matrix = [1, 0, 0, 0, 1, 0, 0, 0, 1] };
        MagSetWindowTransform(magnifier, ref identity);
        var effect = new ColorEffect { Matrix = MagnifierMath.InvertMatrix(smart: true) };
        MagSetColorEffect(magnifier, ref effect);
        overlays[target] = new Overlay { Host = host, Magnifier = magnifier, Automatic = automatic };
        UpdateFilters();
        Place(target, overlays[target], force: true);
        EnsureTimer();
        return true;
    }

    private void Remove(nint target)
    {
        if (!overlays.Remove(target, out var overlay)) return;
        DestroyWindow(overlay.Host);
        UpdateFilters();
        EnsureTimer();
    }

    // No overlay may appear in any overlay's source, or it would invert itself again.
    private void UpdateFilters()
    {
        var hosts = overlays.Values.Select(x => x.Host).ToArray();
        foreach (var overlay in overlays.Values) MagSetWindowFilterList(overlay.Magnifier, 0, hosts.Length, hosts);
    }

    private void Place(nint target, Overlay overlay, bool force = false)
    {
        if (!IsWindow(target)) { Remove(target); return; }
        bool measured = VisibleBounds(target, out var bounds);
        bool hidden = !measured || bounds.Width <= 0 || !IsWindowVisible(target) || IsIconic(target) || Cloaked(target);
        if (hidden)
        {
            if (overlay.Visible) { ShowWindow(overlay.Host, 0); overlay.Visible = false; }
            return;
        }
        // Directly above the window: windows opened on top of it still cover the overlay.
        nint above = GetWindow(target, 3); // GW_HWNDPREV
        bool targetTopmost = (GetWindowLongPtr(target, -20) & 0x8) != 0;
        uint flags = SwpNoActivate | SwpShowWindow;
        nint after;
        if (above == overlay.Host) { after = 0; flags |= SwpNoZOrder; }
        else if (above == 0 || (!targetTopmost && (GetWindowLongPtr(above, -20) & 0x8) != 0)) after = targetTopmost ? -1 : 0;
        else after = above;
        bool moved = force || !bounds.Equals(overlay.Bounds);
        if (!moved) flags |= SwpNoMove | SwpNoSize;
        SetWindowPos(overlay.Host, after, bounds.Left, bounds.Top, bounds.Width, bounds.Height, flags);
        if (moved) SetWindowPos(overlay.Magnifier, 0, 0, 0, bounds.Width, bounds.Height, SwpNoZOrder | SwpNoActivate);
        overlay.Bounds = bounds; overlay.Visible = true;
        MagSetWindowSource(overlay.Magnifier, bounds);
        InvalidateRect(overlay.Magnifier, 0, true);
    }

    // About 30 refreshes per second while overlays exist; no timer otherwise.
    private void EnsureTimer()
    {
        bool needed = overlays.Count > 0 || pending.Count > 0;
        if (needed && timer == 0) timer = SetTimer(0, 0, 33, 0);
        else if (!needed && timer != 0) { KillTimer(0, timer); timer = 0; }
    }

    private void Tick()
    {
        foreach (var (target, overlay) in overlays.ToArray()) Place(target, overlay);
        long now = Environment.TickCount64;
        foreach (var (window, (shown, next)) in pending.ToArray())
        {
            if (now < next) continue;
            // Some windows paint seconds after appearing: check again until 2 s have passed.
            if (!overlays.ContainsKey(window) && IsWindow(window) && IsWindowVisible(window) && IsLight(window)) { pending.Remove(window); Add(window, automatic: true); }
            else if (now - shown >= 2000 || !IsWindow(window)) pending.Remove(window);
            else pending[window] = (shown, now + 400);
        }
        EnsureTimer();
    }

    // ---- Automatic rules -------------------------------------------------------------------

    private void OnWinEvent(nint hook, uint eventType, nint window, int idObject, int idChild, uint threadId, uint time)
    {
        if (idObject != 0 || idChild != 0 || window == 0) return; // Whole windows only.
        if (eventType == EventLocationChange) { if (overlays.TryGetValue(window, out var moved)) Place(window, moved); return; }
        if (eventType == EventDestroy) { if (overlays.ContainsKey(window)) Remove(window); pending.Remove(window); return; }
        if (eventType == EventForeground) { foreach (var (target, overlay) in overlays.ToArray()) Place(target, overlay); return; }
        if (eventType != EventShow || (!properties && rules.Count == 0)) return;
        if (GetAncestor(window, 2) != window || Own(window) || overlays.ContainsKey(window)) return;
        if ((properties && IsPropertySheet(window)) || rules.Contains(RuleOf(window)))
        {
            long now = Environment.TickCount64;
            pending[window] = (now, now + 300); // Let it finish painting first.
            EnsureTimer();
        }
    }

    // Classic property sheets (file, folder, drive, Control Panel): a dialog with a tab control.
    private static bool IsPropertySheet(nint window) => ClassName(window) == "#32770" && FindWindowEx(window, 0, "SysTabControl32", null) != 0;

    private static bool Own(nint window) { GetWindowThreadProcessId(window, out uint process); return process == Environment.ProcessId; }

    private string RuleOf(nint window)
    {
        GetWindowThreadProcessId(window, out uint process);
        if (!processNames.TryGetValue(process, out var name))
        {
            name = ProcessName(process);
            if (processNames.Count > 256) processNames.Clear();
            processNames[process] = name;
        }
        return WindowThemeSettings.Rule(name, ClassKey(ClassName(window)));
    }

    internal static string RuleFor(nint window)
    {
        GetWindowThreadProcessId(window, out uint process);
        return WindowThemeSettings.Rule(ProcessName(process), ClassKey(ClassName(window)));
    }

    private static string ProcessName(uint process)
    {
        try { using var p = Process.GetProcessById((int)process); return p.ProcessName; }
        catch (ArgumentException) { return "unknown"; }
    }

    // WPF and some frameworks put a per-run GUID in the window class; a remembered type ignores it.
    [System.Text.RegularExpressions.GeneratedRegex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial System.Text.RegularExpressions.Regex Guid();
    internal static string ClassKey(string windowClass) => Guid().Replace(windowClass, "*");

    /// <summary>
    /// Average brightness of the window's central area. The window renders itself into memory
    /// (PrintWindow), so a magnified screen or windows on top do not matter; pixels are discarded.
    /// </summary>
    internal static bool IsLight(nint window)
    {
        if (!GetWindowRect(window, out var bounds) || bounds.Width < 8 || bounds.Height < 8) return false;
        using var full = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(full))
        {
            nint dc = graphics.GetHdc();
            bool printed;
            try { printed = PrintWindow(window, dc, 2); } // PW_RENDERFULLCONTENT
            finally { graphics.ReleaseHdc(dc); }
            if (!printed) return false;
        }
        int width = Math.Min(bounds.Width, 480), height = Math.Min(bounds.Height, 480);
        using var bitmap = full.Clone(new Rectangle((bounds.Width - width) / 2, (bounds.Height - height) / 2, width, height), PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            double sum = 0; int count = 0;
            unsafe
            {
                for (int y = 0; y < height; y += 4)
                {
                    byte* row = (byte*)data.Scan0 + y * data.Stride;
                    for (int x = 0; x < width; x += 4, count++)
                        sum += (row[x * 4 + 2] * 0.2126 + row[x * 4 + 1] * 0.7152 + row[x * 4] * 0.0722) / 255;
                }
            }
            return count > 0 && sum / count >= 0.6;
        }
        finally { bitmap.UnlockBits(data); }
    }
}
