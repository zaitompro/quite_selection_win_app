namespace QuietSelection.Core;

/// <summary>Full-screen magnifier options. Off by default; the wheel is captured only when enabled.</summary>
public sealed class MagnifierSettings
{
    public static readonly IReadOnlyList<string> Modifiers = ["Ctrl+Alt", "Alt", "Shift+Alt", "Ctrl+Shift"];
    /// <summary>Middle button toggles zoom: off, with the zoom modifier held, or a plain middle click.</summary>
    public static readonly IReadOnlyList<string> MiddleClickModes = ["Off", "Modifier", "Alone"];
    /// <summary>Classic negative, or brightness inverted with hues kept (light UI becomes dark, red stays red).</summary>
    public static readonly IReadOnlyList<string> InvertStyles = ["Classic", "Smart"];
    public bool Enabled { get; set; }
    /// <summary>Keys held while turning the wheel to zoom.</summary>
    public string Modifier { get; set; } = "Ctrl+Alt";
    /// <summary>Zoom factor per wheel notch.</summary>
    public double Step { get; set; } = 1.25;
    /// <summary>Zoom restored by the toggle shortcut.</summary>
    public double LastScale { get; set; } = 2.0;
    public bool Invert { get; set; }
    public bool FollowCaret { get; set; } = true;
    public bool HideCursorWhileTyping { get; set; } = true;
    public string MiddleClick { get; set; } = "Off";
    public string InvertStyle { get; set; } = "Classic";
    /// <summary>Choices offered for the zoom animation, in milliseconds; 0 turns it off.</summary>
    public static readonly IReadOnlyList<int> AnimationChoices = [0, 250, 400, 600, 800];
    /// <summary>Duration of turning zoom on or off; wheel steps take half of it.</summary>
    public int AnimationMs { get; set; } = 400;
}

public readonly record struct MagPoint(double X, double Y);
public readonly record struct MagRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
}

/// <summary>
/// Geometry of full-screen magnification in physical desktop pixels. The offset is the top-left
/// desktop point shown at the top-left of the screen; the visible area is screen / scale.
/// Windows maps the cursor through the same transform, so a click lands under the drawn cursor.
/// </summary>
public static class MagnifierMath
{
    /// <summary>
    /// 5x5 color matrices for the Magnification API (row vector [r g b a 1] x M).
    /// Smart: invert, then rotate hue by 180 degrees (luminance weights 0.213/0.715/0.072), so white
    /// becomes black while colored items keep their hue.
    /// </summary>
    public static float[] InvertMatrix(bool smart) => smart
        ? [0.574f, -0.426f, -0.426f, 0, 0,
           -1.430f, -0.430f, -1.430f, 0, 0,
           -0.144f, -0.144f, 0.856f, 0, 0,
           0, 0, 0, 1, 0,
           1, 1, 1, 0, 1]
        : [-1, 0, 0, 0, 0, 0, -1, 0, 0, 0, 0, 0, -1, 0, 0, 0, 0, 0, 1, 0, 1, 1, 1, 0, 1];

    /// <summary>Applies a matrix to one color (0..1 channels, clamped), for tests and previews.</summary>
    public static (double R, double G, double B) ApplyMatrix(float[] m, double r, double g, double b)
    {
        double Channel(int i) => Math.Clamp(r * m[i] + g * m[5 + i] + b * m[10 + i] + m[20 + i], 0, 1);
        return (Channel(0), Channel(1), Channel(2));
    }

    // Windows Magnifier stops at 16x (1600 %); the API itself accepts at least 64x.
    public const double MaximumScale = 64.0;

    public static double NextScale(double current, int notches, double step)
    {
        double next = current * Math.Pow(step, notches);
        // Snap to exactly 1 near the bottom so magnification turns off cleanly.
        return next < 1.03 ? 1.0 : Math.Min(MaximumScale, Math.Round(next, 3));
    }

