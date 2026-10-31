using PhotoGallery.Core.Culling;
using PhotoGallery.Core.Faces;
using PhotoGallery.Core.Library;
using PhotoGallery.Core.Metadata;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PhotoGallery.Core.Tests;

public sealed class ShotQualityTests : IDisposable
{
    private readonly TempFolder _data = new();
    private readonly PhotoIndex _index;

    public ShotQualityTests() => _index = new PhotoIndex(Path.Combine(_data.Path, "index.db"));

    public void Dispose()
    {
        _index.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _data.Dispose();
    }

    // Fine random detail, like leaves or gravel, around a mid grey.
    private static Image<L8> Detailed(int width = 800, int height = 600, int seed = 1, int around = 128, int spread = 60)
    {
        var random = new Random(seed);
        var image = new Image<L8>(width, height);
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                image[x, y] = new L8((byte)Math.Clamp(around + random.Next(-spread, spread), 0, 255));
        return image;
    }

    [Fact]
    public void A_sharp_photo_gets_no_hint_and_a_blurred_copy_is_possibly_blurry()
    {
        using var sharp = Detailed();
        var measure = ShotQuality.Measure(sharp, []);
        Assert.Equal(QualityHint.None, ShotQuality.Hints(measure));

        using var blurred = sharp.Clone(x => x.GaussianBlur(4));
        var soft = ShotQuality.Measure(blurred, []);
        Assert.True(soft.Sharpness < measure.Sharpness / 10);
        Assert.Equal(QualityHint.Blurry, ShotQuality.Hints(soft));
    }

    [Fact]
    public void A_soft_background_is_fine_when_one_part_is_sharp()
    {
        using var image = Detailed();
        image.Mutate(x => x.GaussianBlur(6));
        using var subject = Detailed(200, 200, seed: 2);
        image.Mutate(x => x.DrawImage(subject, new Point(300, 200), 1f));
        Assert.Equal(QualityHint.None, ShotQuality.Hints(ShotQuality.Measure(image, [])));
    }

    [Fact]
    public void With_faces_only_the_faces_count()
    {
        using var image = Detailed();
        // A blurred face on a sharp scene: the photo is possibly blurry where it matters.
        var face = new FaceBox(0.4, 0.3, 0.2, 0.25);
        image.Mutate(x => x.GaussianBlur(5, new Rectangle(280, 140, 240, 230)));
        var measure = ShotQuality.Measure(image, [face]);
        Assert.NotNull(measure.FaceSharpness);
        Assert.True(measure.Sharpness > ShotQuality.BlurryBelow);
        Assert.Equal(QualityHint.Blurry, ShotQuality.Hints(measure));
    }

    [Fact]
    public void Nearly_black_and_nearly_white_photos_are_too_dark_or_too_bright_not_blurry()
    {
        using var dark = Detailed(around: 6, spread: 6);
        Assert.Equal(QualityHint.TooDark, ShotQuality.Hints(ShotQuality.Measure(dark, [])));
        using var bright = Detailed(around: 250, spread: 5);
        Assert.Equal(QualityHint.TooBright, ShotQuality.Hints(ShotQuality.Measure(bright, [])));
        // A night scene with a lit part stays without a hint.
        using var night = Detailed(around: 8, spread: 6);
        using var lit = Detailed(300, 300, seed: 3);
        night.Mutate(x => x.DrawImage(lit, new Point(250, 150), 1f));
        Assert.Equal(QualityHint.None, ShotQuality.Hints(ShotQuality.Measure(night, [])));
    }

    [Fact]
    public void Large_photos_are_measured_at_the_same_size()
    {
        using var large = Detailed(2048, 1536);
        using var medium = large.Clone(x => x.Resize(ShotQuality.Size, ShotQuality.Size * 3 / 4));
        var a = ShotQuality.Measure(large, []);
        var b = ShotQuality.Measure(medium, []);
        Assert.InRange(a.Sharpness, b.Sharpness * 0.9, b.Sharpness * 1.1);
    }

    [Fact]
    public void Reads_a_file_and_returns_null_for_what_it_cannot_read()
    {
        var path = Path.Combine(_data.Path, "shot.png");
        using (var image = Detailed(400, 300))
            image.SaveAsPng(path);
        Assert.NotNull(ShotQuality.FromFile(path, []));
        var bad = Path.Combine(_data.Path, "bad.jpg");
        File.WriteAllText(bad, "not a picture");
        Assert.Null(ShotQuality.FromFile(bad, []));
    }

