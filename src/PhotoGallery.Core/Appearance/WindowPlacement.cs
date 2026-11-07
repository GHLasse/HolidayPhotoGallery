namespace PhotoGallery.Core.Appearance;

/// <summary>
/// Where the main window was and how big, as Windows apps remember it (Explorer, Edge, Office, Photos): the
/// window's normal (not maximized) bounds plus whether it was maximized. Kept in the app settings, in physical
/// pixels, with the display scale they were measured at so a change of scaling keeps the same visible size.
/// </summary>
public sealed class WindowPlacement
{
    public int X { get; set; }

    public int Y { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    /// <summary>The display scale the bounds were measured at (1 = 100 %, 1.5 = 150 %).</summary>
    public double Scale { get; set; } = 1;

    public bool Maximized { get; set; }

    /// <summary>The size a first start opens at, in device-independent pixels, when the screen has room for it.</summary>
    public const int DefaultWidth = 1500;

    public const int DefaultHeight = 950;

    /// <summary>The smallest size a remembered window is given back, in device-independent pixels.</summary>
    public const int MinimumWidth = 640;

    public const int MinimumHeight = 480;

    /// <summary>
    /// The bounds to open the window at: the remembered ones where they still fit the screens, otherwise moved onto
    /// the nearest screen; a first start, or a screen that is gone, opens centred on the main screen.
    /// </summary>
    /// <param name="saved">The remembered placement, or null on a first start.</param>
    /// <param name="screens">The work areas of the screens (without the taskbar), the main screen first.</param>
    public static ScreenRect Fit(WindowPlacement? saved, IReadOnlyList<ScreenArea> screens)
    {
        if (screens.Count == 0)
            return saved is { Width: > 0, Height: > 0 } s
                ? new(s.X, s.Y, s.Width, s.Height)
                : new(0, 0, DefaultWidth, DefaultHeight);

        if (saved is not { Width: > 0, Height: > 0 } || !(saved.Scale > 0))
        {
            var main = screens[0];
            var width = (int)Math.Round(DefaultWidth * main.Scale);
            var height = (int)Math.Round(DefaultHeight * main.Scale);
            // On a small screen a first start leaves a margin around the window instead of filling it to the edges.
            width = Math.Min(width, (int)(main.Width * 0.9));
            height = Math.Min(height, (int)(main.Height * 0.9));
            return Centre(main, width, height);
        }

        var rect = new ScreenRect(saved.X, saved.Y, saved.Width, saved.Height);
        var screen = screens.MaxBy(s => Overlap(s, rect))!;
        var onScreen = Overlap(screen, rect) > 0;
        if (!onScreen)
            screen = screens[0];

        // The same visible size when the screen's scaling changed since (or the window opens on another screen).
        var factor = screen.Scale / saved.Scale;
        var w = (int)Math.Round(saved.Width * factor);
        var h = (int)Math.Round(saved.Height * factor);
        w = Math.Min(Math.Max(w, (int)Math.Round(MinimumWidth * screen.Scale)), screen.Width);
        h = Math.Min(Math.Max(h, (int)Math.Round(MinimumHeight * screen.Scale)), screen.Height);

        if (!onScreen)
            return Centre(screen, w, h);

        // Partly off the screen (a screen was rearranged or the taskbar moved): slid back so all of it shows.
        var x = Math.Clamp(saved.X, screen.X, screen.X + screen.Width - w);
        var y = Math.Clamp(saved.Y, screen.Y, screen.Y + screen.Height - h);
        return new(x, y, w, h);
    }

    private static ScreenRect Centre(ScreenArea screen, int width, int height) =>
        new(screen.X + (screen.Width - width) / 2, screen.Y + (screen.Height - height) / 2, width, height);

    private static long Overlap(ScreenArea s, ScreenRect r)
    {
        long w = Math.Min(s.X + s.Width, r.X + r.Width) - Math.Max(s.X, r.X);
        long h = Math.Min(s.Y + s.Height, r.Y + r.Height) - Math.Max(s.Y, r.Y);
        return w > 0 && h > 0 ? w * h : 0;
    }
}

/// <summary>A screen's work area in physical pixels, and its display scale (1 = 100 %).</summary>
public readonly record struct ScreenArea(int X, int Y, int Width, int Height, double Scale = 1);

/// <summary>Window bounds in physical pixels.</summary>
public readonly record struct ScreenRect(int X, int Y, int Width, int Height);
