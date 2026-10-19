using PhotoGallery.Core.Appearance;

namespace PhotoGallery.Core.Tests;

public class ColorPaletteTests
{
    public static TheoryData<string, bool> Palettes()
    {
        var data = new TheoryData<string, bool>();
        foreach (var palette in ColorPalette.All.Where(p => p.Color is not null))
        {
            data.Add(palette.Key, false);
            data.Add(palette.Key, true);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Palettes))]
    public void Every_palette_keeps_text_and_highlights_readable(string key, bool dark)
    {
        var shades = ColorPalette.Find(key).ShadesFor(dark)!;
        // Windows' normal and secondary text colours (secondary is about #5D5D5D in light mode, #C5C5C5 in dark).
        var normal = dark ? Rgb.White : Rgb.Parse("#1A1A1A");
        var secondary = dark ? Rgb.Parse("#C5C5C5") : Rgb.Parse("#5D5D5D");

        foreach (var background in new[] { shades.WindowBackground, shades.BarBackground })
        {
            Assert.True(Rgb.Contrast(shades.Text, background) >= 4.5, $"text {shades.Text} on {background}");
            Assert.True(Rgb.Contrast(shades.Fill, background) >= 3, $"fill {shades.Fill} on {background}");
            Assert.True(Rgb.Contrast(normal, background) >= 7, $"normal text on {background}");
            Assert.True(Rgb.Contrast(secondary, background) >= 4.5, $"secondary text on {background}");
        }
        Assert.True(Rgb.Contrast(shades.TextOnFill, shades.Fill) >= 4.5, $"{shades.TextOnFill} on fill {shades.Fill}");
    }

    [Fact]
    public void Yellow_gets_dark_text_and_blue_gets_white_text()
    {
        Assert.Equal(Rgb.Black, ColorPalette.Find("illuminating").ShadesFor(false)!.TextOnFill);
        Assert.Equal(Rgb.White, ColorPalette.Find("classic-blue").ShadesFor(false)!.TextOnFill);
    }

    [Fact]
    public void The_windows_accent_needs_no_shades_and_unknown_keys_fall_back_to_it()
    {
        Assert.Null(ColorPalette.Find("windows").ShadesFor(true));
        Assert.Equal(ColorPalette.WindowsKey, ColorPalette.Find("nonsense").Key);
    }

    [Theory]
    [MemberData(nameof(Palettes))]
    public void Every_palette_visibly_tints_the_window_and_more_so_the_bars(string key, bool dark)
    {
        var shades = ColorPalette.Find(key).ShadesFor(dark)!;
        var neutral = dark ? ColorPalette.DarkBackground : ColorPalette.LightBackground;

        Assert.NotEqual(neutral, shades.WindowBackground);
        Assert.NotEqual(shades.WindowBackground, shades.BarBackground);
    }

    [Fact]
    public void Mixing_blends_between_two_colours()
    {
        Assert.Equal(Rgb.Black, Rgb.Black.Mix(Rgb.White, 0));
        Assert.Equal(Rgb.White, Rgb.Black.Mix(Rgb.White, 1));
        Assert.Equal("#808080", Rgb.Black.Mix(Rgb.White, 0.5).ToString());
    }

    [Fact]
    public void Lightening_keeps_the_hue()
    {
        Assert.Equal("#0F4C81", Rgb.Parse("#0F4C81").Lighten(0).ToString());
        Assert.Equal(Rgb.White, Rgb.Parse("#0F4C81").Lighten(1));
        Assert.Equal(21, Rgb.Contrast(Rgb.Black, Rgb.White), 1);
    }
}
