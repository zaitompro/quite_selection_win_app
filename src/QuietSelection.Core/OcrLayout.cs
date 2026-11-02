namespace QuietSelection.Core;

/// <summary>A recognized line with its box in capture pixels.</summary>
public readonly record struct OcrLine(int Left, int Top, int Right, int Bottom, string Text)
{
    public int Height => Math.Max(1, Bottom - Top);
    public bool Overlaps(OcrLine other) => Left < other.Right && other.Left < Right;
}

public static class OcrLayout
{
    /// <summary>
    /// The paragraph under a point: the line containing it (or the vertically nearest line within one
    /// line height whose span covers the point), extended to neighbouring lines of the same column
    /// with line-sized gaps and similar height. Lines must be sorted by Top. Empty when nothing is there.
    /// </summary>
    public static string PickParagraph(IReadOnlyList<OcrLine> lines, int x, int y)
    {
        int best = -1; double bestDistance = double.MaxValue;
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            bool spans = x >= line.Left - line.Height && x <= line.Right + line.Height;
            if (!spans) continue;
            double distance = y < line.Top ? line.Top - y : y > line.Bottom ? y - line.Bottom : 0;
            if (distance <= line.Height && distance < bestDistance) { best = i; bestDistance = distance; }
        }
        if (best < 0) return "";
        int first = best, last = best;
        while (first > 0 && Joins(lines[first - 1], lines[first])) first--;
        while (last < lines.Count - 1 && Joins(lines[last], lines[last + 1])) last++;
        return string.Join("\n", lines.Skip(first).Take(last - first + 1).Select(l => l.Text));

        static bool Joins(OcrLine upper, OcrLine lower) =>
            upper.Overlaps(lower) && lower.Top - upper.Bottom < 0.8 * Math.Max(upper.Height, lower.Height)
            && Math.Abs(upper.Height - lower.Height) < 0.5 * Math.Max(upper.Height, lower.Height);
    }
}
