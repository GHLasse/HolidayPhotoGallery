using PhotoGallery.Core.Editing;
using PhotoGallery.Core.ExifTool;
using PhotoGallery.Core.Library;
using PhotoGallery.Core.Metadata;

namespace PhotoGallery.Core.Tests;

public sealed class PhotoEditServiceTests : IAsyncLifetime
{
    private ExifToolProcess? _exifTool;
    private readonly TempFolder _temp = new();

    private PhotoMetadataService Metadata => new(_exifTool!);

    private PhotoEditService Editor => new(Metadata);

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

    // Stands in for the Windows encoder: a fresh image file of the right format, without metadata.
    private Func<string, Task> FreshImage(string extension) => async target =>
    {
        File.Copy(Path.Combine(AppContext.BaseDirectory, "TestData", "sample" + extension), target);
        await _exifTool!.RunAsync(["-overwrite_original", "-all=", target]);
    };

    [ExifToolFact]
    public async Task An_edit_keeps_the_metadata_and_can_be_undone_and_reverted()
    {
        var path = _temp.CopySample(".jpg");
        DateTaken.TryParse("2024-07-01 10:15:30+02:00", out var date);
        await Metadata.WriteAsync(path, new MetadataChanges { Tags = ["Strand"], Rating = 3, DateTaken = date, Orientation = 6, Location = new GeoPosition(9.9, -84.1) });
        var original = await File.ReadAllBytesAsync(path);

        await Editor.SaveEditAsync(path, ".jpg", 100, 80, FreshImage(".jpg"));
        var first = await File.ReadAllBytesAsync(path);
        await Editor.SaveEditAsync(path, ".jpg", 90, 70, FreshImage(".jpg"));
        Assert.Equal(2, EditHistory.EditCount(path));

        var edited = await Metadata.ReadAsync(path);
        Assert.Equal(["Strand"], edited.Tags);
        Assert.Equal(3, edited.Rating);
        Assert.Equal(date, edited.DateTaken);
        Assert.Equal(9.9, edited.Latitude!.Value, 5);
        Assert.Equal(1, edited.Orientation); // the new pixels are upright already

        // A rating given after editing survives undo and revert.
        await Metadata.WriteAsync(path, new MetadataChanges { Rating = 5 });

        Assert.Equal(path, await Editor.UndoAsync(path));
        Assert.Equal(1, EditHistory.EditCount(path));
        Assert.Equal(5, (await Metadata.ReadAsync(path)).Rating);

        Assert.Equal(path, await Editor.RevertAsync(path));
        Assert.Equal(0, EditHistory.EditCount(path));
        var reverted = await Metadata.ReadAsync(path);
        Assert.Equal(5, reverted.Rating);
        Assert.Equal(6, reverted.Orientation);
        Assert.False(Directory.Exists(Path.Combine(_temp.Path, EditHistory.FolderName)));
        Assert.NotEqual(first, original);
    }

    [ExifToolFact]
    public async Task Saving_a_heic_as_jpeg_renames_it_and_revert_brings_the_heic_back()
    {
        var path = _temp.CopySample(".heic", "IMG_0001");
        await Metadata.WriteAsync(path, new MetadataChanges { Tags = ["Tortuguero"] });

        var jpeg = await Editor.SaveEditAsync(path, ".jpg", 100, 80, FreshImage(".jpg"));

        Assert.Equal(Path.Combine(_temp.Path, "IMG_0001.jpg"), jpeg);
        Assert.False(File.Exists(path));
        Assert.Equal(["Tortuguero"], (await Metadata.ReadAsync(jpeg)).Tags);
        Assert.Equal(1, EditHistory.EditCount(jpeg));

        Assert.Equal(path, await Editor.RevertAsync(jpeg));
        Assert.True(File.Exists(path));
        Assert.False(File.Exists(jpeg));
        Assert.Equal(["Tortuguero"], (await Metadata.ReadAsync(path)).Tags);
    }

    [ExifToolFact]
    public async Task The_backup_folder_is_not_part_of_the_library()
    {
        var path = _temp.CopySample(".jpg");
        await Editor.SaveEditAsync(path, ".jpg", 100, 80, FreshImage(".jpg"));

        Assert.Equal([path], LibraryIndexer.EnumeratePhotos(_temp.Path).Select(f => f.FullName));
    }

    [ExifToolFact]
    public async Task A_failed_encode_leaves_the_photo_untouched()
    {
        var path = _temp.CopySample(".jpg");
        var before = await File.ReadAllBytesAsync(path);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Editor.SaveEditAsync(path, ".jpg", 1, 1, _ => throw new InvalidOperationException("codec missing")));

        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.Equal(0, EditHistory.EditCount(path));
        Assert.Single(Directory.GetFiles(_temp.Path));
    }
}
