using PhotoGallery.Core.Library;

namespace PhotoGallery.Core.Tests;

public class FileCheckTests
{
    private static byte[] Noise()
    {
        var bgra = new byte[120 * 80 * 4];
        new Random(5).NextBytes(bgra);
        for (var i = 3; i < bgra.Length; i += 4)
            bgra[i] = 255;
        return bgra;
    }

    private static byte[] Jpeg() => TestImages.Jpeg(Noise(), 120, 80, 90);

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
        var png = TestImages.Png(Noise(), 120, 80);
        Assert.Equal(FileHealth.Whole, Check(png, ".png"));
        Assert.Equal(FileHealth.CutOff, Check(png[..(png.Length / 2)], ".png"));
    }

    [Fact]
    public void Webps_are_checked()
    {
        var webp = TestImages.Webp(Noise(), 120, 80, lossless: false);
        Assert.Equal(FileHealth.Whole, Check(webp, ".webp"));
        Assert.Equal(FileHealth.CutOff, Check(webp[..(webp.Length / 2)], ".webp"));
    }
    [Fact]
    public void A_jpeg_called_png_is_misnamed() => Assert.Equal(FileHealth.Misnamed, Check(Jpeg(), ".png"));

    [Fact]
    public void A_cut_off_jpeg_called_png_is_cut_off()
    {
        // The damage matters more than the name, and it is found by checking the file as a JPEG.
        var jpeg = Jpeg();
        Assert.Equal(FileHealth.CutOff, Check(jpeg[..(jpeg.Length * 2 / 3)], ".png"));
    }

    [Theory]
    [InlineData("sample.jpg", ".jpeg")]
    [InlineData("sample.tiff", ".tif")]
    [InlineData("sample.heic", ".heif")]
    public void Other_names_for_the_same_format_are_fine(string sample, string extension) =>
        Assert.Equal(FileHealth.Whole, Check(File.ReadAllBytes(Path.Combine("TestData", sample)), extension));

    [Theory]
    [InlineData("sample.jpg", ".jpg")]
    [InlineData("sample.png", ".png")]
    [InlineData("sample.gif", ".gif")]
    [InlineData("sample.webp", ".webp")]
    [InlineData("sample.tiff", ".tif")]
    [InlineData("sample.heic", ".heic")]
    public void Recognises_the_real_format(string sample, string extension)
    {
        using var stream = File.OpenRead(Path.Combine("TestData", sample));
        Assert.Equal(extension, FileCheck.RealExtension(stream));
    }

    [Fact]
    public void The_correct_name_keeps_the_case()
    {
        var folder = Directory.CreateTempSubdirectory().FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(folder, "IMG_4973.PNG"), Jpeg());
            File.WriteAllBytes(Path.Combine(folder, "beach.png"), Jpeg());
            File.WriteAllBytes(Path.Combine(folder, "fine.jpeg"), Jpeg());
            Assert.Equal("IMG_4973.JPG", FileCheck.CorrectName(Path.Combine(folder, "IMG_4973.PNG")));
            Assert.Equal("beach.jpg", FileCheck.CorrectName(Path.Combine(folder, "beach.png")));
            Assert.Null(FileCheck.CorrectName(Path.Combine(folder, "fine.jpeg")));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

}
