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
    public void Damaged_or_missing_file_gives_empty_settings()
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Path, "settings.json");
        Assert.Empty(LibrarySettings.Load(path).Folders);

        File.WriteAllText(path, "{ not json");
        Assert.Empty(LibrarySettings.Load(path).Folders);
    }
}
