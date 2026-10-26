using PhotoGallery.Core.Duplicates;
using PhotoGallery.Core.Library;
using PhotoGallery.Core.Metadata;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PhotoGallery.Core.Tests;

public sealed class DuplicateFinderTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    // A picture with some structure: a diagonal gradient with a bright block, or (other) bands across it.
    private string Picture(string name, int width, int quality, bool other = false)
    {
        using var image = new Image<Rgb24>(400, 300);
        for (var y = 0; y < 300; y++)
            for (var x = 0; x < 400; x++)
            {
                var v = other ? (byte)((y / 30) % 2 == 0 ? 40 : 220) : (byte)((x + y) * 255 / 700);
                image[x, y] = x is > 120 and < 220 && y is > 80 and < 160 && !other ? new Rgb24(250, 250, 250) : new Rgb24(v, (byte)(255 - v), 128);
            }
        image.Mutate(x => x.Resize(width, width * 3 / 4));
        var path = Path.Combine(_temp.Path, name);
        image.SaveAsJpeg(path, new JpegEncoder { Quality = quality });
        return path;
    }

    private static IndexedPhoto Indexed(string path, DateTime? taken = null, int tags = 0)
    {
        var image = Image.Identify(path);
        return new IndexedPhoto
        {
            Path = path,
            Folder = Path.GetDirectoryName(path)!,
            FileSize = new FileInfo(path).Length,
            Width = image.Width,
            Height = image.Height,
            DateTaken = taken is { } t ? new DateTaken(t, null) : null,
            Tags = Enumerable.Range(0, tags).Select(i => $"t{i}").ToList(),
        };
    }

    [Fact]
    public void A_resized_recompressed_copy_has_almost_the_same_fingerprint_and_another_picture_does_not()
    {
        var original = PictureHash.FromFile(Picture("a.jpg", 400, 95))!.Value;
        var copy = PictureHash.FromFile(Picture("a-small.jpg", 160, 40))!.Value;
        var other = PictureHash.FromFile(Picture("b.jpg", 400, 95, other: true))!.Value;

        Assert.True(PictureHash.Distance(original, copy) <= DuplicateFinder.MaxDistance);
        Assert.True(PictureHash.Distance(original, other) > 16);
    }

    [Fact]
    public void Finds_exact_copies_and_near_duplicates_and_suggests_the_largest_copy()
    {
        var taken = new DateTime(2024, 5, 1, 10, 0, 0);
        var big = Picture("big.jpg", 400, 95);
        var exact = Path.Combine(_temp.Path, "exact copy.jpg");
        File.Copy(big, exact);
        var messenger = Picture("IMG-WA0001.jpg", 200, 50);
        var other = Picture("other.jpg", 400, 95, other: true);
        IndexedPhoto[] photos = [Indexed(messenger, null, tags: 5), Indexed(big, taken), Indexed(exact, taken), Indexed(other, taken)];

        var groups = DuplicateFinder.Find(photos, p => PictureHash.FromFile(p.Path));

        var group = Assert.Single(groups);
        Assert.Equal(3, group.Photos.Count);
        Assert.False(group.Exact);
        Assert.Equal(400, group.Best.Width);

        var onlyExact = DuplicateFinder.Find([Indexed(big, taken), Indexed(exact, taken)], _ => null);
        Assert.True(Assert.Single(onlyExact).Exact);
    }

    [Fact]
    public void Photos_taken_at_different_moments_are_not_duplicates()
    {
        var a = Indexed(Picture("burst1.jpg", 400, 95), new DateTime(2024, 5, 1, 10, 0, 0));
        var b = Indexed(Picture("burst2.jpg", 400, 90), new DateTime(2024, 5, 1, 10, 0, 5));

        Assert.Empty(DuplicateFinder.Find([a, b], p => PictureHash.FromFile(p.Path)));
    }

    [Fact]
    public void The_index_keeps_fingerprints_until_the_file_changes()
    {
        using var data = new TempFolder();
        var path = Picture("a.jpg", 400, 95);
        using (var index = new PhotoIndex(Path.Combine(data.Path, "index.db")))
        {
            index.Upsert([Indexed(path)]);
            index.SavePictureHashes([(path, 0xF0F0_0000_0000_0001UL)]);
            Assert.Equal(0xF0F0_0000_0000_0001UL, index.GetPictureHashes()[path]);

            index.Upsert([Indexed(path) with { FileModifiedUtc = DateTime.UtcNow }]);
            Assert.Empty(index.GetPictureHashes());
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [Fact]
    public void The_background_search_finds_photos_without_a_current_fingerprint()
    {
        using var data = new TempFolder();
        var a = Picture("a.jpg", 400, 95);
        var b = Picture("b.jpg", 400, 90);
        using (var index = new PhotoIndex(Path.Combine(data.Path, "index.db")))
        {
            index.Upsert([Indexed(a), Indexed(b)]);
            Assert.Equal(2, index.GetNotPictureHashed(10).Count);

            index.SavePictureHashes([(a, 1UL), (b, null)]);
            Assert.Empty(index.GetNotPictureHashed(10));

            index.Upsert([Indexed(b) with { FileModifiedUtc = DateTime.UtcNow }]);
            Assert.Equal([b], index.GetNotPictureHashed(10));
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [Fact]
    public void A_group_kept_whole_is_remembered_until_another_copy_joins_it()
    {
        using var data = new TempFolder();
        var a = Indexed(Picture("a.jpg", 400, 95));
        var b = Indexed(Picture("b.jpg", 200, 80));
        var c = Indexed(Picture("c.jpg", 300, 85));
        var pair = new DuplicateGroup([a, b], Exact: false);
        using (var index = new PhotoIndex(Path.Combine(data.Path, "index.db")))
        {
            index.KeepDuplicates(pair.Key);
            index.KeepDuplicates(pair.Key);
            var kept = index.GetKeptDuplicates();
            Assert.Contains(new DuplicateGroup([b, a], Exact: false).Key, kept);
            Assert.DoesNotContain(new DuplicateGroup([a, b, c], Exact: false).Key, kept);
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }
}
