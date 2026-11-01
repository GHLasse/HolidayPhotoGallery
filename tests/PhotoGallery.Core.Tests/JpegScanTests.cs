using PhotoGallery.Core.Library;

namespace PhotoGallery.Core.Tests;

public class JpegScanTests
{
    // A smooth picture with some detail, like a photo: 4:2:0 colour, as cameras store it.
    public enum Colour { Ratio420, Ratio444, Grey }

    private static byte[] Jpeg(Colour colour = Colour.Ratio420)
    {
        var random = new Random(3);
        var bgra = new byte[320 * 240 * 4];
        var grey = new byte[320 * 240];
        for (var y = 0; y < 240; y++)
            for (var x = 0; x < 320; x++)
            {
                var (r, g, b) = ((byte)(x * 255 / 320), (byte)(y * 255 / 240), (byte)(120 + random.Next(40)));
                var i = y * 320 + x;
                (bgra[i * 4], bgra[i * 4 + 1], bgra[i * 4 + 2], bgra[i * 4 + 3]) = (b, g, r, 255);
                grey[i] = (byte)((r * 299 + g * 587 + b * 114) / 1000);
            }
        return colour switch
        {
            Colour.Grey => TestImages.GreyJpeg(grey, 320, 240),
            Colour.Ratio444 => TestImages.Jpeg(bgra, 320, 240, 90, SkiaSharp.SKJpegEncoderDownsample.Downsample444),
            _ => TestImages.Jpeg(bgra, 320, 240),
        };
    }

    private static (int Start, int End) Data(byte[] jpeg)
    {
        var sos = 2;
        while (!(jpeg[sos] == 0xFF && jpeg[sos + 1] == 0xDA))
            sos++;
        var start = sos + 2 + (jpeg[sos + 2] << 8 | jpeg[sos + 3]);
        return (start, jpeg.Length - 2);
    }

    private static JpegScanResult? Scan(byte[] bytes) => JpegScan.Check(new MemoryStream(bytes));

    [Theory]
    [InlineData(Colour.Ratio420)]
    [InlineData(Colour.Ratio444)]
    [InlineData(Colour.Grey)]
    public void Whole_picture_data_adds_up(Colour color)
    {
        var result = Scan(Jpeg(color));
        Assert.NotNull(result);
        Assert.False(result.Damaged);
        Assert.False(result.MissingEnd);
    }

    [Fact]
    public void Lost_bytes_in_the_middle_are_found()
    {
        var jpeg = Jpeg();
        var (start, end) = Data(jpeg);
        var middle = (start + end) / 2;
        // Some bytes of the data lost, e.g. on a failing card; the file still ends properly.
        var damaged = jpeg.Take(middle).Concat(jpeg.Skip(middle + 7)).ToArray();
        Assert.True(Scan(damaged)!.Damaged);
        Assert.Equal(FileHealth.Garbled, FileCheck.Check(new MemoryStream(damaged), ".jpg"));
    }

    [Fact]
    public void A_missing_end_mark_alone_can_be_mended()
    {
        var jpeg = Jpeg();
        var path = Path.Combine(Path.GetTempPath(), $"mend-{Guid.NewGuid():N}.jpg");
        try
        {
            File.WriteAllBytes(path, jpeg[..^2]);
            Assert.Equal(FileHealth.CutOff, FileCheck.Check(path));
            Assert.True(FileMend.CanMend(path));

            FileMend.Mend(path);
            Assert.Equal(FileHealth.Whole, FileCheck.Check(path));
            Assert.Equal(jpeg, File.ReadAllBytes(path)); // the picture data is untouched
            Assert.Equal(1, Editing.EditHistory.EditCount(path)); // the file as it was is kept
        }
        finally
        {
            foreach (var kept in Editing.EditHistory.FilesOf(path))
                File.Delete(kept);
            File.Delete(path);
        }
    }

    [Fact]
    public void A_file_cut_off_inside_the_picture_data_is_not_mended()
    {
        var jpeg = Jpeg();
        var (start, end) = Data(jpeg);
        var path = Path.Combine(Path.GetTempPath(), $"mend-{Guid.NewGuid():N}.jpg");
        try
        {
            File.WriteAllBytes(path, jpeg[..((start + end) / 2)]);
            Assert.False(FileMend.CanMend(path));
            Assert.Throws<IOException>(() => FileMend.Mend(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Progressive_jpegs_are_not_read()
    {
        // The same file marked as progressive (SOF2): its data is coded another way, which isn't read here.
        var jpeg = Jpeg();
        var sof = Array.FindIndex(jpeg, 2, b => b == 0xC0 || b == 0xC1) ;
        Assert.Equal(0xFF, jpeg[sof - 1]);
        jpeg[sof] = 0xC2;
        Assert.Null(Scan(jpeg));
    }

    [Fact]
    public void A_progressive_jpeg_without_its_end_mark_can_be_mended()
    {
        // Its data can't be followed, but once the end mark is added the file checks out.
        var jpeg = Jpeg();
        var sof = Array.FindIndex(jpeg, 2, b => b == 0xC0 || b == 0xC1);
        jpeg[sof] = 0xC2;
        var path = Path.Combine(Path.GetTempPath(), $"mend-{Guid.NewGuid():N}.jpg");
        try
        {
            File.WriteAllBytes(path, jpeg[..^2]);
            Assert.Equal(FileHealth.CutOff, FileCheck.Check(path));
            Assert.True(FileMend.CanMend(path));

            FileMend.Mend(path);
            Assert.Equal(FileHealth.Whole, FileCheck.Check(path));
            Assert.Equal(jpeg, File.ReadAllBytes(path));
        }
        finally
        {
            foreach (var kept in Editing.EditHistory.FilesOf(path))
                File.Delete(kept);
            File.Delete(path);
        }
    }

    [Fact]
    public void A_progressive_jpeg_cut_inside_a_segment_is_not_mended()
    {
        var jpeg = Jpeg();
        var sof = Array.FindIndex(jpeg, 2, b => b == 0xC0 || b == 0xC1);
        jpeg[sof] = 0xC2;
        var path = Path.Combine(Path.GetTempPath(), $"mend-{Guid.NewGuid():N}.jpg");
        try
        {
            File.WriteAllBytes(path, jpeg[..(sof + 4)]); // inside the frame header
            Assert.False(FileMend.CanMend(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
