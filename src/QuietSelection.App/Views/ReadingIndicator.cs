using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace QuietSelection.App.Views;

/// <summary>
/// A small pill in the corner of the work area while text is translated or read.
/// It never takes focus: reading checks that the source window stays in the foreground.
/// </summary>
internal sealed class ReadingIndicator : Window
{
    private readonly TextBlock label = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new(10, 0, 12, 0) };

    internal ReadingIndicator(Action stop)
    {
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; AllowsTransparency = true;
        Background = Brushes.Transparent; ShowInTaskbar = false; ShowActivated = false; Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight; Focusable = false; FontSize = 14;
        Title = "QuietSelection — чтение";
        var dot = new System.Windows.Shapes.Ellipse { Width = 10, Height = 10, VerticalAlignment = VerticalAlignment.Center };
        dot.Fill = new SolidColorBrush(Color.FromRgb(0x4C, 0xC2, 0xFF));
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        var button = new Button { Content = "Стоп", Padding = new(12, 4, 12, 4), Focusable = false };
        AutomationProperties.SetName(button, "Остановить чтение");
        button.Click += (_, _) => stop();
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(dot); row.Children.Add(label); row.Children.Add(button);
        var card = new Border { Child = row, CornerRadius = new(20), Padding = new(14, 8, 8, 8), BorderThickness = new(1) };
        card.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorTertiaryBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "SurfaceStrokeColorDefaultBrush");
        card.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 2, Opacity = 0.25 };
        Content = new Border { Child = card, Padding = new(12) }; // Room for the shadow.
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            SetWindowLongPtr(handle, -20, GetWindowLongPtr(handle, -20) | 0x08000080); // NOACTIVATE | TOOLWINDOW
            HwndSource.FromHwnd(handle).AddHook((nint _, int msg, nint _, nint _, ref bool handled) =>
            { if (msg == 0x0021) { handled = true; return 3; } return 0; }); // WM_MOUSEACTIVATE → MA_NOACTIVATE
        };
        SizeChanged += (_, _) => Place();
    }

    internal void ShowState(string text)
    {
        label.Text = text;
        if (!IsVisible) Show();
        Place();
    }

    // Bottom-right corner of the monitor with the pointer, in physical pixels.
    private void Place()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == 0 || ActualWidth == 0) return;
        var area = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position).WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(this);
        int width = (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX), height = (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY);
        SetWindowPos(handle, -1, area.Right - width - 12, area.Bottom - height - 12, 0, 0, 0x0001 | 0x0010); // NOSIZE | NOACTIVATE
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
}
