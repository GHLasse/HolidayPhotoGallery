using PhotoGallery.Core.Albums;
using PhotoGallery.Core.Cloud;
using PhotoGallery.Core.ExifTool;
using PhotoGallery.Core.Import;
using PhotoGallery.Core.Library;
using PhotoGallery.Core.Metadata;

namespace PhotoGallery.Core.Tests;

/// <summary>The fixes from the security and data-protection review.</summary>
public sealed class SecurityTests : IDisposable
{
    private readonly TempFolder _photos = new();
    private readonly TempFolder _data = new();
    private readonly PhotoIndex _index;

    public SecurityTests() => _index = new PhotoIndex(Path.Combine(_data.Path, "index.db"));

    public void Dispose()
    {
        _index.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _photos.Dispose();
        _data.Dispose();
    }

    [Theory]
    [InlineData("https://photos.google.com/album/abc", true)]
    [InlineData("https://1drv.ms/a/s!xyz", true)]
    [InlineData("http://photos.google.com/album/abc", false)]
    [InlineData("file:///C:/Windows/System32/calc.exe", false)]
    [InlineData(@"\\attacker\share\setup.exe", false)]
    [InlineData("C:\\Windows\\notepad.exe", false)]
    [InlineData("ms-settings:privacy", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("not a link", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_https_links_are_opened(string? link, bool opened) =>
        Assert.Equal(opened, WebLinks.Parse(link) is not null);

    [Fact]
    public void An_album_cover_outside_the_album_folder_is_not_used()
    {
        var italy = Directory.CreateDirectory(Path.Combine(_photos.Path, "Italy")).FullName;
        var first = AddPhoto("Italy/a.jpg", new DateTime(2024, 5, 1));
        AddPhoto("Elsewhere/secret.jpg", new DateTime(2024, 5, 2));

        AlbumFile.Write(italy, new AlbumInfo { Cover = Path.Combine("..", "Elsewhere", "secret.jpg") });
        Assert.Equal(first, Album.Load(italy, _index).CoverPath);
        AlbumFile.Write(italy, new AlbumInfo { Cover = Path.Combine(_photos.Path, "Elsewhere", "secret.jpg") });
        Assert.Equal(first, Album.Load(italy, _index).CoverPath);
        Assert.Null(Album.CoverIn(italy, "..\\..\\x.jpg".Replace('\\', Path.DirectorySeparatorChar)));
    }

    [Fact]
    public void A_private_photo_is_no_album_cover_while_private_mode_is_off()
    {
        var italy = Directory.CreateDirectory(Path.Combine(_photos.Path, "Italy")).FullName;
        var first = AddPhoto("Italy/a.jpg", new DateTime(2024, 5, 1));
        var hidden = AddPhoto("Italy/b.jpg", new DateTime(2024, 5, 2));
        AlbumFile.Write(italy, new AlbumInfo { Cover = "b.jpg" });
        PrivateMarks.SetFiles([hidden], true);
        _index.RefreshPrivate(italy);

        _index.ShowPrivate = false;
        Assert.Equal(first, Album.Load(italy, _index).CoverPath);
        _index.ShowPrivate = true;
        Assert.Equal(hidden, Album.Load(italy, _index).CoverPath);
    }

    [Theory]
    [InlineData(@"C:\Users\Anna Müller", @"Could not read C:\Users\anna müller\Pictures\a.jpg", @"Could not read %USERPROFILE%\Pictures\a.jpg")]
    [InlineData(@"C:\Users\Anna", @"D:\Fotos\a.jpg", @"D:\Fotos\a.jpg")]
    [InlineData(@"C:\", @"C:\Fotos\a.jpg", @"C:\Fotos\a.jpg")]
    public void Logs_write_the_user_folder_without_the_user_name(string profile, string text, string expected) =>
        Assert.Equal(expected, LogText.Redact(text, profile));

    [Theory]
    [InlineData("IMG_0001.JPG", "IMG_0001.JPG")]
    [InlineData(@"..\..\AppData\Roaming\evil.jpg", "evil.jpg")]
    [InlineData("../../evil.jpg", "evil.jpg")]
    [InlineData("..", null)]
    [InlineData("...", null)]
    [InlineData("a:b.jpg", null)]
    [InlineData("", null)]
    public void Names_from_a_device_stay_in_the_import_folder(string name, string? expected) =>
        Assert.Equal(expected, ImportPlanner.SafeFileName(name));

    [Fact]
    public void An_import_with_a_climbing_name_stays_in_its_folder()
    {
        var destination = Path.Combine(_photos.Path, "Import");
        var (planned, _) = ImportPlanner.Plan(
            [new ImportItem(@"..\..\evil.jpg", 10, new DateTime(2024, 5, 1)), new ImportItem("..", 10, new DateTime(2024, 5, 1))],
            destination, "{year}", "..", (_, _) => false, _ => false);
        var only = Assert.Single(planned);
        Assert.Equal(Path.Combine(destination, "2024", "evil.jpg"), only.Target);
    }

    [ExifToolFact]
    public async Task A_copy_to_send_has_no_GPS_position_and_the_photo_keeps_it()
    {
        await using var exifTool = ExifToolProcess.TryStart()!;
        var metadata = new PhotoMetadataService(exifTool);
        var photo = _photos.CopySample(".jpg", "Wien");
        await metadata.WriteAsync(photo, new MetadataChanges { Location = new GeoPosition(48.2082, 16.3738), Tags = ["Wien"] });

        var copy = await OutgoingCopies.WithoutGpsAsync(exifTool, new UploadPhoto(photo, null), Path.Combine(_data.Path, "Outgoing"));
        Assert.Equal(Path.GetFileName(photo), Path.GetFileName(copy.Source));
        Assert.NotEqual(photo, copy.Source);

        var sent = await metadata.ReadAsync(copy.Source);
        Assert.Null(sent.Latitude);
        Assert.Null(sent.Longitude);
        Assert.Equal(["Wien"], sent.Tags);
        var gps = await exifTool.RunAsync(["-s", "-a", "-G1", "-*GPS*", copy.Source]);
        Assert.DoesNotContain("GPS", gps.Output.Replace("Composite", ""), StringComparison.OrdinalIgnoreCase);
        Assert.NotNull((await metadata.ReadAsync(photo)).Latitude);

        OutgoingCopies.Delete(copy);
        Assert.False(File.Exists(copy.Source));
    }

    private string AddPhoto(string relative, DateTime taken)
    {
        var path = Path.GetFullPath(Path.Combine(_photos.Path, relative));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0]);
        _index.Upsert([new IndexedPhoto { Path = path, Folder = Path.GetDirectoryName(path)!, DateTaken = new DateTaken(taken, null) }]);
        return path;
    }
}