    /// <summary>Offset that puts <paramref name="point"/> in the middle of the screen, clamped at the edges.</summary>
    /// <summary>Smooth start and end (smoothstep), for animation progress 0..1.</summary>
    public static double Ease(double progress)
    {
        progress = Math.Clamp(progress, 0, 1);
        return progress * progress * (3 - 2 * progress);
    }

    /// <summary>
    /// Zoom during an animation from one level to another. Interpolated on a logarithmic scale, so
    /// 1→2× and 4→8× feel equally fast.
    /// </summary>
    public static double AnimatedScale(double from, double to, double progress)
    {
        from = Math.Max(1, from); to = Math.Max(1, to);
        return Math.Exp(Math.Log(from) + (Math.Log(to) - Math.Log(from)) * Ease(progress));
    }

    /// <summary>
    /// View offset while zooming from or to 1×: the pointer starts where it really is on screen and
    /// glides to the centre as the zoom grows, so the area being enlarged stays visible. Between two
    /// zoomed levels (and once zoomed) it is the usual pointer-centred view.
    /// </summary>
    public static MagPoint ZoomFromPointer(MagPoint pointer, MagPoint screen, double scale, double from, double to)
    {
        scale = Math.Max(1, scale);
        double top = Math.Max(from, to);
        if (Math.Min(from, to) > 1 || top <= 1) return Center(pointer, screen, scale);
        double toward = Math.Clamp(Math.Log(scale) / Math.Log(top), 0, 1); // 0 at 1×, 1 at the zoomed end.
        double shownX = pointer.X + (screen.X / 2 - pointer.X) * toward;
        double shownY = pointer.Y + (screen.Y / 2 - pointer.Y) * toward;
        return Clamp(new(pointer.X - shownX / scale, pointer.Y - shownY / scale), screen, scale);
    }

    public static MagPoint Center(MagPoint point, MagPoint screen, double scale)
    {
        scale = Math.Max(1, scale); // An unreadable transform reports 0.
        double width = screen.X / scale, height = screen.Y / scale;
        return Clamp(new(point.X - width / 2, point.Y - height / 2), screen, scale);
    }

    /// <summary>
    /// Moves the view only when the caret leaves the central area (half of the visible width and
    /// height), and then just enough to bring it back. Typing does not shift the screen every letter.
    /// </summary>
    public static MagPoint FollowCaret(MagPoint offset, MagRect caret, MagPoint screen, double scale)
    {
        scale = Math.Max(1, scale);
        double width = screen.X / scale, height = screen.Y / scale;
        double left = offset.X + width / 4, right = offset.X + width * 3 / 4;
        double top = offset.Y + height / 4, bottom = offset.Y + height * 3 / 4;
        double x = offset.X, y = offset.Y;
        // A caret far outside (another window, a jump) is centered instead of nudged.
        if (caret.Right < offset.X || caret.X > offset.X + width || caret.Bottom < offset.Y || caret.Y > offset.Y + height)
            return Center(new(caret.X + caret.Width / 2, caret.Y + caret.Height / 2), screen, scale);
        if (caret.X < left) x -= left - caret.X; else if (caret.Right > right) x += caret.Right - right;
        if (caret.Y < top) y -= top - caret.Y; else if (caret.Bottom > bottom) y += caret.Bottom - bottom;
        return Clamp(new(x, y), screen, scale);
    }

    /// <summary>Where a desktop point appears on the magnified screen.</summary>
    public static MagPoint ToScreen(MagPoint point, MagPoint offset, double scale) =>
        new((point.X - offset.X) * scale, (point.Y - offset.Y) * scale);

    private static MagPoint Clamp(MagPoint offset, MagPoint screen, double scale) =>
        new(Math.Clamp(offset.X, 0, screen.X - screen.X / scale), Math.Clamp(offset.Y, 0, screen.Y - screen.Y / scale));
}
