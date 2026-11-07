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

    // Lasse's old Citroën in a grey Amsterdam street: dull colours, nothing in the pixels says "old".
    private static LookSuggestions.Traits GreyStreet => new(0.55, 0.6, 0.25, 0.5, 0.05, 0.1, 0.02);

    [Fact]
    public void An_old_car_the_model_recognises_gets_vintage_and_sepia()
    {
        var scenes = new Dictionary<string, double> { ["vintage"] = 0.55, ["sepia"] = 0.4 };
        var suggested = LookSuggestions.Suggest(GreyStreet, faces: 0, tags: [], count: 4, scenes: scenes);
        Assert.Equal("vintage", suggested[0]);
        Assert.Contains("sepia", suggested.Take(3));
        Assert.DoesNotContain("vintage", LookSuggestions.Suggest(GreyStreet, faces: 0, tags: [], count: 4));
    }

    [Fact]
    public void An_oldtimer_tag_gets_vintage_and_sepia()
    {
        var suggested = LookSuggestions.Suggest(GreyStreet, faces: 0, tags: ["Oldtimer"], count: 4);
        Assert.Contains("vintage", suggested);
        Assert.Contains("sepia", suggested);
    }

    [Fact]
    public void Scene_shares_add_up_per_look_and_leave_out_everyday_scenes()
    {
        // Picture and prompt vectors of length 2: the picture matches the first prompt (an old car) only.
        var vectors = LookSuggestions.Scenes.Select((_, i) => i == 0 ? new[] { 1f, 0f } : new[] { 0f, 1f }).ToList();
        var shares = LookSuggestions.SceneShares([1f, 0f], vectors);
        Assert.True(shares["vintage"] > 0.9, $"vintage {shares["vintage"]:P0}");
        Assert.True(shares.Values.Sum() <= 1.0001);
        Assert.DoesNotContain(shares.Keys, k => Looks.Find(k) is null);
    }

    // Photos from Wikimedia Commons that CI puts in look-scenes next to the tagging model (see build.yml).
    [TagModelFact]
    public void The_model_sees_which_looks_suit_real_photos()
    {
        var folder = Path.Combine(TagModelFactAttribute.Folder!, "look-scenes");
        using var tagger = Tagging.ImageTagger.Load(TagModelFactAttribute.Folder!);
        var prompts = tagger.EmbedTexts(LookSuggestions.Scenes.Select(s => s.Prompt).ToList());
        IReadOnlyList<string> Suggested(string file)
        {
            var image = TestImages.Decode(Path.Combine(folder, file))!.Value;
            var shares = LookSuggestions.SceneShares(tagger.EmbedImage(image.Pixels, image.Width, image.Height), prompts);
            var suggested = LookSuggestions.Suggest(LookSuggestions.Measure(image.Pixels, image.Width, image.Height), 0, [], 6, scenes: shares);
            Console.WriteLine($"LOOKSCENE {file}: {string.Join(", ", shares.OrderByDescending(s => s.Value).Take(3).Select(s => $"{s.Key} {s.Value:P0}"))} -> {string.Join(" ", suggested)}");
            return suggested;
        }

        foreach (var car in new[] { "old-car-citroen-ds.jpg", "old-car-rusty.jpg" })
        {
            var suggested = Suggested(car);
            Assert.Equal("vintage", suggested[0]);
            Assert.Contains("sepia", suggested);
        }
        foreach (var old in new[] { "typewriter.jpg", "vespa.jpg" })
            Assert.Equal("vintage", Suggested(old)[0]);
        Assert.Equal("sepia", Suggested("castle.jpg")[0]);
        Assert.Equal("vivid", Suggested("food-cake.jpg")[0]);
        Assert.Equal("cool", Suggested("snow-mountain-lake.jpg")[0]);
        foreach (var modern in new[] { "modern-street.jpg", "hiking-trail.jpg", "sports-motorbike.jpg" })
        {
            var suggested = Suggested(modern);
            Assert.DoesNotContain("vintage", suggested.Take(3));
            Assert.DoesNotContain("sepia", suggested.Take(3));
        }
    }
}
