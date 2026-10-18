using PhotoGallery.Core.Tagging;

namespace PhotoGallery.Core.Tests;

public sealed class AutoTagsTests
{
    [Fact]
    public void Replaces_earlier_auto_tags_and_keeps_the_users_own()
    {
        var tags = AutoTags.Replace(["Strand", "Auto/Berge", "Familie", "auto/Schnee"], ["Strand", "Meer"]);
        Assert.Equal(["Strand", "Familie", "Auto/Strand", "Auto/Meer"], tags);
    }

    [Fact]
    public void Picks_labels_at_or_above_the_threshold_best_first()
    {
        var picked = AutoTags.Pick(["beach", "city", "sea", "snow"], [0.6, 0.1, 0.9, 0.5], 0.5);
        Assert.Equal(["sea", "beach", "snow"], picked);
    }

    [Fact]
    public void Reads_one_label_per_line()
    {
        var list = AutoTags.ParseList("Strand\r\n\n  Meer \nstrand\nRom/Italien\nEssen, Trinken\n");
        Assert.Equal(["Strand", "Meer", "Rom Italien", "Essen  Trinken"], list);
    }

    [Fact]
    public void Starter_list_follows_the_language()
    {
        Assert.Contains("Strand", AutoTags.StarterList("de"));
        Assert.Contains("beach", AutoTags.StarterList("en"));
        Assert.Contains("beach", AutoTags.StarterList("fr"));
    }

    [Fact]
    public void A_plain_colour_becomes_the_same_value_everywhere()
    {
        // 300 × 200 pure orange (B=0, G=128, R=255).
        var image = Enumerable.Repeat(new byte[] { 0, 128, 255, 255 }, 300 * 200).SelectMany(p => p).ToArray();
        var values = ImageTagger.ToPixelValues(image, 300, 200);

        const int plane = ImageTagger.ImageSize * ImageTagger.ImageSize;
        Assert.Equal(3 * plane, values.Length);
        Assert.All(values.Take(plane), v => Assert.Equal(1f, v, 3));                    // red
        Assert.All(values.Skip(plane).Take(plane), v => Assert.Equal(128 / 127.5f - 1, v, 3)); // green
        Assert.All(values.Skip(2 * plane), v => Assert.Equal(-1f, v, 3));               // blue
    }

    [Fact]
    public void Shrinking_averages_fine_detail()
    {
        // Alternating black and white columns at twice the model's size average to mid grey.
        const int size = ImageTagger.ImageSize * 2;
        var image = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var v = (byte)(x % 2 == 0 ? 0 : 255);
                var o = (y * size + x) * 4;
                image[o] = image[o + 1] = image[o + 2] = v;
            }
        var values = ImageTagger.ToPixelValues(image, size, size);
        // Away from the edges every value is close to the middle (0).
        var middle = ImageTagger.ImageSize / 2;
        Assert.InRange(values[middle * ImageTagger.ImageSize + middle], -0.02f, 0.02f);
    }
}
