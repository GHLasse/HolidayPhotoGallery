using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.Core.Editing;

/// <summary>
/// A one-click filter (E-9): the light and colour sliders it turns, and what sliders can't do: black and white,
/// a sepia tone, a faded (matte) look, a tint in the shadows and in the highlights, and more or less of one colour.
/// </summary>
public sealed record Look
{
    /// <summary>Stays the same in every language; kept in settings and tests.</summary>
    public required string Key { get; init; }

    public required Func<string> Name { get; init; }

    /// <summary>What it does and what it is best for (Lasse, 2026-10-25).</summary>
    public required Func<string> Tip { get; init; }

    public Adjustments Sliders { get; init; } = Adjustments.None;

    /// <summary>0 keeps the colours, 1 is black and white.</summary>
    public double Grey { get; init; }

    /// <summary>0 to 1: a brown tone over the greys, like old prints.</summary>
    public double Sepia { get; init; }

    /// <summary>0 to 1: blacks lifted and whites lowered, for a washed-out, matte look.</summary>
    public double Fade { get; init; }

    /// <summary>Levels added to red, green and blue in the dark parts.</summary>
    public (int R, int G, int B) ShadowTint { get; init; }

    /// <summary>Levels added to red, green and blue in the bright parts.</summary>
    public (int R, int G, int B) HighlightTint { get; init; }

    /// <summary>More (above 1) or less of each colour.</summary>
    public (double R, double G, double B) Gain { get; init; } = (1, 1, 1);

    /// <summary>The Beauty look (E-10): it also works on the faces in the photo (see <see cref="Editing.Beauty"/>).</summary>
    public bool Beauty { get; init; }
}

/// <summary>The app's looks, and how they are applied with a strength.</summary>
public static class Looks
{
    public static readonly IReadOnlyList<Look> All =
    [
        new()
        {
            Key = "crisp", Name = () => Tr("Crisp"),
            Tip = () => Tr("More contrast and colour with a cool touch in the shadows and highlights. A good all-rounder that enhances without changing the light (in the style of Clarendon)."),
            Sliders = new Adjustments { Contrast = 25, Saturation = 20 },
            ShadowTint = (-4, 0, 10), HighlightTint = (-2, 2, 8),
        },
        new()
        {
            Key = "vivid-warm", Name = () => Tr("Vivid warm"),
            Tip = () => Tr("Sharper looking, more contrast and warm reds, yellows and oranges. Best for street, travel and colourful landscapes (in the style of Juno)."),
            Sliders = new Adjustments { Contrast = 25, Saturation = 25, Warmth = 25 },
            Gain = (1.06, 1.02, 0.94),
        },
        new()
        {
            Key = "soft-haze", Name = () => Tr("Soft haze"),
            Tip = () => Tr("Washed-out colours with gentle highlights. Best for minimal, vintage or indoor photos (in the style of Gingham)."),
            Sliders = new Adjustments { Highlights = -50, Saturation = -25, Contrast = -15 },
            Fade = 0.35, HighlightTint = (4, 2, 0),
        },
        new()
        {
            Key = "fresh", Name = () => Tr("Fresh"),
            Tip = () => Tr("Brighter, with livelier blues and greens and calmer reds. Best for nature, the outdoors, beaches and landscapes (in the style of Lark)."),
            Sliders = new Adjustments { Brightness = 15, Vibrance = 30 },
            Gain = (0.94, 1.05, 1.06),
        },
        new()
        {
            Key = "golden", Name = () => Tr("Golden"),
            Tip = () => Tr("A warm yellow glow, a little brighter, like old film. Best for lifestyle photos and golden-hour moods (in the style of Valencia)."),
            Sliders = new Adjustments { Brightness = 10, Warmth = 40, Contrast = -5 },
            Sepia = 0.15, Fade = 0.1,
        },
        new()
        {
            Key = "beauty", Name = () => Tr("Beauty"),
            Tip = () => Tr("Smoother skin, brighter eyes and whiter teeth on the faces in the photo, with a soft warm glow. For portraits whose camera didn’t already do this. Right-click to choose what it does."),
            Sliders = new Adjustments { Brightness = 6, Warmth = 8, Contrast = -5 },
            Beauty = true,
        },
        new()
        {
            Key = "vivid", Name = () => Tr("Vivid"),
            Tip = () => Tr("Strong, bright colours and more contrast. Best for flowers, food and sunny days."),
            Sliders = new Adjustments { Contrast = 20, Saturation = 40 },
        },
        new()
        {
            Key = "warm", Name = () => Tr("Warm"),
            Tip = () => Tr("Warmer colours, like late-afternoon sun. Best for portraits, sunsets and cold, grey light."),
            Sliders = new Adjustments { Warmth = 45 },
        },
        new()
        {
            Key = "cool", Name = () => Tr("Cool"),
            Tip = () => Tr("Cooler, bluer colours. Best for snow, water, cities and a calm mood."),
            Sliders = new Adjustments { Warmth = -45 },
        },
        new()
        {
            Key = "matte", Name = () => Tr("Matte"),
            Tip = () => Tr("Soft blacks and softer colours, like a matte print. Best for moody or quiet photos."),
            Sliders = new Adjustments { Contrast = -10, Saturation = -15 },
            Fade = 0.5,
        },
        new()
        {
            Key = "vintage", Name = () => Tr("Vintage"),
            Tip = () => Tr("Faded, warm colours with green-tinted shadows, like a print from the seventies. Best for nostalgic holiday photos."),
            Sliders = new Adjustments { Saturation = -20, Warmth = 20 },
            Fade = 0.3, Sepia = 0.2, ShadowTint = (0, 6, 4), HighlightTint = (8, 4, -6),
        },
        new()
        {
            Key = "bw", Name = () => Tr("Black and white"),
            Tip = () => Tr("Neutral black and white. Best for portraits, street photos and when colours distract."),
            Grey = 1,
        },
        new()
        {
            Key = "bw-contrast", Name = () => Tr("Black and white, strong"),
            Tip = () => Tr("Black and white with deep blacks and bright whites. Best for architecture, drama and strong shapes."),
            Grey = 1, Sliders = new Adjustments { Contrast = 50, Shadows = -15 },
        },
        new()
        {
            Key = "sepia", Name = () => Tr("Sepia"),
            Tip = () => Tr("Warm brown tones like a very old photo. Best for an old-fashioned feel."),
            Grey = 1, Sepia = 1, Fade = 0.1,
        },
    ];

