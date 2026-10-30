using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QuietSelection.App.Views;

/// <summary>
/// The app mark drawn in code: lines of text with one selected line, on the brand blue.
/// The active variant adds a sound dot so the tray shows that reading is in progress.
/// </summary>
internal static class AppIcon
{
    internal static readonly Color Brand = Color.FromRgb(0x1B, 0x4B, 0x7E);
    private static readonly Color Highlight = Color.FromRgb(0xFF, 0xC8, 0x3D);
    private static readonly Color Active = Color.FromRgb(0x4C, 0xC2, 0xFF);

    internal static BitmapSource Render(int size, bool active = false)
    {
        var visual = new DrawingVisual();
        using (var g = visual.RenderOpen())
        {
            double s = size;
            g.DrawRoundedRectangle(new SolidColorBrush(Brand), null, new Rect(0, 0, s, s), s * 0.22, s * 0.22);
            var white = new SolidColorBrush(Colors.White);
            double left = s * 0.22, line = Math.Max(1, Math.Round(s * 0.09)), radius = line / 2;
            g.DrawRoundedRectangle(white, null, new Rect(left, s * 0.25, s * 0.56, line), radius, radius);
            // The selected line: a highlight block with a dark line on top.
            g.DrawRoundedRectangle(new SolidColorBrush(Highlight), null, new Rect(left - s * 0.04, s * 0.42, s * 0.52, line * 2), radius, radius);
            g.DrawRoundedRectangle(new SolidColorBrush(Brand), null, new Rect(left, s * 0.42 + line / 2, s * 0.44, line), radius, radius);
            g.DrawRoundedRectangle(white, null, new Rect(left, s * 0.66, s * 0.40, line), radius, radius);
            if (active)
            {
                double d = s * 0.36;
                g.DrawEllipse(new SolidColorBrush(Active), new Pen(new SolidColorBrush(Brand), Math.Max(1, s * 0.06)),
                    new Point(s - d / 2 - s * 0.02, s - d / 2 - s * 0.02), d / 2, d / 2);
            }
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Title bar and taskbar icon (Windows scales it to the current DPI).</summary>
    internal static ImageSource Window { get; } = Render(48);

    /// <summary>A GDI icon for the notification area. The caller disposes it.</summary>
    internal static System.Drawing.Icon Tray(int size, bool active)
    {
        var source = Render(size, active);
        var pixels = new byte[size * size * 4];
        source.CopyPixels(pixels, size * 4, 0);
        using var bitmap = new System.Drawing.Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        var data = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, size, size), System.Drawing.Imaging.ImageLockMode.WriteOnly, bitmap.PixelFormat);
        try { Marshal.Copy(pixels, 0, data.Scan0, pixels.Length); }
        finally { bitmap.UnlockBits(data); }
        nint handle = bitmap.GetHicon();
        try { return (System.Drawing.Icon)System.Drawing.Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
    }

    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint handle);
}
