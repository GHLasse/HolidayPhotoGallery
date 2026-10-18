using PhotoGallery.Core.Editing;

namespace PhotoGallery.Core.Tests;

public sealed class ImageMathTests
{
    [Theory]
    [InlineData(1.0, 4000, 3000, 500, 0, 3000, 3000)]
    [InlineData(16.0 / 9, 4000, 3000, 0, 375, 4000, 2250)]
    [InlineData(2.0 / 3, 4000, 3000, 1000, 0, 2000, 3000)]
    [InlineData(0, 4000, 3000, 0, 0, 4000, 3000)]
    public void Fits_the_largest_centred_rectangle(double ratio, int w, int h, int x, int y, int cw, int ch) =>
        Assert.Equal(new PixelRect(x, y, cw, ch), ImageMath.Fit(ratio, w, h));

    [Fact]
    public void Crops_rows_and_columns()
    {
        // 3 × 2 image whose blue byte is the pixel number.
        var image = Enumerable.Range(0, 6).SelectMany(i => new byte[] { (byte)i, 0, 0, 255 }).ToArray();
        var cropped = ImageMath.Crop(image, 3, 2, new PixelRect(1, 0, 2, 2));
        Assert.Equal([1, 2, 4, 5], Enumerable.Range(0, 4).Select(i => (int)cropped[i * 4]));
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageMath.Crop(image, 3, 2, new PixelRect(2, 0, 2, 2)));
    }

    [Fact]
    public void Auto_adjust_stretches_a_dull_dark_photo()
    {
        // Greys between 40 and 120, most of them near the dark end: low contrast and too dark.
        var image = Enumerable.Range(0, 10_000).SelectMany(i => { var v = (byte)(40 + i % 81 * (i % 81) / 80); return new[] { v, v, v, (byte)255 }; }).ToArray();
        var curve = ImageMath.AutoAdjustCurve(image);

        Assert.True(curve[40] <= 3);
        Assert.True(curve[120] >= 252);
        // 60 is a quarter of the way up the range; plain stretching would make it 64.
        Assert.True(curve[60] > 85, $"mid-tones should be lifted, got {curve[60]}");
        for (var v = 1; v < 256; v++)
            Assert.True(curve[v] >= curve[v - 1]);
    }

    [Fact]
    public void Auto_adjust_leaves_a_well_exposed_photo_almost_alone()
    {
        var image = Enumerable.Range(0, 25_600).SelectMany(i => { var v = (byte)(i % 256); return new[] { v, v, v, (byte)255 }; }).ToArray();
        var curve = ImageMath.AutoAdjustCurve(image);
        Assert.All(Enumerable.Range(0, 256), v => Assert.InRange(curve[v] - v, -6, 6));
    }
}
