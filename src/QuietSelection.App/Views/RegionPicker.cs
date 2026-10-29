using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using QuietSelection.App.Platform;

namespace QuietSelection.App.Views;

internal sealed class RegionPicker : Window
{
    private readonly TaskCompletionSource<PixelRegion> completion = new();
    private readonly nint sourceWindow;
    private readonly System.Drawing.Rectangle bounds;
    private readonly Canvas canvas = new() { Focusable = false };
    private readonly Rectangle rectangle = new() { Stroke = Brushes.Yellow, StrokeThickness = 3, Fill = Brushes.Transparent, Focusable = false };
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(25) };
    private NativePoint? start;
    private CancellationTokenRegistration cancellationRegistration;

    private RegionPicker(nint source, CancellationToken token)
    {
        sourceWindow = source;
        GetCursorPos(out var cursor);
        bounds = System.Windows.Forms.Screen.FromPoint(new(cursor.X, cursor.Y)).Bounds;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = new SolidColorBrush(Color.FromArgb(30, 0, 0, 0));
        ShowInTaskbar = false; ShowActivated = false; Topmost = true; Focusable = false;
        Content = canvas;
        canvas.Children.Add(rectangle);
        var hint = new TextBlock { Text = "Обведите область на этом мониторе. Esc или правая кнопка — отмена.",
            FontSize = 22, Background = Brushes.Black, Foreground = Brushes.White, Padding = new(12), IsHitTestVisible = false };
        canvas.Children.Add(hint); Canvas.SetLeft(hint, 20); Canvas.SetTop(hint, 20);
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            SetWindowLongPtr(handle, -20, GetWindowLongPtr(handle, -20) | (nint)0x08000080);
            HwndSource.FromHwnd(handle).AddHook((nint hwnd, int msg, nint wp, nint lp, ref bool handled) =>
            { if (msg == 0x0021) { handled = true; return 3; } return 0; });
            SetWindowPos(handle, new nint(-1), bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x0010);
        };
        Loaded += (_, _) =>
        {
            SetWindowPos(new WindowInteropHelper(this).Handle, new nint(-1), bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x0010);
            timer.Start();
        };
        MouseLeftButtonDown += (_, e) => { GetCursorPos(out var point); start = point; CaptureMouse(); e.Handled = true; };
        MouseMove += (_, _) => Draw();
        MouseLeftButtonUp += (_, e) =>
        {
            if (start is not { } first) return;
            GetCursorPos(out var last);
            var region = PixelRegion.FromDrag(first.X, first.Y, Math.Clamp(last.X, bounds.Left, bounds.Right), Math.Clamp(last.Y, bounds.Top, bounds.Bottom));
            completion.TrySetResult(region); Close(); e.Handled = true;
        };
        MouseRightButtonDown += (_, _) => Cancel();
        timer.Tick += (_, _) =>
        {
            // Only Escape is observed while the user explicitly selects a region.
            if ((GetAsyncKeyState(0x1B) & 0x8000) != 0 || NativeMethods.GetForegroundWindow() != sourceWindow) Cancel();
        };
        Closed += (_, _) => { timer.Stop(); ReleaseMouseCapture(); cancellationRegistration.Dispose(); completion.TrySetCanceled(); };
        cancellationRegistration = token.Register(() => Dispatcher.BeginInvoke(Cancel));
    }

    internal static async Task<PixelRegion> SelectAsync(nint source, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var picker = new RegionPicker(source, token); picker.Show();
        var result = await picker.completion.Task;
        // Wait for removal of the overlay before capturing the selected pixels.
        await Task.Yield(); DwmFlush();
        token.ThrowIfCancellationRequested();
        if (NativeMethods.GetForegroundWindow() != source) throw new QuietException(ErrorCode.SourceChanged);
        return result;
    }
    private void Cancel() { completion.TrySetCanceled(); Close(); }
    private void Draw()
    {
        if (start is not { } first) return;
        GetCursorPos(out var last);
        var a = PointFromScreen(new(first.X, first.Y));
        var b = PointFromScreen(new(Math.Clamp(last.X, bounds.Left, bounds.Right), Math.Clamp(last.Y, bounds.Top, bounds.Bottom)));
        Canvas.SetLeft(rectangle, Math.Min(a.X, b.X)); Canvas.SetTop(rectangle, Math.Min(a.Y, b.Y));
        rectangle.Width = Math.Abs(a.X - b.X); rectangle.Height = Math.Abs(a.Y - b.Y);
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X; public int Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
}
