using PhotoGallery.Core.ExifTool;
using PhotoGallery.Core.Faces;
using PhotoGallery.Core.Library;
using PhotoGallery.Core.Metadata;

namespace PhotoGallery.Core.Tests;

public sealed class FaceIndexTests : IAsyncLifetime
{
    private ExifToolProcess? _exifTool;
    private readonly TempFolder _photos = new();
    private readonly TempFolder _data = new();
    private PhotoIndex? _index;

    public Task InitializeAsync()
    {
        _exifTool = ExifToolProcess.TryStart();
        _index = new PhotoIndex(Path.Combine(_data.Path, "index.db"));
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _index?.Dispose();
        if (_exifTool is not null)
            await _exifTool.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _photos.Dispose();
        _data.Dispose();
    }

    private static float[] Vector(params float[] v)
    {
        var length = MathF.Sqrt(v.Sum(x => x * x));
        return v.Select(x => x / length).ToArray();
    }

    [ExifToolFact]
    public async Task Found_faces_take_the_names_in_the_file_and_people_can_be_searched()
    {
        var service = new PhotoMetadataService(_exifTool!);
        var indexer = new LibraryIndexer(_index!, service);
        var path = _photos.CopySample(".jpg", "beach");
        var anna = new FaceBox(0.1, 0.1, 0.2, 0.2);
        var stranger = new FaceBox(0.6, 0.1, 0.2, 0.2);
        await service.WriteAsync(path, new MetadataChanges { People = [new("Anna", new FaceBox(0.11, 0.09, 0.2, 0.21))] });
        await indexer.SyncFolderAsync(_photos.Path);

        Assert.True(_index!.SaveFaces(path, [new(anna, 0.9f, Vector(1, 0, 0), [1, 2, 3]), new(stranger, 0.95f, Vector(0, 1, 0), null)]));

        var faces = _index.GetFaces(path);
        Assert.Equal(["Anna", null], faces.Select(f => f.Person));
        Assert.Equal(Vector(1, 0, 0), faces[0].Embedding);
        Assert.Equal([1, 2, 3], faces[0].Thumbnail);
        Assert.Contains(path, _index.GetFaceScanned([path, "other.jpg"]));
        Assert.Equal("Anna", Assert.Single(Assert.Single(_index.Search(new PhotoQuery { Text = "ann" })).People).Name);
        Assert.Equal([("Anna", 1)], _index.GetPeopleCounts());

        // Naming the second face in the file names it in the index once the file is read again.
        var photo = Assert.Single(_index.Search(new PhotoQuery()));
        await service.WriteAsync(path, new MetadataChanges { People = FaceLinks.Name(photo.People, stranger, "Ben") });
        await indexer.RefreshFileAsync(path);
        Assert.Equal(["Anna", "Ben"], _index.GetFaces(path).Select(f => f.Person));

        _index.RejectName(faces[1].Id, "Carl");
        _index.RejectName(faces[1].Id, "Dora");
        _index.IgnoreFace(faces[0].Id);
        Assert.Equal(["Carl", "Dora"], _index.GetFace(faces[1].Id)!.Rejected);
        Assert.Equal(faces[1].Id, Assert.Single(_index.GetAllFaces()).Id);

        _index.ForgetFaces(path);
        Assert.Empty(_index.GetFaces(path));
        Assert.Empty(_index.GetFaceScanned([path]));
    }

    [Fact]
    public void Suggests_the_most_alike_person_unless_they_were_rejected()
    {
        var matcher = new FaceMatcher([("Anna", Vector(1, 0.1f, 0)), ("Anna", Vector(1, -0.1f, 0)), ("Ben", Vector(0, 1, 0))]);

        Assert.Equal("Anna", matcher.Suggest(Vector(1, 0.05f, 0.1f))?.Name);
        Assert.Equal("Ben", matcher.Suggest(Vector(0.1f, 1, 0))?.Name);
        Assert.Null(matcher.Suggest(Vector(0, 0, 1)));
        Assert.Null(matcher.Suggest(Vector(1, 0.05f, 0.1f), ["anna"]));
    }

    [Fact]
    public void Groups_faces_that_look_alike()
    {
        var groups = FaceMatcher.Group([Vector(1, 0, 0), Vector(0, 1, 0), Vector(1, 0.1f, 0), Vector(0, 0, 1), Vector(1, -0.1f, 0.05f)]);
        Assert.Equal([0, 2, 4], groups[0]);
        Assert.Equal(3, groups.Count);
    }

    [Fact]
    public void Naming_a_face_replaces_whoever_was_on_it()
    {
        var face = new FaceBox(0.1, 0.1, 0.2, 0.2);
        PersonTag[] people = [new("Wrong", face), new("Oma", new FaceBox(0, 0, 1, 1)), new("Opa", new FaceBox(0.6, 0.6, 0.2, 0.2))];

        var named = FaceLinks.Name(people, face, "Oma");
        Assert.Equal(["Opa", "Oma"], named.Select(p => p.Name));
        Assert.Equal(["Opa"], FaceLinks.Unname(named, face).Select(p => p.Name));
        Assert.Null(FaceLinks.PersonFor(new FaceBox(0.3, 0.3, 0.1, 0.1), [new("Oma", new FaceBox(0, 0, 1, 1))]));
    }
}
