using PhotoGallery.Core.Editing;

namespace PhotoGallery.Core.Tests;

public sealed class LooksTests
{
    private static byte[] Pixels(params (int R, int G, int B)[] colours) =>
        colours.SelectMany(c => new[] { (byte)c.B, (byte)c.G, (byte)c.R, (byte)255 }).ToArray();

    private static (int R, int G, int B) At(byte[] bgra, int i) => (bgra[i * 4 + 2], bgra[i * 4 + 1], bgra[i * 4]);

    private static byte[] With(string key, int strength, params (int R, int G, int B)[] colours)
    {
        var pixels = Pixels(colours);
        Looks.Apply(pixels, colours.Length, 1, Looks.Find(key)!, strength);
        return pixels;
    }

    [Fact]
    public void Every_look_has_its_own_key_a_name_and_a_tip()
    {
        Assert.Equal(Looks.All.Count, Looks.All.Select(l => l.Key).Distinct().Count());
        Assert.All(Looks.All, l => Assert.False(string.IsNullOrWhiteSpace(l.Name()) || string.IsNullOrWhiteSpace(l.Tip())));
        // Lasse's five (2026-10-25) are there, under the app's own names.
        Assert.All(new[] { "crisp", "vivid-warm", "soft-haze", "fresh", "golden", "bw", "sepia" }, k => Assert.NotNull(Looks.Find(k)));
    }

    [Fact]
    public void Black_and_white_makes_grey()
    {
        var c = At(With("bw", 100, (200, 40, 40)), 0);
        Assert.True(Math.Abs(c.R - c.G) <= 1 && Math.Abs(c.G - c.B) <= 1);
    }

    [Fact]
    public void Sepia_is_brown_and_matte_lifts_the_blacks()
    {
        var s = At(With("sepia", 100, (128, 128, 128)), 0);
        Assert.True(s.R > s.G && s.G > s.B);
        Assert.True(At(With("matte", 100, (0, 0, 0)), 0).R >= 20);
    }

    [Fact]
    public void Strength_blends_between_the_photo_and_the_full_look()
    {
        Assert.Equal(Pixels((200, 40, 40)), With("bw", 0, (200, 40, 40)));
        var full = At(With("bw", 100, (200, 40, 40)), 0);
        var half = At(With("bw", 50, (200, 40, 40)), 0);
        Assert.InRange(half.R, full.R + 5, 199);
    }

    [Fact]
    public void Every_look_changes_a_colourful_photo_and_keeps_alpha()
    {
        foreach (var look in Looks.All)
        {
            var before = Pixels((30, 90, 160), (220, 180, 120), (60, 140, 60));
            var after = (byte[])before.Clone();
            Looks.Apply(after, 3, 1, look, 100);
            Assert.NotEqual(before, after);
            Assert.All(new[] { 3, 7, 11 }, i => Assert.Equal(255, after[i]));
        }
    }
}
