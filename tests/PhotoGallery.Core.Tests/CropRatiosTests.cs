using PhotoGallery.Core.Editing;

namespace PhotoGallery.Core.Tests;

public sealed class CropRatiosTests
{
    [Theory]
    [InlineData("3:2", 6000, 4000, false, 1.5)]
    [InlineData("3:2", 4000, 6000, false, 2.0 / 3)]   // a portrait photo gets a portrait crop
    [InlineData("3:2", 6000, 4000, true, 2.0 / 3)]    // turned on purpose
    [InlineData("original", 4000, 3000, false, 4.0 / 3)]
    [InlineData("original", 3000, 4000, false, 0.75)]
    [InlineData("original", 4000, 3000, true, 0.75)]
    [InlineData("free", 4000, 3000, false, 0)]
    [InlineData("1:1", 3000, 4000, true, 1)]
    public void Resolves_the_crop_shape_for_the_photo(string key, int width, int height, bool turned, double expected) =>
        Assert.Equal(expected, CropRatios.Resolve(CropRatios.Find(key), width, height, turned), 6);

    [Fact]
    public void Unknown_keys_fall_back_to_the_photos_own_shape()
    {
        Assert.Equal(CropRatios.Original, CropRatios.Find("7:3").Key);
        Assert.Equal(CropRatios.Original, CropRatios.Find(null).Key);
        Assert.Equal(CropRatios.Original, CropRatios.All[0].Key);
    }
}
