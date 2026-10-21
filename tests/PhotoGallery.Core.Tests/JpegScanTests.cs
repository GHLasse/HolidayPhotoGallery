using PhotoGallery.Core.Library;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;

namespace PhotoGallery.Core.Tests;

public class JpegScanTests
{
    // A smooth picture with some detail, like a photo: 4:2:0 colour, as cameras store it.
    private static byte[] Jpeg(JpegEncodingColor color = JpegEncodingColor.YCbCrRatio420)
    {
        using var image = new Image<Rgb24>(320, 240);
        var random = new Random(3);
        image.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < rows.Height; y++)
            {
                var row = rows.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                    row[x] = new Rgb24((byte)(x * 255 / 320), (byte)(y * 255 / 240), (byte)(120 + random.Next(40)));
            }
        });
        using var stream = new MemoryStream();
        image.SaveAsJpeg(stream, new JpegEncoder { Quality = 90, ColorType = color });
        return stream.ToArray();
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
    [InlineData(JpegEncodingColor.YCbCrRatio420)]
    [InlineData(JpegEncodingColor.YCbCrRatio444)]
    [InlineData(JpegEncodingColor.Luminance)]
    public void Whole_picture_data_adds_up(JpegEncodingColor color)
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
}
