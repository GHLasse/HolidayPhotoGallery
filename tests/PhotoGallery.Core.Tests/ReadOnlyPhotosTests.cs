using PhotoGallery.Core.Library;

namespace PhotoGallery.Core.Tests;

public sealed class ReadOnlyPhotosTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Clears_the_read_only_flag_of_photos_in_subfolders_too()
    {
        var top = _temp.CopySample(".jpg", "top");
        var nested = _temp.CopySample(".jpg", "nested", subfolder: "sub");
        var writable = _temp.CopySample(".png", "writable");
        File.SetAttributes(top, FileAttributes.ReadOnly);
        File.SetAttributes(nested, FileAttributes.ReadOnly);

        Assert.Equal(2, ReadOnlyPhotos.MakeWritable(_temp.Path));

        Assert.False(new FileInfo(top).IsReadOnly);
        Assert.False(new FileInfo(nested).IsReadOnly);
        Assert.False(new FileInfo(writable).IsReadOnly);
    }
}
