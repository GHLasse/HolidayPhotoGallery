using PhotoGallery.Core.Editing;

namespace PhotoGallery.Core.Tests;

public sealed class LookSuggestionTests
{
    // A picture of one colour on the top part and another below, with a little noise.
    private static byte[] Picture(int w, int h, (int R, int G, int B) top, (int R, int G, int B) bottom, int noise = 6)
    {
        var random = new Random(2);
        var pixels = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var c = y < h / 2 ? top : bottom;
                var n = random.Next(-noise, noise + 1);
                var o = (y * w + x) * 4;
                (pixels[o], pixels[o + 1], pixels[o + 2], pixels[o + 3]) =
                    ((byte)Math.Clamp(c.B + n, 0, 255), (byte)Math.Clamp(c.G + n, 0, 255), (byte)Math.Clamp(c.R + n, 0, 255), 255);
            }
        }
        return pixels;
    }

    [Fact]
    public void A_beach_with_blue_sky_and_sea_gets_fresh_and_cool()
    {
        var traits = LookSuggestions.Measure(Picture(80, 60, (90, 160, 230), (40, 110, 190)), 80, 60);
        Assert.True(traits.SkyAndWater > 0.8);
        var suggested = LookSuggestions.Suggest(traits, faces: 0, tags: [], count: 3);
        Assert.Contains("fresh", suggested);
        Assert.Contains("cool", suggested);
    }

    [Fact]
    public void A_sunset_gets_the_warm_looks()
    {
        var traits = LookSuggestions.Measure(Picture(80, 60, (240, 140, 40), (120, 50, 20)), 80, 60);
        var suggested = LookSuggestions.Suggest(traits, faces: 0, tags: ["Sonnenuntergang"], count: 3);
        Assert.Contains("golden", suggested);
        Assert.Contains("vivid-warm", suggested);
        Assert.DoesNotContain("cool", suggested);
    }

    [Fact]
    public void A_portrait_gets_beauty_first()
    {
        var traits = LookSuggestions.Measure(Picture(80, 60, (200, 160, 140), (60, 60, 70)), 80, 60);
        Assert.Equal("beauty", LookSuggestions.Suggest(traits, faces: 1, tags: [], count: 4)[0]);
        Assert.DoesNotContain("beauty", LookSuggestions.Suggest(traits, faces: 0, tags: [], count: 4));
    }

    [Fact]
    public void A_flat_grey_photo_gets_contrast_and_black_and_white()
    {
        var traits = LookSuggestions.Measure(Picture(80, 60, (130, 128, 126), (120, 120, 118), noise: 3), 80, 60);
        var suggested = LookSuggestions.Suggest(traits, faces: 0, tags: [], count: 4);
        Assert.Contains("crisp", suggested);
        Assert.Contains("bw", suggested);
    }

    [Fact]
    public void Pinned_looks_are_left_out_and_the_count_is_kept()
    {
        var traits = LookSuggestions.Measure(Picture(40, 30, (90, 160, 230), (40, 110, 190)), 40, 30);
        var suggested = LookSuggestions.Suggest(traits, 0, [], 5, except: ["fresh", "cool"]);
        Assert.Equal(5, suggested.Count);
        Assert.DoesNotContain("fresh", suggested);
        Assert.DoesNotContain("cool", suggested);
        Assert.Empty(LookSuggestions.Suggest(traits, 0, [], 0));
        Assert.Equal(Looks.All.Count, LookSuggestions.Suggest(traits, 0, [], 99).Count);
    }
}
