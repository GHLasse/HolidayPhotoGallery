using PhotoGallery.Core.Library;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;

namespace PhotoGallery.Core.Tests;

public class FileCheckTests
{
    private static byte[] Encoded(Action<Image<Rgba32>, Stream> save, bool progressive = false)
    {
        using var image = new Image<Rgba32>(120, 80);
        var random = new Random(5);
        image.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < rows.Height; y++)
                foreach (ref var p in rows.GetRowSpan(y))
                    p = new Rgba32((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
        });
        using var stream = new MemoryStream();
        save(image, stream);
        return stream.ToArray();
    }

    private static byte[] Jpeg() => Encoded((i, s) => i.SaveAsJpeg(s, new JpegEncoder { Quality = 90 }));

    private static FileHealth Check(byte[] bytes, string extension) => FileCheck.Check(new MemoryStream(bytes), extension);

    [Fact]
    public void A_whole_jpeg_is_whole() => Assert.Equal(FileHealth.Whole, Check(Jpeg(), ".jpg"));

    [Fact]
    public void A_jpeg_with_data_after_its_end_is_whole() =>
        Assert.Equal(FileHealth.Whole, Check([.. Jpeg(), .. new byte[5000]], ".jpg")); // e.g. a motion photo's video

    [Fact]
    public void A_cut_off_jpeg_is_found()
    {
        var jpeg = Jpeg();
        Assert.Equal(FileHealth.CutOff, Check(jpeg[..(jpeg.Length * 2 / 3)], ".jpg"));
    }

    [Fact]
    public void A_file_of_zeros_is_unreadable() => Assert.Equal(FileHealth.Unreadable, Check(new byte[20000], ".jpg"));

    [Fact]
    public void Pngs_are_checked()
    {
        var png = Encoded((i, s) => i.SaveAsPng(s));
        Assert.Equal(FileHealth.Whole, Check(png, ".png"));
        Assert.Equal(FileHealth.CutOff, Check(png[..(png.Length / 2)], ".png"));
    }

    [Fact]
    public void Webps_are_checked()
    {
        var webp = Encoded((i, s) => i.SaveAsWebp(s));
        Assert.Equal(FileHealth.Whole, Check(webp, ".webp"));
        Assert.Equal(FileHealth.CutOff, Check(webp[..(webp.Length / 2)], ".webp"));
    }
}
