using PhotoGallery.Core.Editing;

namespace PhotoGallery.Core.Tests;

public sealed class EyeFixTests
{
    private const int Size = 80;
    private const double Middle = 40;

    // A painted eye on skin: the white of the eye, an iris, a pupil and the flash's highlight, with a little noise.
    private static byte[] Eye(double irisRadius, (byte R, byte G, byte B) iris, double pupilRadius, (byte R, byte G, byte B) pupil,
                              bool highlight = true, int seed = 5)
    {
        var random = new Random(seed);
        var pixels = new byte[Size * Size * 4];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var (dx, dy) = (x + 0.5 - Middle, y + 0.5 - Middle);
                var d = Math.Sqrt(dx * dx + dy * dy);
                (byte R, byte G, byte B) c = (214, 168, 140); // skin
                if (dx * dx / (34.0 * 34) + dy * dy / (16.0 * 16) <= 1)
                    c = (232, 228, 224); // white of the eye
                if (d <= irisRadius)
                    c = iris;
                if (d <= pupilRadius)
                    c = pupil;
                if (highlight && Math.Sqrt((dx + 2) * (dx + 2) + (dy + 2) * (dy + 2)) <= 1.6)
                    c = (250, 250, 250);
                var o = (y * Size + x) * 4;
                var noise = random.Next(-3, 4);
                pixels[o] = (byte)Math.Clamp(c.B + noise, 0, 255);
                pixels[o + 1] = (byte)Math.Clamp(c.G + noise, 0, 255);
                pixels[o + 2] = (byte)Math.Clamp(c.R + noise, 0, 255);
                pixels[o + 3] = 255;
            }
        }
        return pixels;
    }

    private static (int R, int G, int B) At(byte[] pixels, double x, double y)
    {
        var o = ((int)y * Size + (int)x) * 4;
        return (pixels[o + 2], pixels[o + 1], pixels[o]);
    }

    private static readonly (byte, byte, byte) Blue = (70, 110, 160);
    private static readonly (byte, byte, byte) Red = (210, 40, 45);

    [Fact]
    public void A_red_pupil_turns_dark_and_the_blue_iris_stays_as_it_was()
    {
        var pixels = Eye(12, Blue, 5, Red);
        var before = (byte[])pixels.Clone();

        Assert.True(EyeFix.Fix(pixels, Size, Size, Middle, Middle, 16, animal: false));

        var pupil = At(pixels, Middle + 2, Middle + 2);
        Assert.True(pupil.R < 70 && pupil.R - Math.Max(pupil.G, pupil.B) < 12, $"pupil still red: {pupil}");
        // The iris, the white of the eye and the skin are untouched.
        Assert.Equal(At(before, Middle + 9, Middle), At(pixels, Middle + 9, Middle));
        Assert.Equal(At(before, Middle + 25, Middle), At(pixels, Middle + 25, Middle));
        Assert.Equal(At(before, Middle, Middle - 30), At(pixels, Middle, Middle - 30));
        // The highlight of the flash is kept.
        Assert.True(At(pixels, Middle - 2, Middle - 2).G > 200);
    }

    [Fact]
    public void Red_over_the_whole_iris_gets_a_pupil_and_an_iris_that_is_not_brown()
    {
        var pixels = Eye(12, Red, 12, Red);

        Assert.True(EyeFix.Fix(pixels, Size, Size, Middle, Middle, 20, animal: false));

        var pupil = At(pixels, Middle + 2, Middle + 2);
        var iris = At(pixels, Middle + 9, Middle);
        Assert.True(pupil.R < 60, $"pupil: {pupil}");
        Assert.True(iris.R - Math.Max(iris.G, iris.B) < 10, $"iris still red or brown: {iris}");
        Assert.True(iris.R + iris.G + iris.B > pupil.R + pupil.G + pupil.B, "the iris is lighter than the pupil");
    }

    [Fact]
    public void A_chosen_iris_colour_is_given_back()
    {
        var pixels = Eye(12, Red, 12, Red);

        Assert.True(EyeFix.Fix(pixels, Size, Size, Middle, Middle, 20, animal: false, EyeColour.Blue));

        var iris = At(pixels, Middle + 8, Middle);
        Assert.True(iris.B > iris.R + 15, $"not blue: {iris}");
    }

    [Fact]
    public void An_eye_without_red_eye_is_left_alone()
    {
        var pixels = Eye(12, Blue, 5, (20, 20, 22));
        var before = (byte[])pixels.Clone();

        Assert.False(EyeFix.Fix(pixels, Size, Size, Middle, Middle, 16, animal: false));
        Assert.Equal(before, pixels);
    }

    [Fact]
    public void Red_everywhere_is_not_taken_for_an_eye()
    {
        var pixels = Eye(60, Red, 60, Red, highlight: false);
        var before = (byte[])pixels.Clone();

        Assert.False(EyeFix.Fix(pixels, Size, Size, Middle, Middle, 16, animal: false));
        Assert.Equal(before, pixels);
    }

    [Theory]
    [InlineData(235, 215, 90)]  // yellow
    [InlineData(150, 240, 140)] // green
    [InlineData(245, 245, 240)] // white
    public void Animal_eyeshine_of_any_colour_turns_into_a_dark_pupil(byte r, byte g, byte b)
    {
        var pixels = Eye(12, (120, 90, 50), 8, (r, g, b), highlight: false);
        var before = (byte[])pixels.Clone();

        Assert.True(EyeFix.Fix(pixels, Size, Size, Middle, Middle, 18, animal: true));

        var pupil = At(pixels, Middle + 3, Middle + 3);
        Assert.True(pupil.R + pupil.G + pupil.B < 150, $"pupil still glowing: {pupil}");
        Assert.Equal(At(before, Middle + 25, Middle), At(pixels, Middle + 25, Middle)); // the white of the eye stays
    }

    [Fact]
    public void People_mode_leaves_animal_eyeshine_alone()
    {
        var pixels = Eye(12, (120, 90, 50), 8, (235, 215, 90), highlight: false);

        Assert.False(EyeFix.Fix(pixels, Size, Size, Middle, Middle, 18, animal: false));
    }

    [Fact]
    public void Retouch_counts_only_the_eye_strokes_that_changed_something()
    {
        var pixels = Eye(12, Blue, 5, Red);
        var red = new RetouchStroke(RetouchTool.RedEye, [(0.5, 0.5)], 16.0 / Size);
        var skin = new RetouchStroke(RetouchTool.RedEye, [(0.1, 0.1)], 6.0 / Size);

        Assert.Equal(1, Retouch.Apply(pixels, Size, Size, [red, skin], erase: null));
        Assert.True(red.ForEyes);
    }
}