    public static Look? Find(string? key) => All.FirstOrDefault(l => l.Key == key);

    /// <summary>
    /// Applies the look to the pixels in place, at a strength from 0 (nothing) to 100 (the full look), as a blend
    /// between the photo and the photo with the full look.
    /// </summary>
    public static void Apply(byte[] bgra, int width, int height, Look look, int strength)
    {
        if (strength <= 0 || bgra.Length == 0)
            return;
        var original = strength < 100 ? (byte[])bgra.Clone() : null;
        ImageAdjust.Apply(bgra, width, height, look.Sliders);

        var (gr, gg, gb) = look.Gain;
        Parallel.For(0, height, y =>
        {
            var row = y * width * 4;
            for (var o = row; o < row + width * 4; o += 4)
            {
                double b = bgra[o] * gb, g = bgra[o + 1] * gg, r = bgra[o + 2] * gr;
                var l = (r * 299 + g * 587 + b * 114) / 1000;
                if (look.Grey > 0)
                {
                    r += (l - r) * look.Grey;
                    g += (l - g) * look.Grey;
                    b += (l - b) * look.Grey;
                }
                if (look.Sepia > 0)
                {
                    // The classic sepia mix of the grey, blended in.
                    var t = l / 255;
                    var (sr, sg, sb) = (l * 1.07 + 12 * (1 - t), l * 0.92 + 4, l * 0.72);
                    r += (sr - r) * look.Sepia;
                    g += (sg - g) * look.Sepia;
                    b += (sb - b) * look.Sepia;
                }
                var bright = Math.Clamp((l - 96) / 128, 0, 1);
                var dark = Math.Clamp((160 - l) / 128, 0, 1);
                r += look.ShadowTint.R * dark + look.HighlightTint.R * bright;
                g += look.ShadowTint.G * dark + look.HighlightTint.G * bright;
                b += look.ShadowTint.B * dark + look.HighlightTint.B * bright;
                if (look.Fade > 0)
                {
                    // Black goes up to about a fifth of the way and white down a little.
                    var lift = 48 * look.Fade;
                    var scale = (255 - lift - 16 * look.Fade) / 255;
                    r = lift + r * scale;
                    g = lift + g * scale;
                    b = lift + b * scale;
                }
                bgra[o] = (byte)Math.Clamp(Math.Round(b), 0, 255);
                bgra[o + 1] = (byte)Math.Clamp(Math.Round(g), 0, 255);
                bgra[o + 2] = (byte)Math.Clamp(Math.Round(r), 0, 255);
            }
        });

        if (original is null)
            return;
        var keep = strength / 100.0;
        Parallel.For(0, height, y =>
        {
            var row = y * width * 4;
            for (var o = row; o < row + width * 4; o++)
            {
                if ((o & 3) != 3)
                    bgra[o] = (byte)Math.Round(original[o] + (bgra[o] - original[o]) * keep);
            }
        });
    }
}
