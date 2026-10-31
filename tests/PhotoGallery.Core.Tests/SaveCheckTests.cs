using PhotoGallery.Core.ExifTool;
using PhotoGallery.Core.Faces;
using PhotoGallery.Core.Library;
using PhotoGallery.Core.Metadata;

namespace PhotoGallery.Core.Tests;

public sealed class SaveCheckTests : IAsyncLifetime
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

    private static IndexedPhoto Basis(params string[] tags) =>
        new() { Path = "/p/a.jpg", Folder = "/p", Tags = tags, Rating = 2, Caption = "Old" };

    private static PhotoMetadata Theirs(params string[] tags) =>
        new() { Path = "/p/a.jpg", Tags = tags, Rating = 2, Caption = "Old" };

    [Fact]
    public void Tags_keep_both_sides_changes()
    {
        // We added "Sea" and removed "Rain"; they added "Venice" and removed "Boat".
        var merged = SaveCheck.Merge(Basis("Italy", "Rain", "Boat"), Theirs("Italy", "Rain", "Venice"),
                                     new MetadataChanges { Tags = ["Italy", "Boat", "Sea"] }, out var clashes);
        Assert.Equal(["Italy", "Venice", "Sea"], merged.Tags);
        Assert.Empty(clashes);
    }

    [Fact]
    public void A_field_only_they_changed_is_no_clash_and_one_both_changed_is()
    {
        var theirs = Theirs() with { Rating = 4, Caption = "Their caption" };
        SaveCheck.Merge(Basis(), theirs, new MetadataChanges { Rating = 5 }, out var clashes);
        Assert.Equal(["rating"], clashes);
        SaveCheck.Merge(Basis(), theirs, new MetadataChanges { Rating = 4 }, out clashes); // same value: fine
        Assert.Empty(clashes);
        SaveCheck.Merge(Basis(), theirs, new MetadataChanges { Flagged = true }, out clashes); // other field
        Assert.Empty(clashes);
        SaveCheck.Merge(Basis(), theirs, new MetadataChanges { Caption = "Mine" }, out clashes);
        Assert.Equal(["caption"], clashes);
    }

    [Fact]
    public void People_they_added_stay_and_people_they_removed_go()
    {
        var anna = new PersonTag("Anna", new FaceBox(0.1, 0.1, 0.2, 0.2));
        var ben = new PersonTag("Ben", new FaceBox(0.5, 0.1, 0.2, 0.2));
        var cleo = new PersonTag("Cleo", new FaceBox(0.3, 0.5, 0.2, 0.2));
        var dan = new PersonTag("Dan", new FaceBox(0.7, 0.5, 0.2, 0.2));
        var basis = Basis() with { People = [anna, ben] };
        var theirs = Theirs() with { People = [anna, cleo] };          // they removed Ben, added Cleo
        var merged = SaveCheck.Merge(basis, theirs, new MetadataChanges { People = [anna, ben, dan] }, out _); // we added Dan
        Assert.Equal(["Anna", "Dan", "Cleo"], merged.People!.Select(p => p.Name));
    }

    [Fact]
    public void Notices_a_file_changed_since_it_was_read()
    {
        var path = _temp.CopySample(".jpg");
        var file = new FileInfo(path);
        var basis = new IndexedPhoto { Path = path, Folder = _temp.Path, FileSize = file.Length, FileModifiedUtc = file.LastWriteTimeUtc };
        Assert.False(SaveCheck.ChangedSince(basis, file));
        File.SetLastWriteTimeUtc(path, file.LastWriteTimeUtc.AddMinutes(5));
        Assert.True(SaveCheck.ChangedSince(basis, file));
    }

    [ExifToolFact]
    public async Task A_save_merges_with_what_another_program_wrote_meanwhile()
    {
        var path = _temp.CopySample(".jpg");
        var service = new PhotoMetadataService(_exifTool!);
        await service.WriteAsync(path, new MetadataChanges { Tags = ["Italy"], Rating = 2 });
        var read = await service.ReadAsync(path);
        var file = new FileInfo(path);
        var basis = new IndexedPhoto
        {
            Path = path, Folder = _temp.Path, FileSize = file.Length, FileModifiedUtc = file.LastWriteTimeUtc,
            Tags = read.Tags, Rating = read.Rating,
        };
        service.BasisFor = p => p == path ? basis : null;
        var reported = new List<string>();
        service.ChangedElsewhere += (_, clashes) => reported.AddRange(clashes);

        // The other PC adds a tag and rates it 4 (and its clock moves the file's time on).
        await _exifTool!.RunAsync(["-overwrite_original", "-XMP-dc:Subject+=Venice", "-XMP-xmp:Rating=4", path]);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));

        // Here the user adds "Sea" and sets 5 stars, from the list the app read before.
        await service.WriteAsync(path, new MetadataChanges { Tags = ["Italy", "Sea"] });
        await service.WriteAsync(path, new MetadataChanges { Rating = 5 });

        var now = await service.ReadAsync(path);
        Assert.Equal(["Italy", "Venice", "Sea"], now.Tags);
        Assert.Equal(5, now.Rating);
        Assert.Equal(["rating"], reported);
    }
}
