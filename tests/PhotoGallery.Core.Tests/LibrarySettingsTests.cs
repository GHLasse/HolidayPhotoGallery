using PhotoGallery.Core.Library;

namespace PhotoGallery.Core.Tests;

public class LibrarySettingsTests
{
    [Fact]
    public void Saves_and_loads_folders()
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Path, "settings.json");

        new LibrarySettings { Folders = [@"D:\Fotos", "/home/me/Pictures"] }.Save(path);

        Assert.Equal([@"D:\Fotos", "/home/me/Pictures"], LibrarySettings.Load(path).Folders);
    }

    [Fact]
    public void Saves_and_loads_favourite_apps_photo_order_and_the_backup_choice()
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Path, "settings.json");

        new LibrarySettings
        {
            FavouriteApps = [new(@"C:\Program Files\Affinity\Photo 2\Photo.exe", "Affinity Photo 2")],
            PhotoSort = PhotoSort.Newest,
            BackUpBeforeOpenWith = false,
        }.Save(path);

        var loaded = LibrarySettings.Load(path);
        Assert.Equal([new FavouriteApp(@"C:\Program Files\Affinity\Photo 2\Photo.exe", "Affinity Photo 2")], loaded.FavouriteApps);
        Assert.Equal(PhotoSort.Newest, loaded.PhotoSort);
        Assert.False(loaded.BackUpBeforeOpenWith);
        Assert.True(new LibrarySettings().BackUpBeforeOpenWith);
    }

    [Fact]
    public void Saves_and_loads_the_auto_tag_list_and_crop_shape()
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Path, "settings.json");

        var settings = new LibrarySettings { CropRatio = "16:9" };
        settings.AutoTagging.Tags = ["Strand", "Berge", "Hund"];
        settings.AutoTagging.Language = "de";
        settings.AutoTagging.Threshold = 0.3;
        settings.Save(path);

        var loaded = LibrarySettings.Load(path);
        Assert.Equal(["Strand", "Berge", "Hund"], loaded.AutoTagging.Tags);
        Assert.Equal("de", loaded.AutoTagging.Language);
        Assert.Equal(0.3, loaded.AutoTagging.Threshold);
        Assert.Equal("16:9", loaded.CropRatio);
    }

    [Fact]
    public void Damaged_or_missing_file_gives_empty_settings()
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Path, "settings.json");
        Assert.Empty(LibrarySettings.Load(path).Folders);

        File.WriteAllText(path, "{ not json");
        Assert.Empty(LibrarySettings.Load(path).Folders);
    }
}
