using PhotoGallery.Core.ExifTool;
using PhotoGallery.Core.Faces;
using PhotoGallery.Core.Metadata;

namespace PhotoGallery.Core.Tests;

public sealed class FaceRegionsTests : IAsyncLifetime
{
    private ExifToolProcess? _exifTool;
    private readonly TempFolder _temp = new();

    private PhotoMetadataService Service => new(_exifTool!);

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

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void Turning_a_box_upright_and_back_gives_the_same_box(int orientation)
    {
        var box = new FaceBox(0.1, 0.2, 0.3, 0.15);
        AssertClose(box, box.ToUpright(orientation).FromUpright(orientation));
    }

    [Fact]
    public void A_face_at_the_top_left_of_the_stored_pixels_is_top_right_when_turned_clockwise()
    {
        // Orientation 6: the camera was held so the photo has to be turned 90° clockwise to stand upright.
        AssertClose(new FaceBox(0.8, 0, 0.2, 0.1), new FaceBox(0, 0, 0.1, 0.2).ToUpright(6));
        AssertClose(new FaceBox(0, 0.9, 0.2, 0.1), new FaceBox(0, 0, 0.1, 0.2).ToUpright(8));
    }

    [Fact]
    public void Cropping_moves_faces_and_drops_the_ones_mostly_cut_off()
    {
        var crop = new FaceBox(0.5, 0, 0.5, 1);
        AssertClose(new FaceBox(0.2, 0.1, 0.2, 0.1), new FaceBox(0.6, 0.1, 0.1, 0.1).AfterCrop(crop)!.Value);
        Assert.Null(new FaceBox(0.4, 0.1, 0.15, 0.1).AfterCrop(crop));
    }

    [Theory]
    [InlineData("a,b", "a|,b")]
    [InlineData("x=[1]{2}|", "x|=|[1|]|{2|}||")]
    [InlineData(" lead", "| lead")]
    public void Escapes_values_for_exiftool(string value, string expected) => Assert.Equal(expected, FaceRegions.Escape(value));

    [ExifToolTheory]
    [InlineData(".jpg")]
    [InlineData(".heic")]
    [InlineData(".png")]
    [InlineData(".tiff")]
    public async Task Writes_and_reads_back_people(string extension)
    {
        var path = _temp.CopySample(extension);
        PersonTag[] people = [new("Anna Müller", new FaceBox(0.1, 0.2, 0.15, 0.2)), new("Smith, John", new FaceBox(0.6, 0.25, 0.1, 0.12))];

        await Service.WriteAsync(path, new MetadataChanges { People = people });

        var m = await Service.ReadAsync(path);
        Assert.Equal(people.Select(p => p.Name), m.People.Select(p => p.Name));
        for (var i = 0; i < people.Length; i++)
            AssertClose(people[i].Box, m.People[i].Box);
        Assert.Empty(m.OtherRegions);
        Assert.Contains("Smith, John", await Raw(path, "-XMP-MP:RegionPersonDisplayName"));
    }

    [ExifToolFact]
    public async Task Keeps_other_programs_regions_and_replaces_an_unnamed_face_that_gets_a_name()
    {
        var path = _temp.CopySample(".jpg");
        await _exifTool!.RunAsync(["-overwrite_original",
            "-XMP-mwg-rs:RegionInfo={RegionList=[{Area={X=0.5,Y=0.5,W=0.2,H=0.2,Unit=normalized},Type=Focus}," +
            "{Area={X=0.2,Y=0.2,W=0.1,H=0.1,Unit=normalized},Type=Face},{Area={X=0.8,Y=0.8,W=0.1,H=0.1,Unit=normalized},Type=Face}]}", path]);
        var before = await Service.ReadAsync(path);
        Assert.Empty(before.People);
        Assert.Equal(3, before.OtherRegions.Count);

        await Service.WriteAsync(path, new MetadataChanges { People = [new("Anna", new FaceBox(0.15, 0.15, 0.1, 0.1))] });

        var after = await Service.ReadAsync(path);
        Assert.Equal("Anna", Assert.Single(after.People).Name);
        Assert.Equal(2, after.OtherRegions.Count);
        Assert.Contains(after.OtherRegions, r => r.Value.Contains("Type=Focus"));
        Assert.Contains(after.OtherRegions, r => r.UnnamedFace is { X: > 0.7 });
    }

    [ExifToolFact]
    public async Task Reads_people_tagged_in_windows_live_photo_gallery_on_a_turned_photo()
    {
        var path = _temp.CopySample(".jpg");
        // Windows' rectangle is measured on the upright photo: here the top right corner of a photo turned clockwise.
        await _exifTool!.RunAsync(["-overwrite_original", "-IFD0:Orientation#=6",
            "-XMP-MP:RegionInfoMP={Regions=[{PersonDisplayName=Oma,Rectangle=0.8|, 0|, 0.2|, 0.1},{PersonDisplayName=Opa}]}", path]);

        var m = await Service.ReadAsync(path);
        Assert.Equal(["Oma", "Opa"], m.People.Select(p => p.Name));
        AssertClose(new FaceBox(0, 0, 0.1, 0.2), m.People[0].Box);
        AssertClose(new FaceBox(0, 0, 1, 1), m.People[1].Box);
    }

    [ExifToolFact]
    public async Task Rotating_keeps_the_people_on_their_faces()
    {
        var path = _temp.CopySample(".jpg");
        await Service.WriteAsync(path, new MetadataChanges { People = [new("Anna", new FaceBox(0, 0, 0.1, 0.2))] });

        await Service.WriteAsync(path, new MetadataChanges { Orientation = 6 });

        var m = await Service.ReadAsync(path);
        AssertClose(new FaceBox(0, 0, 0.1, 0.2), Assert.Single(m.People).Box);
        // Windows' copy now describes the turned photo.
        Assert.Contains("0.8, 0, 0.2, 0.1", await Raw(path, "-XMP-MP:RegionRectangle"));
    }

    [ExifToolFact]
    public async Task Removing_everyone_clears_the_people_tags()
    {
        var path = _temp.CopySample(".jpg");
        await Service.WriteAsync(path, new MetadataChanges { People = [new("Anna", new FaceBox(0, 0, 0.1, 0.2))] });

        await Service.WriteAsync(path, new MetadataChanges { People = [] });

        Assert.Empty((await Service.ReadAsync(path)).People);
        Assert.DoesNotContain("Anna", await Raw(path, "-XMP:all"));
    }

    private async Task<string> Raw(string path, string tag) => (await _exifTool!.RunAsync(["-s3", "-n", tag, path])).Output;

    private static void AssertClose(FaceBox expected, FaceBox actual)
    {
        Assert.Equal(expected.X, actual.X, 4);
        Assert.Equal(expected.Y, actual.Y, 4);
        Assert.Equal(expected.Width, actual.Width, 4);
        Assert.Equal(expected.Height, actual.Height, 4);
    }
}
