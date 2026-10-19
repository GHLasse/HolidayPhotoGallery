using PhotoGallery.Core.Editing;
using PhotoGallery.Core.ExifTool;
using PhotoGallery.Core.Faces;
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
    public async Task Cropping_moves_the_people_with_the_photo()
    {
        var path = _temp.CopySample(".jpg");
        // Turned clockwise: Anna's face at the top left of the stored pixels is at the top right of the upright photo.
        await Metadata.WriteAsync(path, new MetadataChanges
        {
            Orientation = 6,
            People = [new("Anna", new FaceBox(0, 0, 0.1, 0.2)), new("Ben", new FaceBox(0.1, 0.8, 0.1, 0.1))],
        });
        await _exifTool!.RunAsync(["-overwrite_original", "-XMP-mwg-rs:RegionInfo+={RegionList=[{Area={X=0.5,Y=0.5,W=0.1,H=0.1,Unit=normalized},Type=Focus}]}", path]);

        // Keep the right half of the upright photo, where Anna is; Ben is on the left.
        await Editor.SaveEditAsync(path, ".jpg", 50, 100, FreshImage(".jpg"), new FaceBox(0.5, 0, 0.5, 1));

        var edited = await Metadata.ReadAsync(path);
        var anna = Assert.Single(edited.People);
        Assert.Equal("Anna", anna.Name);
        Assert.Equal(0.6, anna.Box.X, 4);
        Assert.Equal(0.4, anna.Box.Width, 4);
        Assert.Equal(0.1, anna.Box.Height, 4);
        Assert.Empty(edited.OtherRegions);
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
