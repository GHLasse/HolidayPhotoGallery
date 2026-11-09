using PhotoGallery.Core.Duplicates;
using PhotoGallery.Core.Library;
using PhotoGallery.Core.Metadata;
using PhotoGallery.Core.Editing;

namespace PhotoGallery.Core.Tests;

public sealed class DuplicateFinderTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    // A picture with some structure: a diagonal gradient with a bright block, or (other) bands across it.
    private string Picture(string name, int width, int quality, bool other = false, double light = 1)
    {
        var bgra = new byte[400 * 300 * 4];
        for (var y = 0; y < 300; y++)
            for (var x = 0; x < 400; x++)
            {
                var v = other ? (byte)((y / 30) % 2 == 0 ? 40 : 220) : (byte)((x + y) * 255 / 700);
                var white = x is > 120 and < 220 && y is > 80 and < 160 && !other;
                var i = (y * 400 + x) * 4;
                (bgra[i], bgra[i + 1], bgra[i + 2], bgra[i + 3]) = white ? ((byte)250, (byte)250, (byte)250, (byte)255) : ((byte)128, (byte)(255 - v), v, (byte)255);
            }
        if (light != 1)
            for (var i = 0; i < bgra.Length; i++)
                if (i % 4 != 3)
                    bgra[i] = (byte)Math.Clamp(bgra[i] * light, 0, 255);
        var height = width * 3 / 4;
        var resized = ImageMath.Resize(bgra, 400, 300, width, height);
        var path = Path.Combine(_temp.Path, name);
        File.WriteAllBytes(path, TestImages.Jpeg(resized, width, height, quality));
        return path;
    }

    // In the app Windows decodes the photo at PictureHash.DecodeSize; here SkiaSharp stands in.
    private static ulong? Hash(string path)
    {
        if (TestImages.Decode(path) is not { } image)
            return null;
        var scale = (double)PictureHash.DecodeSize / Math.Max(image.Width, image.Height);
        var (w, h) = (Math.Max(1, (int)(image.Width * scale)), Math.Max(1, (int)(image.Height * scale)));
        return PictureHash.FromPixels(ImageMath.Resize(image.Pixels, image.Width, image.Height, w, h), w, h);
    }

    private static ulong? Look(string path) =>
        TestImages.Decode(path) is { } image ? PictureHash.LookFromPixels(image.Pixels, image.Width, image.Height) : null;

    private static IndexedPhoto Indexed(string path, DateTime? taken = null, int tags = 0)
    {
        var image = TestImages.Size(path);
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
        var original = Hash(Picture("a.jpg", 400, 95))!.Value;
        var copy = Hash(Picture("a-small.jpg", 160, 40))!.Value;
        var other = Hash(Picture("b.jpg", 400, 95, other: true))!.Value;

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

        var groups = DuplicateFinder.Find(photos, p => Hash(p.Path));

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

        Assert.Empty(DuplicateFinder.Find([a, b], p => Hash(p.Path)));
    }

    [Theory]
    [InlineData("IMG_1166.JPG", "IMG_1167.JPG", true)]
    [InlineData("DSC01234.jpg", "DSC01235.jpg", true)]
    [InlineData("PXL_20240501_100000123.jpg", "PXL_20240501_100000456.jpg", true)]
    [InlineData("IMG_1166.JPG", "IMG_1166.HEIC", false)]
    [InlineData("IMG_1166.JPG", "IMG_1166 (1).JPG", false)]
    [InlineData("IMG_1166.JPG", "IMG_1167-edited.JPG", false)]
    [InlineData("IMG_1166.JPG", "DSC01167.JPG", false)]
    [InlineData("IMG-20200121-WA0001.jpg", "IMG-20200121-WA0002.jpg", false)]
    [InlineData("holiday.jpg", "beach.jpg", false)]
    public void Knows_two_shots_the_camera_numbered_itself(string a, string b, bool separate) =>
        Assert.Equal(separate, DuplicateFinder.SeparateShots(Path.Combine("x", a), Path.Combine("y", b)));

    [Fact]
    public void Burst_shots_taken_in_the_same_second_are_not_duplicates()
    {
        var taken = new DateTime(2020, 1, 21, 15, 56, 0);
        var first = Indexed(Picture("IMG_1166.JPG", 400, 95), taken) with { Camera = "Apple iPhone X" };
        var second = Indexed(Picture("IMG_1167.JPG", 400, 90), taken) with { Camera = "Apple iPhone X" };

        // Even with the very same fingerprint the camera's own numbering keeps them apart ...
        Assert.Empty(DuplicateFinder.Find([first, second], _ => 0xFF00UL));

        // ... and without it, two full-size originals from one camera must be all but identical.
        var a = first with { Path = Path.Combine(_temp.Path, "a.jpg") };
        var b = second with { Path = Path.Combine(_temp.Path, "b.jpg") };
        Assert.Empty(DuplicateFinder.Find([a, b], p => p == a ? 0xFF00UL : 0xFF07UL));
        Assert.Single(DuplicateFinder.Find([a, b], p => p == a ? 0xFF00UL : 0xFF01UL));
    }

    [Fact]
    public void A_copy_saved_again_at_full_size_is_still_found()
    {
        var taken = new DateTime(2020, 1, 21, 15, 56, 0);
        var original = Indexed(Picture("IMG_1166.JPG", 400, 95), taken) with { Camera = "Apple iPhone X" };
        var copy = Indexed(Picture("IMG_1166 (1).JPG", 400, 60), taken) with { Camera = "Apple iPhone X" };

        var group = Assert.Single(DuplicateFinder.Find([original, copy], p => Hash(p.Path)));
        Assert.False(group.Exact);
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
    public void Fingerprints_from_an_older_index_are_taken_again()
    {
        using var data = new TempFolder();
        var file = Path.Combine(data.Path, "index.db");
        var path = Picture("a.jpg", 400, 95);
        using (var index = new PhotoIndex(file))
        {
            index.Upsert([Indexed(path)]);
            index.SavePictureHashes([(path, 1UL)]);
        }
        using (var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={file};Pooling=False"))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "PRAGMA user_version = 7;";
            cmd.ExecuteNonQuery();
        }
        using (var index = new PhotoIndex(file))
        {
            Assert.Empty(index.GetPictureHashes());
            Assert.Single(index.Search(new PhotoQuery()));
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

    [Fact]
    public void Photos_that_are_each_a_bit_like_a_third_are_not_chained_into_one_group()
    {
        // A dark and a bright shot of the same dunes, days apart, and an undated picture between them: each is a
        // few bits from the middle one but too far from each other.
        var dark = Indexed(Picture("dark.jpg", 400, 95), new DateTime(2015, 8, 25, 21, 44, 0));
        var bright = Indexed(Picture("bright.jpg", 400, 80), new DateTime(2015, 8, 28, 18, 11, 0));
        var undated = Indexed(Picture("received.jpg", 200, 60));
        const ulong Middle = 0x0F0F_3C3C_F0F0_A5A5UL;
        ulong Fingerprint(IndexedPhoto p) => p == dark ? Middle ^ 0b111 : p == bright ? Middle ^ 0b111000 : Middle;

        var group = Assert.Single(DuplicateFinder.Find([dark, bright, undated], p => Fingerprint(p)));
        Assert.Equal(2, group.Photos.Count);
        Assert.Contains(undated, group.Photos);
    }

    [Fact]
    public void A_dark_and_a_bright_picture_of_the_same_view_are_not_duplicates_but_a_smaller_copy_is()
    {
        var photo = Picture("view.jpg", 400, 95);
        var dark = Picture("view dark.jpg", 400, 95, light: 0.35);
        var copy = Picture("view small.jpg", 200, 50);
        IndexedPhoto[] photos = [Indexed(photo), Indexed(dark), Indexed(copy)];

        Assert.True(PictureHash.Distance(Hash(photo)!.Value, Hash(dark)!.Value) <= DuplicateFinder.MaxDistance);
        Assert.False(PictureHash.LooksAlike(Look(photo)!.Value, Look(dark)!.Value));
        Assert.True(PictureHash.LooksAlike(Look(photo)!.Value, Look(copy)!.Value));

        var group = Assert.Single(DuplicateFinder.Find(photos, p => Hash(p.Path), look: p => Look(p.Path)));
        Assert.Equal(new[] { photo, copy }.Order(), group.Photos.Select(p => p.Path).Order());
    }

    [Fact]
    public void Pictures_with_hardly_any_detail_are_only_found_as_exact_copies()
    {
        var a = Indexed(Picture("night1.jpg", 400, 95));
        var b = Indexed(Picture("night2.jpg", 200, 70));

        Assert.True(PictureHash.Plain(0));
        Assert.Empty(DuplicateFinder.Find([a, b], _ => 0b1001UL));
        Assert.Empty(DuplicateFinder.Find([a, b], _ => 0xFF00UL, look: p => p == a ? 0x0000_0000_0280_8080UL : 0x0000_0000_0280_8080UL));
    }

    [Fact]
    public void The_index_keeps_looks_and_takes_fingerprints_without_one_again()
    {
        using var data = new TempFolder();
        var file = Path.Combine(data.Path, "index.db");
        var path = Picture("a.jpg", 400, 95);
        using (var index = new PhotoIndex(file))
        {
            index.Upsert([Indexed(path)]);
            index.SavePictureHashes([(path, (ulong?)1UL, (ulong?)0xABCDUL)]);
            Assert.Equal(0xABCDUL, index.GetPictureLooks()[path]);
        }
        using (var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={file};Pooling=False"))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "ALTER TABLE picture_hashes DROP COLUMN look;";
            cmd.ExecuteNonQuery();
        }
        using (var index = new PhotoIndex(file))
        {
            Assert.Empty(index.GetPictureHashes());
            Assert.Equal([path], index.GetNotPictureHashed(10));
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }
}
