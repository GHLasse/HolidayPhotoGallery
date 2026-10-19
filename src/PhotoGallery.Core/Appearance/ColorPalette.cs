using System.Globalization;

namespace PhotoGallery.Core.Appearance;

/// <summary>A colour as red, green and blue from 0 to 255.</summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    public static readonly Rgb White = new(255, 255, 255);
    public static readonly Rgb Black = new(0, 0, 0);

    public static Rgb Parse(string hex)
    {
        hex = hex.TrimStart('#');
        return new Rgb(Convert.ToByte(hex[..2], 16), Convert.ToByte(hex[2..4], 16), Convert.ToByte(hex[4..6], 16));
    }

    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";

    /// <summary>Relative luminance as WCAG defines it, from 0 (black) to 1 (white).</summary>
    public double Luminance
    {
        get
        {
            static double Channel(byte c)
            {
                var v = c / 255.0;
                return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Channel(R) + 0.7152 * Channel(G) + 0.0722 * Channel(B);
        }
    }

    /// <summary>WCAG contrast ratio between two colours, from 1 to 21.</summary>
    public static double Contrast(Rgb a, Rgb b)
    {
        var (light, dark) = a.Luminance > b.Luminance ? (a.Luminance, b.Luminance) : (b.Luminance, a.Luminance);
        return (light + 0.05) / (dark + 0.05);
    }

    /// <summary>The same hue with its lightness (HSL) moved by <paramref name="delta"/>, from -1 to 1.</summary>
    public Rgb Lighten(double delta)
    {
        var (h, s, l) = ToHsl();
        return FromHsl(h, s, Math.Clamp(l + delta, 0, 1));
    }

    private (double H, double S, double L) ToHsl()
    {
        double r = R / 255.0, g = G / 255.0, b = B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2;
        if (max == min)
            return (0, 0, l);
        var d = max - min;
        var s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        var h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        return (h / 6, s, l);
    }

    private static Rgb FromHsl(double h, double s, double l)
    {
        if (s == 0)
            return new Rgb(ToByte(l), ToByte(l), ToByte(l));
        var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        var p = 2 * l - q;
        return new Rgb(ToByte(Hue(p, q, h + 1.0 / 3)), ToByte(Hue(p, q, h)), ToByte(Hue(p, q, h - 1.0 / 3)));

        static double Hue(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            return t < 1.0 / 6 ? p + (q - p) * 6 * t : t < 0.5 ? q : t < 2.0 / 3 ? p + (q - p) * (2.0 / 3 - t) * 6 : p;
        }
    }

    private static byte ToByte(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255, MidpointRounding.AwayFromZero);
}

/// <summary>
/// A colour scheme for the app: the Windows accent colour, or a palette built from one colour (the Pantone
/// Colors of the Year that Lasse picked). Each palette gives matching shades for light and dark mode.
/// </summary>
public sealed record ColorPalette(string Key, string Name, Rgb? Color, bool IsBackground = false)
{
    public const string WindowsKey = "windows";

    // Window backgrounds the shades are checked against: Windows 11's light and dark base colours.
    public static readonly Rgb LightBackground = Rgb.Parse("#F3F3F3");
    public static readonly Rgb DarkBackground = Rgb.Parse("#202020");

    public static IReadOnlyList<ColorPalette> All { get; } =
    [
        new(WindowsKey, "Windows accent colour", null),
        new("mocha", "Mocha Mousse", Rgb.Parse("#A47864")),
        new("cloud", "Cloud Dancer", Rgb.Parse("#F0EEE9"), IsBackground: true),
        new("classic-blue", "Classic Blue", Rgb.Parse("#0F4C81")),
        new("viva-magenta", "Viva Magenta", Rgb.Parse("#BB2649")),
        new("very-peri", "Very Peri", Rgb.Parse("#6667AB")),
        new("illuminating", "Illuminating", Rgb.Parse("#F5DF4D")),
        new("greenery", "Greenery", Rgb.Parse("#88B04B")),
    ];

    public static ColorPalette Find(string? key) =>
        All.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)) ?? All[0];

    /// <summary>The shades for light or dark mode; null for the Windows accent colour, which Windows provides.</summary>
    public PaletteShades? ShadesFor(bool dark)
    {
        if (Color is not { } color)
            return null;
        var background = dark ? DarkBackground : LightBackground;

        // Cloud Dancer is almost white: it tints the window, and a warm grey from the same hue is the accent.
        var accentBase = IsBackground ? color.Lighten(-0.45) : color;
        var windowTint = IsBackground ? (dark ? color.Lighten(-0.78) : color) : (Rgb?)null;
        if (windowTint is { } tint)
            background = tint;

        // Fills (selection, buttons) need 3:1 against the window, text 4.5:1 (WCAG AA).
        var fill = Readable(dark ? accentBase.Lighten(0.1) : accentBase.Lighten(-0.05), background, 3, dark);
        var text = Readable(fill, background, 4.5, dark);
        var onFill = Rgb.Contrast(fill, Rgb.Black) >= Rgb.Contrast(fill, Rgb.White) ? Rgb.Black : Rgb.White;
        return new PaletteShades(
            Accent: accentBase,
            Light1: accentBase.Lighten(0.1), Light2: accentBase.Lighten(0.2), Light3: accentBase.Lighten(0.3),
            Dark1: accentBase.Lighten(-0.1), Dark2: accentBase.Lighten(-0.2), Dark3: accentBase.Lighten(-0.3),
            Fill: fill, Text: text, TextOnFill: onFill, WindowBackground: windowTint);
    }

    /// <summary>The colour made lighter (on dark) or darker (on light) until it stands out enough.</summary>
    private static Rgb Readable(Rgb color, Rgb background, double contrast, bool dark)
    {
        for (var i = 0; i < 40 && Rgb.Contrast(color, background) < contrast; i++)
            color = color.Lighten(dark ? 0.025 : -0.025);
        return color;
    }

    public override string ToString() => Name.ToString(CultureInfo.InvariantCulture);
}

/// <summary>One palette's colours for light or dark mode.</summary>
public sealed record PaletteShades(
    Rgb Accent, Rgb Light1, Rgb Light2, Rgb Light3, Rgb Dark1, Rgb Dark2, Rgb Dark3,
    Rgb Fill, Rgb Text, Rgb TextOnFill, Rgb? WindowBackground);
