using PhotoGallery.Core.Faces;
using PhotoGallery.Core.Library;

namespace PhotoGallery.Core.Tests;

public class IndexedPhotoSameValuesTests
{
    private static IndexedPhoto Photo(params string[] tags) => new()
    {
        Path = "/a/b.jpg", Folder = "/a", Rating = 3, Tags = tags.ToList(),
        People = [new PersonTag("Ann", new FaceBox(0.1, 0.1, 0.2, 0.2))],
    };

    [Fact]
    public void Same_values_in_new_lists_are_the_same() => Assert.True(Photo("x", "y").HasSameValues(Photo("x", "y")));

    [Fact]
    public void A_changed_tag_is_different() => Assert.False(Photo("x").HasSameValues(Photo("y")));

    [Fact]
    public void A_changed_rating_is_different() => Assert.False(Photo("x").HasSameValues(Photo("x") with { Rating = 4 }));
}
