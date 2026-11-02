using PhotoGallery.Core.Library;

namespace PhotoGallery.Core.Tests;

/// <summary>A photo changed in another app while this one was closed is noticed by its new fingerprint.</summary>
public sealed class PictureChangeTests : IDisposable
{
    private readonly TempFolder _photos = new();
    private readonly TempFolder _data = new();
    private readonly PhotoIndex _index;

    public PictureChangeTests() => _index = new PhotoIndex(Path.Combine(_data.Path, "index.db"));

    public void Dispose()
    {
        _index.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _photos.Dispose();
        _data.Dispose();
    }

    private string Add(string name, DateTime modified)
    {
        var path = Path.GetFullPath(Path.Combine(_photos.Path, name));
        File.WriteAllBytes(path, [0]);
        Touch(path, modified);
        return path;
    }

    private void Touch(string path, DateTime modified) =>
        _index.Upsert([new IndexedPhoto { Path = path, Folder = Path.GetDirectoryName(path)!, FileModifiedUtc = modified }]);

    [Fact]
    public void A_new_picture_is_reported_but_new_details_and_the_first_fingerprint_are_not()
    {
        var day = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);
        var edited = Add("a.jpg", day);
        var tagged = Add("b.jpg", day);

        Assert.Empty(_index.SavePictureHashes([(edited, 0xFF00FF00FF00FF00UL), (tagged, 0x0F0F0F0F0F0F0F0FUL)]));

        Touch(edited, day.AddHours(1));
        Touch(tagged, day.AddHours(1));
        Assert.Empty(_index.GetPictureHashes()); // both are fingerprinted again
        var changed = _index.SavePictureHashes([(edited, 0x00FF00FF00FF00FFUL), (tagged, 0x0F0F0F0F0F0F0F0EUL)]);

        Assert.Equal([edited], changed);
    }

    [Fact]
    public void After_the_app_edits_the_pixels_itself_the_next_fingerprint_is_not_a_change()
    {
        var day = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);
        var path = Add("a.jpg", day);
        _index.SavePictureHashes([(path, 0xFF00FF00FF00FF00UL)]);

        Touch(path, day.AddHours(1));
        _index.ForgetPictureHash(path);

        Assert.Empty(_index.SavePictureHashes([(path, 0x00FF00FF00FF00FFUL)]));
    }

    [Theory]
    [InlineData(1UL, 3UL, false)]
    [InlineData(0UL, 0x1FUL, true)]
    [InlineData(null, 5UL, true)]
    [InlineData(5UL, null, true)]
    [InlineData(null, null, false)]
    public void Only_more_than_a_few_bits_or_a_picture_that_can_now_or_no_longer_be_read_count(ulong? before, ulong? now, bool expected) =>
        Assert.Equal(expected, PhotoIndex.PictureChanged(before, now));
}
