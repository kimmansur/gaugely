namespace RateTray.Ui;

/// <summary>
/// Works out where the details fly-out goes: in the taskbar corner the tray icons live in,
/// and always fully inside the visible work area.
///
/// Kept free of WinForms types so it can be unit tested for every taskbar edge and screen
/// size without a desktop.
/// </summary>
public static class FlyoutPlacement
{
    public enum Edge { Bottom, Top, Left, Right }

    /// <summary>Derives which screen edge the taskbar is docked to from its rectangle.</summary>
    public static Edge EdgeOf(Rectangle taskbar, Rectangle screen)
    {
        if (taskbar.Width >= taskbar.Height)
            return taskbar.Top - screen.Top >= screen.Bottom - taskbar.Bottom ? Edge.Bottom : Edge.Top;

        return taskbar.Left - screen.Left >= screen.Right - taskbar.Right ? Edge.Right : Edge.Left;
    }

    /// <summary>
    /// Splits service blocks, in order, into the fewest columns whose height fits
    /// <paramref name="maxHeight"/>, balanced so the window is no taller than it has to be. When
    /// even <paramref name="maxColumns"/> are not enough it uses them all, balanced the same way.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<int>> Columns(IReadOnlyList<int> heights, int maxHeight, int maxColumns)
    {
        if (heights.Count == 0) return [];

        var columns = Math.Min(Math.Max(1, maxColumns), Fill(heights, maxHeight).Count);

        var cap = Math.Max(heights.Max(), heights.Sum() / columns);
        while (Fill(heights, cap).Count > columns) cap++;
        return Fill(heights, cap);
    }

    /// <summary>How far the content can scroll; zero when the window shows all of it.</summary>
    public static int MaxScroll(int contentHeight, int windowHeight) => Math.Max(0, contentHeight - windowHeight);

    private static List<IReadOnlyList<int>> Fill(IReadOnlyList<int> heights, int cap)
    {
        var columns = new List<IReadOnlyList<int>>();
        var current = new List<int>();
        var used = 0;
        for (var index = 0; index < heights.Count; index++)
        {
            if (current.Count > 0 && used + heights[index] > cap)
            {
                columns.Add(current);
                current = [];
                used = 0;
            }

            current.Add(index);
            used += heights[index];
        }

        columns.Add(current);
        return columns;
    }

    /// <summary>
    /// Top-left corner for a window of <paramref name="size"/>. The result is always inside
    /// <paramref name="workArea"/>: a window larger than the screen is aligned to the top-left
    /// corner rather than pushed off-screen.
    /// </summary>
    public static Point Locate(Size size, Rectangle workArea, Edge edge, int margin)
    {
        // The tray sits at the far end of the taskbar, so anchor to that corner.
        var x = edge == Edge.Left
            ? workArea.Left + margin
            : workArea.Right - size.Width - margin;

        var y = edge == Edge.Top
            ? workArea.Top + margin
            : workArea.Bottom - size.Height - margin;

        return new Point(
            Clamp(x, workArea.Left, workArea.Right - size.Width),
            Clamp(y, workArea.Top, workArea.Bottom - size.Height));
    }

    /// <summary>Caps the window so it never exceeds the work area it has to fit into.</summary>
    public static Size Fit(Size desired, Rectangle workArea, int margin) => new(
        Math.Min(desired.Width, Math.Max(1, workArea.Width - 2 * margin)),
        Math.Min(desired.Height, Math.Max(1, workArea.Height - 2 * margin)));

    private static int Clamp(int value, int min, int max) =>
        max < min ? min : Math.Clamp(value, min, max);
}
