using PhotoGallery.Core.Library;
using PhotoGallery.Core.Tagging;

namespace PhotoGallery.Core.Tests;

public sealed class PictureVectorsTests : IDisposable
{
    private readonly TempFolder _data = new();
    private readonly PhotoIndex _index;

    public PictureVectorsTests() => _index = new PhotoIndex(Path.Combine(_data.Path, "index.db"));

    public void Dispose()
    {
        _index.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _data.Dispose();
    }

    private static IndexedPhoto Photo(string name, DateTime modified) =>
        new() { Path = $"/photos/{name}", Folder = "/photos", FileModifiedUtc = modified };

    // A unit vector pointing mostly along one axis, with a little of the next one.
    private static float[] Toward(int axis, float lean = 0, int length = 768)
    {
        var v = new float[length];
        v[axis] = 1;
        v[(axis + 1) % length] = lean;
        var scale = 1 / MathF.Sqrt(1 + lean * lean);
        for (var i = 0; i < length; i++)
            v[i] *= scale;
        return v;
    }

    [Fact]
    public void Bytes_keep_the_numbers_at_half_precision()
    {
        var vector = Enumerable.Range(0, 768).Select(i => MathF.Sin(i) / 20).ToArray();
        var bytes = PictureVectors.ToBytes(vector);
        Assert.Equal(1536, bytes.Length);
        var back = PictureVectors.FromBytes(bytes);
        Assert.Equal(768, back.Length);
        for (var i = 0; i < vector.Length; i++)
            Assert.InRange(back[i] - vector[i], -1e-4f, 1e-4f);
    }

    [Fact]
    public void Closest_ranks_by_closeness_and_honours_the_limits()
    {
        var vectors = new PictureVectors();
        vectors.Set("beach.jpg", Toward(0, 0.1f));
        vectors.Set("beach2.jpg", Toward(0, 0.5f));
        vectors.Set("city.jpg", Toward(5));

        var query = Toward(0);
        Assert.Equal(["beach.jpg", "beach2.jpg", "city.jpg"], vectors.Closest(query, 10).Select(c => c.Path));
        Assert.Equal(["beach.jpg"], vectors.Closest(query, 1).Select(c => c.Path));
        Assert.Equal(["beach.jpg", "beach2.jpg"], vectors.Closest(query, 10, minCloseness: 0.5).Select(c => c.Path));
        var only = new HashSet<string>(["beach2.jpg", "city.jpg"], StringComparer.OrdinalIgnoreCase);
        Assert.Equal(["beach2.jpg", "city.jpg"], vectors.Closest(query, 10, only: only).Select(c => c.Path));
    }

    [Fact]
    public void Removing_a_photo_keeps_the_others()
    {
        var vectors = new PictureVectors();
        vectors.Set("a.jpg", Toward(0));
        vectors.Set("b.jpg", Toward(1));
        vectors.Set("c.jpg", Toward(2));
        vectors.Remove("a.jpg");
        Assert.Equal(2, vectors.Count);
        Assert.False(vectors.Contains("a.jpg"));
        Assert.Equal(1, vectors.Get("c.jpg")![2], 3);
        Assert.Equal("c.jpg", vectors.Closest(Toward(2), 1)[0].Path);

        vectors.Set("b.jpg", Toward(3)); // replaced, not added
        Assert.Equal(2, vectors.Count);
        Assert.Equal("b.jpg", vectors.Closest(Toward(3), 1)[0].Path);
    }

    [Fact]
    public void The_index_keeps_vectors_until_the_picture_changes()
    {
        var then = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        _index.Upsert([Photo("a.jpg", then), Photo("b.jpg", then)]);
        Assert.Equal(2, _index.GetNotPictureVectored(10).Count);

        _index.SavePictureVector("/photos/a.jpg", Toward(7));
        Assert.Equal(["/photos/b.jpg"], _index.GetNotPictureVectored(10));
        Assert.Equal(1, _index.GetPictureVector("/photos/a.jpg")![7], 3);
        var all = _index.GetPictureVectors();
        Assert.Equal(1, all.Count);
        Assert.Equal("/photos/a.jpg", all.Closest(Toward(7), 1)[0].Path);

        // Details written into the file change its time, not the picture: the vector stays.
        _index.Upsert([Photo("a.jpg", then.AddHours(1))]);
        Assert.NotNull(_index.GetPictureVector("/photos/a.jpg"));
        Assert.Equal(1, _index.GetPictureVectors().Count);

        // An edit of the pixels forgets it.
        _index.ForgetPictureVector("/photos/a.jpg");
        Assert.Null(_index.GetPictureVector("/photos/a.jpg"));
        Assert.Equal(0, _index.GetPictureVectors().Count);
        Assert.Equal(2, _index.GetNotPictureVectored(10).Count);
    }
}