    private static IndexedPhoto Photo(string name, DateTime? taken, int rating = 0, bool rejected = false) =>
        new()
        {
            Path = $"/photos/{name}", Folder = "/photos", FileModifiedUtc = new DateTime(2026, 1, 1),
            DateTaken = taken is { } t ? new DateTaken(t, null) : null, Rating = rating, Rejected = rejected,
        };

    [Fact]
    public void Similar_shots_are_runs_of_alike_photos_taken_close_together()
    {
        var t = new DateTime(2026, 5, 1, 12, 0, 0);
        var photos = new[]
        {
            Photo("a1.jpg", t), Photo("a2.jpg", t.AddSeconds(1)), Photo("a3.jpg", t.AddSeconds(3)),
            Photo("b1.jpg", t.AddSeconds(20)),                               // looks different
            Photo("c1.jpg", t.AddMinutes(10)), Photo("c2.jpg", t.AddMinutes(11)),
            Photo("c3.jpg", t.AddMinutes(20)),                               // alike, but 9 minutes later
            Photo("d1.jpg", null), Photo("d2.jpg", null),                      // no date
            Photo("e1.jpg", t.AddHours(1)), Photo("e2.jpg", t.AddHours(1).AddSeconds(2), rejected: true),
        };
        static bool Alike(IndexedPhoto a, IndexedPhoto b) => a.FileName[0] == b.FileName[0];
        var groups = SimilarShots.Find(photos.Reverse(), Alike);
        Assert.Equal([["a1.jpg", "a2.jpg", "a3.jpg"], ["c1.jpg", "c2.jpg"]],
                     groups.Select(g => g.Photos.Select(p => p.FileName).ToArray()).ToArray());
    }

    [Fact]
    public void The_best_of_a_group_is_the_sharpest_else_the_largest()
    {
        var t = new DateTime(2026, 5, 1, 12, 0, 0);
        var small = Photo("small.jpg", t) with { Width = 100, Height = 100 };
        var large = Photo("large.jpg", t) with { Width = 4000, Height = 3000 };
        var group = new ShotGroup([small, large]);
        Assert.Equal(large, group.Best(_ => null));
        Assert.Equal(small, group.Best(p => p == small ? 200 : 50));
    }

    [Fact]
    public void Alike_uses_vectors_first_then_fingerprints()
    {
        float[] a = [1, 0, 0], b = [0.95f, 0.1f, 0], c = [0, 1, 0];
        Assert.True(SimilarShots.Alike(a, b, null, null));
        Assert.False(SimilarShots.Alike(a, c, 0, 0)); // vectors win over equal fingerprints
        Assert.True(SimilarShots.Alike(null, b, 0b1111UL, 0UL));
        Assert.False(SimilarShots.Alike(null, null, ulong.MaxValue, 0UL));
        Assert.False(SimilarShots.Alike(null, null, null, 0UL));
    }

    [Fact]
    public void A_burst_counts_as_alike_with_a_looser_test()
    {
        float[] a = [1, 0], b = [0.75f, 0.66f]; // about 0.75 alike
        Assert.False(SimilarShots.Alike(a, b, null, null, TimeSpan.FromMinutes(2)));
        Assert.True(SimilarShots.Alike(a, b, null, null, TimeSpan.FromSeconds(14)));
        const ulong twentyBits = (1UL << 20) - 1;
        Assert.False(SimilarShots.Alike(null, null, twentyBits, 0UL, TimeSpan.FromMinutes(2)));
        Assert.True(SimilarShots.Alike(null, null, twentyBits, 0UL, TimeSpan.FromSeconds(14)));
    }

    [Fact]
    public void Hints_are_kept_shown_until_judged_or_ignored_and_survive_detail_changes()
    {
        var t = new DateTime(2026, 5, 1, 12, 0, 0);
        _index.Upsert([Photo("blurry.jpg", t), Photo("fine.jpg", t), Photo("rated.jpg", t, rating: 3)]);
        var soft = new QualityMeasure(5, null, 120, 0, 0);
        var fine = new QualityMeasure(400, null, 120, 0, 0);
        _index.SaveShotQuality([("/photos/blurry.jpg", soft), ("/photos/fine.jpg", fine), ("/photos/rated.jpg", soft)]);

        Assert.Equal(["blurry.jpg"], _index.Search(new PhotoQuery { QualityHintsOnly = true }).Select(p => p.FileName));
        Assert.Empty(_index.GetNotQualityChecked(10));
        Assert.Equal(QualityHint.Blurry, _index.GetShotQualities()["/photos/blurry.jpg"].Hints);

        // A new rating or tag changes the file's time, not the picture: the measure stays.
        _index.Upsert([Photo("blurry.jpg", t) with { FileModifiedUtc = new DateTime(2026, 2, 1) }]);
        Assert.Single(_index.Search(new PhotoQuery { QualityHintsOnly = true }));

        _index.IgnoreQualityHints(["/photos/blurry.jpg"]);
        Assert.Empty(_index.Search(new PhotoQuery { QualityHintsOnly = true }));
        // Measuring again keeps the dismissal.
        _index.SaveShotQuality([("/photos/blurry.jpg", soft)]);
        Assert.True(_index.GetShotQualities()["/photos/blurry.jpg"].Ignored);

        _index.ForgetShotQuality("/photos/blurry.jpg");
        Assert.Equal(["/photos/blurry.jpg"], _index.GetNotQualityChecked(10).Select(p => p.Path));
    }

