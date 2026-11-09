using System.Text;
using PhotoGallery.Core.Editing;
using PhotoGallery.Core.ExifTool;
using PhotoGallery.Core.Faces;
using PhotoGallery.Core.Metadata;
using SkiaSharp;

namespace PhotoGallery.Core.Tests;

/// <summary>E-16: pixel edits keep the colour profile and HDR gain map, renew the thumbnail and drop Lightroom's edits.</summary>
public sealed class ColourSafeEditTests : IAsyncLifetime
{
    private ExifToolProcess? _exifTool;
    private readonly TempFolder _temp = new();

    private PhotoMetadataService Metadata => new(_exifTool!);

    private PhotoEditService Editor => new(Metadata);

    public Task InitializeAsync()
    {
        _exifTool = ExifToolProcess.TryStart();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_exifTool is not null)
            await _exifTool.DisposeAsync();
        _temp.Dispose();
    }

    private static byte[] Colours(int width, int height)
    {
        var bgra = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                bgra[i] = (byte)(x * 255 / width);
                bgra[i + 1] = (byte)(y * 255 / height);
                bgra[i + 2] = 180;
                bgra[i + 3] = 255;
            }
        return bgra;
    }

    // A grey gain map, brighter on the left, with Adobe/Google's gain map notes in its own XMP.
    private static byte[] GainMap(int width, int height)
    {
        var grey = new byte[width * height];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                grey[y * width + x] = x < width / 2 ? (byte)230 : (byte)20;
        var jpeg = TestImages.GreyJpeg(grey, width, height);
        const string xmp = "<x:xmpmeta xmlns:x='adobe:ns:meta/'><rdf:RDF xmlns:rdf='http://www.w3.org/1999/02/22-rdf-syntax-ns#'>" +
                           "<rdf:Description rdf:about='' xmlns:hdrgm='http://ns.adobe.com/hdr-gain-map/1.0/' hdrgm:Version='1.0' " +
                           "hdrgm:GainMapMax='2.3' hdrgm:HDRCapacityMax='2.3'/></rdf:RDF></x:xmpmeta>";
        var payload = Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0").Concat(Encoding.UTF8.GetBytes(xmp)).ToArray();
        var segment = new byte[] { 0xFF, 0xE1, (byte)((payload.Length + 2) >> 8), (byte)((payload.Length + 2) & 0xFF) };
        return [0xFF, 0xD8, .. segment, .. payload, .. jpeg[2..]];
    }

    // An Ultra HDR photo, width × height, its gain map a quarter of the size.
    private async Task<string> HdrPhotoAsync(int width = 120, int height = 80, string name = "hdr.jpg")
    {
        var path = Path.Combine(_temp.Path, name);
        await File.WriteAllBytesAsync(path, TestImages.Jpeg(Colours(width, height), width, height));
        await _exifTool!.RunAsync(["-overwrite_original", "-XMP-hdrgm:Version=1.0", "-IFD0:Make=Google", path]);
        await File.WriteAllBytesAsync(path, GainMapJpeg.Attach(await File.ReadAllBytesAsync(path), GainMap(width / 4, height / 4), null));
        return path;
    }

    private Func<string, Task> Encoded(int width, int height) => target =>
        File.WriteAllBytesAsync(target, TestImages.Jpeg(Colours(width, height), width, height));

    private async Task<string> TagAsync(string path, string tag) =>
        (await _exifTool!.RunAsync(["-s3", tag, path])).Output.Trim();

    [ExifToolFact]
    public async Task An_attached_gain_map_is_found_by_exiftool_and_survives_metadata_saves()
    {
        var path = await HdrPhotoAsync();

        Assert.Equal("2", await TagAsync(path, "-MPF:NumberOfImages"));
        Assert.True(PhotoQuality.IsHdr((await _exifTool!.RunAsync(["-json", "-G1", "-XMP-hdrgm:Version", "-MPF:NumberOfImages", path])).Output));
        var gainMap = GainMapJpeg.Find(await File.ReadAllBytesAsync(path));
        Assert.NotNull(gainMap);
        Assert.Equal(gainMap!.Length.ToString(), await TagAsync(path, "-MPImage2:MPImageLength"));
        // Ultra HDR's directory in the photo's XMP names the gain map's length (ExifTool versions call its group differently).
        Assert.Contains($"Item:Semantic='GainMap' Item:Mime='image/jpeg' Item:Length='{gainMap.Length}'", Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path)));

        // A rating written later by ExifTool keeps the gain map where the index says.
        await Metadata.WriteAsync(path, new MetadataChanges { Rating = 4 });
        Assert.Equal(gainMap, GainMapJpeg.Find(await File.ReadAllBytesAsync(path)));
    }

    [ExifToolFact]
    public async Task A_cropped_hdr_jpeg_keeps_its_gain_map_cropped_the_same_way()
    {
        var path = await HdrPhotoAsync();
        // The right half is kept: there the gain map is dark.
        var kept = new FaceBox(0.5, 0, 0.5, 1);

        await Editor.SaveEditAsync(path, ".jpg", 60, 80, Encoded(60, 80), kept, geometry: new EditGeometry(Crop: kept));

        var check = await Editor.InspectAsync(path, ".jpg");
        Assert.False(check.LosesHdr);
        Assert.True((await Metadata.ReadQualityAsync(path)).Hdr);
        var gainMap = GainMapJpeg.Find(await File.ReadAllBytesAsync(path))!;
        Assert.Contains("hdrgm:GainMapMax", Encoding.Latin1.GetString(gainMap)); // its own notes stay
        using var codec = SKCodec.Create(new SKMemoryStream(gainMap));
        Assert.Equal((15, 20), (codec.Info.Width, codec.Info.Height));
        Assert.Equal(SKColorType.Gray8, codec.Info.ColorType);
        var (pixels, _, _) = TestImages.Decode(gainMap)!.Value;
        Assert.True(pixels.Where((_, i) => i % 4 == 1).Average(v => v) < 60);
    }

    [ExifToolFact]
    public async Task A_turned_photo_turns_its_gain_map_too()
    {
        var path = await HdrPhotoAsync();
        await Metadata.WriteAsync(path, new MetadataChanges { Orientation = 6 });

        await Editor.SaveEditAsync(path, ".jpg", 80, 120, Encoded(80, 120), geometry: new EditGeometry());

        using var codec = SKCodec.Create(new SKMemoryStream(GainMapJpeg.Find(await File.ReadAllBytesAsync(path))!));
        Assert.Equal((20, 30), (codec.Info.Width, codec.Info.Height));
    }

    [ExifToolFact]
    public async Task Without_its_gain_map_the_photo_is_no_longer_hdr()
    {
        var path = await HdrPhotoAsync();
        Assert.True((await Editor.InspectAsync(path, ".png")).LosesHdr);
        Assert.False((await Editor.InspectAsync(path, ".jpg")).LosesHdr);

        // Saved without the edit's shape (e.g. a repair), or into a format without gain maps.
        await Editor.SaveEditAsync(path, ".jpg", 120, 80, Encoded(120, 80));

        Assert.Null(GainMapJpeg.Find(await File.ReadAllBytesAsync(path)));
        Assert.False((await Metadata.ReadQualityAsync(path)).Hdr);
        Assert.Equal("", await TagAsync(path, "-XMP-hdrgm:all"));
    }

    [Theory]
    [InlineData("""[{"SourceFile":"a.jpg","XMP-hdrgm:Version":"1.0","MPF0:NumberOfImages":2}]""", true)]
    [InlineData("""[{"SourceFile":"a.jpg","XMP-hdrgm:Version":"1.0"}]""", false)]
    [InlineData("""[{"SourceFile":"a.jpg","Apple:HDRHeadroom":1.5,"MPF0:NumberOfImages":2}]""", true)]
    [InlineData("""[{"SourceFile":"a.heic","Apple:HDRHeadroom":1.5}]""", false)]
    [InlineData("""[{"SourceFile":"a.heic","QuickTime:AuxiliaryImageType":"urn:com:apple:photo:2020:aux:hdrgainmap"}]""", true)]
    public void Hdr_notes_count_only_with_a_gain_map(string json, bool hdr) => Assert.Equal(hdr, PhotoQuality.Read(json).Hdr);

    [Theory]
    [InlineData("""[{"SourceFile":"a.heic","ICC_Profile:ProfileDescription":"Display P3"}]""", "P3")]
    [InlineData("""[{"SourceFile":"a.jpg","ICC_Profile:ProfileDescription":"sRGB IEC61966-2.1"}]""", "sRGB")]
    [InlineData("""[{"SourceFile":"a.jpg","ICC_Profile:ProfileDescription":"Adobe RGB (1998)"}]""", "Adobe RGB")]
    [InlineData("""[{"SourceFile":"a.jpg","ICC_Profile:ProfileDescription":"ProPhoto RGB"}]""", null)]
    [InlineData("""[{"SourceFile":"a.jpg","ExifIFD:ColorSpace":"sRGB"}]""", "sRGB")]
    [InlineData("""[{"SourceFile":"a.jpg","ExifIFD:ColorSpace":"Uncalibrated","InteropIFD:InteropIndex":"R03 - DCF option file (Adobe RGB)"}]""", "Adobe RGB")]
    [InlineData("""[{"SourceFile":"a.heic","QuickTime:ColorPrimaries":"SMPTE EG 432-1"}]""", "P3")]
    [InlineData("""[{"SourceFile":"a.jpg"}]""", null)]
    public void Names_the_colour_space(string json, string? space) => Assert.Equal(space, PhotoQuality.Read(json).ColourSpace);

    // A JPEG with an embedded Display P3 profile, as Skia writes it.
    private static byte[] P3Jpeg(int width, int height)
    {
        var p3 = SKColorSpace.CreateRgb(SKColorSpaceTransferFn.Srgb, SKColorSpaceXyz.DisplayP3);
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul, p3));
        System.Runtime.InteropServices.Marshal.Copy(Colours(width, height), 0, bitmap.GetPixels(), width * height * 4);
        using var data = bitmap.Encode(SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }

    [ExifToolFact]
    public async Task The_colour_profile_is_kept_when_the_pixels_stay_in_its_colours()
    {
        var path = Path.Combine(_temp.Path, "p3.jpg");
        await File.WriteAllBytesAsync(path, P3Jpeg(100, 80));
        var profile = await TagAsync(path, "-ICC_Profile:ProfileDescription");
        Assert.NotEqual("", profile);
        Assert.True((await Editor.InspectAsync(path, ".jpg")).ColourProfile);

        await Editor.SaveEditAsync(path, ".jpg", 100, 80, Encoded(100, 80), colourProfile: true);
        Assert.Equal(profile, await TagAsync(path, "-ICC_Profile:ProfileDescription"));

        // Pixels converted to sRGB don't get the old profile.
        await Editor.SaveEditAsync(path, ".jpg", 100, 80, Encoded(100, 80));
        Assert.Equal("", await TagAsync(path, "-ICC_Profile:ProfileDescription"));
    }

    [ExifToolFact]
    public async Task An_edit_writes_a_new_embedded_thumbnail_of_the_new_pixels()
    {
        var path = _temp.CopySample(".jpg");

        await Editor.SaveEditAsync(path, ".jpg", 320, 100, Encoded(320, 100));

        var file = Path.Combine(_temp.Path, "thumb.jpg");
        await _exifTool!.RunAsync(["-b", "-ThumbnailImage", "-w", "%dthumb.jpg", path]);
        Assert.True(File.Exists(file));
        Assert.Equal((160, 50), TestImages.Size(file));
        Assert.Equal("1", await TagAsync(path, "-IFD0:Orientation#"));
    }

    [ExifToolFact]
    public async Task Lightroom_edits_are_reported_and_removed_by_a_pixel_edit()
    {
        var path = _temp.CopySample(".jpg");
        await _exifTool!.RunAsync(["-overwrite_original", "-XMP-crs:Exposure2012=+0.50", "-XMP-crs:HasSettings=True", "-XMP-crs:CropTop=0.1", path]);
        await Metadata.WriteAsync(path, new MetadataChanges { Rating = 3 });
        Assert.True((await Editor.InspectAsync(path, ".jpg")).RemovesLightroomEdits);

        await Editor.SaveEditAsync(path, ".jpg", 100, 80, Encoded(100, 80));

        Assert.Equal("", await TagAsync(path, "-XMP-crs:all"));
        Assert.Equal(3, (await Metadata.ReadAsync(path)).Rating);
        Assert.False((await Editor.InspectAsync(path, ".jpg")).RemovesLightroomEdits);
    }
}
