using PhotoGallery.Core.Editing;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;

namespace PhotoGallery.Core.Tests;

public class PixelRotationTests
{
    /// <summary>A 3 × 2 image, black with a red top-left pixel; animated ones get a second frame.</summary>
    private static Image<Rgba32> Sample(int frames = 1)
    {
        var image = new Image<Rgba32>(3, 2, new Rgba32(0, 0, 0));
        image[0, 0] = new Rgba32(255, 0, 0);
        for (var i = 1; i < frames; i++)
            image.Frames.AddFrame(image.Frames.RootFrame);
        return image;
    }

    [Theory]
    [InlineData(".png", true, 1, 0)]
    [InlineData(".png", false, 0, 2)]
    [InlineData(".webp", true, 1, 0)]
    public async Task Turns_the_pixels_without_loss(string extension, bool clockwise, int redX, int redY)
    {
        using var temp = new TempFolder();
        var source = Path.Combine(temp.Path, "photo" + extension);
        using (var image = Sample())
        {
            if (extension == ".png")
                await image.SaveAsPngAsync(source);
            else
                await image.SaveAsWebpAsync(source, new WebpEncoder { FileFormat = WebpFileFormatType.Lossless });
        }
        var target = Path.Combine(temp.Path, "turned" + extension);

        Assert.Equal((2, 3), await PixelRotation.TurnedSizeAsync(source));
        await PixelRotation.TurnAsync(source, target, clockwise);

        using var turned = await Image.LoadAsync<Rgba32>(target);
        Assert.Equal((2, 3), (turned.Width, turned.Height));
        Assert.Equal(new Rgba32(255, 0, 0), turned[redX, redY]);
        if (extension == ".webp")
            Assert.Equal(WebpFileFormatType.Lossless, turned.Metadata.GetWebpMetadata().FileFormat);
    }

    [Fact]
    public async Task Turns_every_frame_of_an_animated_gif()
    {
        using var temp = new TempFolder();
        var source = Path.Combine(temp.Path, "photo.gif");
        using (var image = Sample(frames: 2))
        {
            // The second frame differs: a blue bottom-right pixel.
            image.Frames[1][2, 1] = new Rgba32(0, 0, 255);
            await image.SaveAsGifAsync(source);
        }
        var target = Path.Combine(temp.Path, "turned.gif");

        await PixelRotation.TurnAsync(source, target, clockwise: true);

        using var turned = await Image.LoadAsync<Rgba32>(target);
        Assert.Equal(2, turned.Frames.Count);
        Assert.Equal((2, 3), (turned.Width, turned.Height));
        Assert.Equal(255, turned.Frames[1][0, 2].B);
    }

    [Fact]
    public void Handles_only_formats_without_a_rotation_tag()
    {
        Assert.True(PixelRotation.Handles("a.PNG"));
        Assert.True(PixelRotation.Handles("a.webp"));
        Assert.False(PixelRotation.Handles("a.jpg"));
        Assert.False(PixelRotation.Handles("a.heic"));
    }
}