    [Fact]
    public void Stack_keeps_only_each_groups_top_shot_in_place()
    {
        string a = new('a', 1), b = new('b', 1), c = new('c', 1), d = new('d', 1), e = new('e', 1);
        var shown = new List<string> { a, b, c, d, e };
        var stacked = SimilarShots.Stack(shown, [([b, c, d], 1)]);
        Assert.Equal([a, c, e], stacked);
        Assert.Equal(shown, SimilarShots.Stack(shown, []));
    }

    [Fact]
    public void Stacks_follow_the_users_changes_and_keep_them_in_the_index()
    {
        var t = new DateTime(2026, 5, 1, 12, 0, 0);
        var photos = Enumerable.Range(1, 5).Select(i => Photo($"a{i}.jpg", t.AddSeconds(i))).ToList();
        _index.Upsert(photos);
        _index.SetShotMarks([(photos[1].Path, new(ShotMark.Out)), (photos[3].Path, new(ShotMark.Split)), (photos[4].Path, new(ShotMark.Cover))]);
        var marks = _index.GetShotMarks();

        var groups = SimilarShots.Find(photos, (_, _, _) => true, marks);
        Assert.Equal([["a1.jpg", "a3.jpg"], ["a4.jpg", "a5.jpg"]], groups.Select(g => g.Photos.Select(p => p.FileName).ToArray()).ToArray());
        Assert.Null(SimilarShots.Cover(groups[0], marks));
        Assert.Equal("a5.jpg", SimilarShots.Cover(groups[1], marks)!.FileName);

        _index.SetShotMarks([(photos[1].Path, new(ShotMark.None))]);
        Assert.Equal(2, _index.GetShotMarks().Count);
    }

    [Fact]
    public void A_stack_the_user_made_holds_photos_that_dont_look_alike()
    {
        var t = new DateTime(2026, 5, 1, 12, 0, 0);
        var photos = new[] { Photo("a.jpg", t), Photo("b.jpg", t.AddSeconds(5)), Photo("c.jpg", t.AddSeconds(9)), Photo("d.jpg", t.AddHours(2)) };
        _index.Upsert(photos);
        _index.SetShotMarks([(photos[3].Path, new(ShotMark.Joined, "k")), (photos[0].Path, new(ShotMark.Cover, "k"))]);
        var marks = _index.GetShotMarks();

        // b and c look alike by themselves; a and d only belong together because the user said so.
        var groups = SimilarShots.Find(photos, (x, y, _) => x.FileName != "a.jpg" && y.FileName != "a.jpg", marks);
        Assert.Equal([["a.jpg", "d.jpg"], ["b.jpg", "c.jpg"]], groups.Select(g => g.Photos.Select(p => p.FileName).ToArray()).ToArray());
        Assert.Equal("a.jpg", SimilarShots.Cover(groups[0], marks)!.FileName);
    }

    [Fact]
    public void A_panorama_never_stacks_with_normal_photos()
    {
        var t = new DateTime(2026, 5, 1, 12, 0, 0);
        var pano = Photo("pano.jpg", t) with { Width = 12000, Height = 2200 };
        var a = Photo("a.jpg", t.AddSeconds(30)) with { Width = 4000, Height = 3000 };
        var b = Photo("b.jpg", t.AddSeconds(40)) with { Width = 3000, Height = 4000 };
        var c = Photo("c.jpg", t.AddSeconds(50));
        var groups = SimilarShots.Find([pano, a, b, c], (_, _, _) => true);
        Assert.Equal([["a.jpg", "b.jpg", "c.jpg"]], groups.Select(g => g.Photos.Select(p => p.FileName).ToArray()).ToArray());
    }
}
