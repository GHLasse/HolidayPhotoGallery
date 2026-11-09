using PhotoGallery.Core.ExifTool;
using PhotoGallery.Core.Library;
using PhotoGallery.Core.Metadata;

namespace PhotoGallery.Core.Tests;

/// <summary>L-9: 360° photos are found, indexed and placed on the sphere.</summary>
public sealed class PanoramaTests : IAsyncLifetime
{
    private ExifToolProcess? _exifTool;
    private readonly TempFolder _temp = new();

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
    [InlineData("equirectangular", true)]
    [InlineData("Equirectangular ", true)]
    [InlineData("cylindrical", false)]
    [InlineData(null, false)]
    public void Only_a_sphere_counts(string? projection, bool expected) => Assert.Equal(expected, Panorama.IsSphere(projection));

    [Fact]
    public void A_whole_sphere_wraps_around()
    {
        var layout = Panorama.ParseLayout("""[{"XMP-GPano:ProjectionType":"equirectangular"}]""");
        Assert.Equal(PanoramaLayout.Sphere, layout);
        Assert.True(layout.WrapsAround);
    }

    [Fact]
    public void A_partial_sphere_is_placed_where_it_was_taken()
    {
        var layout = Panorama.ParseLayout("""
            [{"XMP-GPano:FullPanoWidthPixels":8000,"XMP-GPano:FullPanoHeightPixels":4000,
              "XMP-GPano:CroppedAreaLeftPixels":2000,"XMP-GPano:CroppedAreaTopPixels":1000,
              "XMP-GPano:CroppedAreaImageWidthPixels":4000,"XMP-GPano:CroppedAreaImageHeightPixels":2000,
              "XMP-GPano:InitialViewHeadingDegrees":100,"XMP-GPano:PoseHeadingDegrees":70}]
            """);
        Assert.Equal(new PanoramaLayout(0.25, 0.25, 0.5, 0.5, 30), layout);
        Assert.False(layout.WrapsAround);
    }

    [Fact]
    public void Nonsense_gives_the_whole_sphere() => Assert.Equal(PanoramaLayout.Sphere, Panorama.ParseLayout("not json"));

    [Fact]
    public void The_index_keeps_the_mark()
    {
        using var data = new TempFolder();
        using var index = new PhotoIndex(Path.Combine(data.Path, "index.db"));
        var sphere = Path.Combine(data.Path, "sphere.jpg");
        var flat = Path.Combine(data.Path, "flat.jpg");
        index.Upsert([new IndexedPhoto { Path = sphere, Folder = data.Path, IsPanorama = true }, new IndexedPhoto { Path = flat, Folder = data.Path }]);
        Assert.True(index.GetPhoto(sphere)!.IsPanorama);
        Assert.False(index.GetPhoto(flat)!.IsPanorama);
    }

    [ExifToolFact]
    public async Task A_photo_sphere_is_read_as_one()
    {
        var service = new PhotoMetadataService(_exifTool!);
        var photo = _temp.CopySample(".jpg", "sphere");
        Assert.False((await service.ReadAsync(photo)).IsPanorama);

        await _exifTool!.RunAsync(["-overwrite_original", "-XMP-GPano:ProjectionType=equirectangular", "-XMP-GPano:UsePanoramaViewer=True",
                                   "-XMP-GPano:FullPanoWidthPixels=4000", "-XMP-GPano:FullPanoHeightPixels=2000",
                                   "-XMP-GPano:CroppedAreaImageWidthPixels=4000", "-XMP-GPano:CroppedAreaImageHeightPixels=1000",
                                   "-XMP-GPano:CroppedAreaLeftPixels=0", "-XMP-GPano:CroppedAreaTopPixels=500", photo]);
        Assert.True((await service.ReadAsync(photo)).IsPanorama);
        Assert.Equal(new PanoramaLayout(0, 0.25, 1, 0.5), await Panorama.ReadLayoutAsync(_exifTool, photo));
    }
}
