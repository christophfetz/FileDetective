namespace FileDetective.Core;

/// <summary>A rectangle in device pixels (left/top inclusive, right/bottom exclusive).</summary>
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;

    public int Height => Bottom - Top;
}

/// <summary>Computes where the search bar appears on a monitor (PLAN.md 8.4). Pure arithmetic, no WPF.</summary>
public static class BarPlacement
{
    /// <summary>
    /// Decision: the top edge of the bar lies at this fraction of the work area height, so the bar sits in the upper
    /// third and has room to grow downwards.
    /// </summary>
    public const double TopFraction = 0.18;

    /// <summary>
    /// Returns the top-left corner in device pixels: centered horizontally in <paramref name="workArea"/>, at
    /// <see cref="TopFraction"/> of its height. <paramref name="reservedHeight"/> is the tallest the window can become;
    /// the position is moved up so that this height still fits on the monitor. The result never leaves the work area
    /// on the left or top, even if the window is larger than the area.
    /// </summary>
    public static (int X, int Y) Compute(PixelRect workArea, int windowWidth, int reservedHeight)
    {
        int x = workArea.Left + (workArea.Width - windowWidth) / 2;
        int y = workArea.Top + (int)(workArea.Height * TopFraction);

        int maxY = workArea.Bottom - reservedHeight;
        if (y > maxY)
        {
            y = maxY;
        }

        return (Math.Max(x, workArea.Left), Math.Max(y, workArea.Top));
    }
}
