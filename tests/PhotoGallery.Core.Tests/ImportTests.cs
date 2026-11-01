using PhotoGallery.Core.Import;

namespace PhotoGallery.Core.Tests;

public sealed class ImportTests : IDisposable
{
    private readonly TempFolder _card = new();
    private readonly TempFolder _library = new();

    public void Dispose()
    {
        _card.Dispose();
        _library.Dispose();
    }

    [Fact]
    public void The_folder_pattern_makes_dated_folders_with_the_event_name()
    {
        var date = new DateTime(2026, 10, 12, 9, 0, 0);
        Assert.Equal(Path.Combine("2026", "2026-10-12 Lisbon"), ImportPlanner.FolderFor(ImportPlanner.DefaultPattern, date, "Lisbon"));
        Assert.Equal(Path.Combine("2026", "2026-10-12"), ImportPlanner.FolderFor(ImportPlanner.DefaultPattern, date, null));
        Assert.Equal(Path.Combine("2026", "10", "Trip a b"), ImportPlanner.FolderFor("{year}/{month}/Trip {event}", date, "a:b"));
    }

    [Fact]
    public void Photos_already_in_the_library_are_skipped_and_name_clashes_get_a_number()
    {
        var day = new DateTime(2026, 10, 12, 9, 0, 0);
        ImportItem[] items =
        [
            new("IMG_1.JPG", 100, day),
            new("IMG_2.JPG", 200, day),
            new("IMG_2.JPG", 300, day.AddDays(1)),
            new("IMG_3.JPG", 300, day.AddDays(1)),
            new("notes.txt", 5, day),
        ];
        var have = new HashSet<(string, long)> { ("img_1.jpg", 100) };
        var existing = Path.Combine(_library.Path, "2026", "2026-10-13", "IMG_3.JPG");

        var (plan, skipped) = ImportPlanner.Plan(items, _library.Path, "{year}/{date} {event}", "",
            (name, size) => have.Contains((name.ToLowerInvariant(), size)), path => path == existing);

        Assert.Equal(1, skipped);
        Assert.Equal(
        [
            Path.Combine("2026", "2026-10-12", "IMG_2.JPG"),
            Path.Combine("2026", "2026-10-13", "IMG_2.JPG"),
            Path.Combine("2026", "2026-10-13", "IMG_3 (2).JPG"),
        ], plan.Select(p => Path.GetRelativePath(_library.Path, p.Target)));
    }

    [Fact]
    public void A_card_lists_its_DCIM_photos_and_copies_are_checked_and_editable()
    {
        var dcim = Directory.CreateDirectory(Path.Combine(_card.Path, "DCIM", "100CANON")).FullName;
        var photo = Path.Combine(dcim, "IMG_1.JPG");
        File.WriteAllBytes(photo, [1, 2, 3]);
        File.SetAttributes(photo, FileAttributes.ReadOnly);
        File.WriteAllText(Path.Combine(dcim, "INDEX.DAT"), "x");

        var item = Assert.Single(DriveImport.ListItems(_card.Path));
        Assert.Equal("IMG_1.JPG", item.Name);

        var target = Path.Combine(_library.Path, "2026", "IMG_1.JPG");
        DriveImport.Copy(photo, target, verify: true);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(target));
        Assert.False(File.GetAttributes(target).HasFlag(FileAttributes.ReadOnly));
        Assert.False(File.Exists(target + ".importing"));
        File.SetAttributes(photo, FileAttributes.Normal);
    }

    [Theory]
    [InlineData("DCIM/Camera", "PXL_20261012_090000.jpg", ImportKind.Camera)]
    [InlineData("DCIM/Screenshots", "Screenshot_20261012.png", ImportKind.Screenshot)]
    [InlineData("Pictures/Screenshots", "a.png", ImportKind.Screenshot)]
    [InlineData("Pictures/Signal", "signal-2026-10-12-090000.jpg", ImportKind.Messenger)]
    [InlineData("Android/media/com.whatsapp/WhatsApp/Media/WhatsApp Images", "IMG-20261012-WA0001.jpg", ImportKind.Messenger)]
    [InlineData("Download", "map.jpg", ImportKind.Download)]
    [InlineData("Pictures/Holiday", "x.jpg", ImportKind.Other)]
    [InlineData(null, "IMG-20261012-WA0001.jpg", ImportKind.Messenger)]
    [InlineData(null, "Screenshot_20261012-090000.png", ImportKind.Screenshot)]
    [InlineData(null, "IMG_1234.HEIC", ImportKind.Camera)]
    [InlineData("DCIM/100CANON", "IMG_1.JPG", ImportKind.Camera)]
    public void Phone_photos_are_sorted_by_folder_then_name(string? folder, string name, ImportKind kind) =>
        Assert.Equal(kind, ImportKinds.Of(folder, name));

    [Fact]
    public void Choosing_keeps_the_kinds_and_days_asked_for()
    {
        var day = new DateTime(2026, 10, 12, 23, 30, 0);
        ImportItem[] items =
        [
            new("a.jpg", 1, day) { Folder = "DCIM/Camera" },
            new("b.jpg", 1, day.AddDays(2)) { Folder = "DCIM/Camera" },
            new("Screenshot_1.png", 1, day) { Folder = "DCIM/Screenshots" },
        ];
        var chosen = ImportKinds.Choose(items, new HashSet<ImportKind> { ImportKind.Camera }, day.Date, day.Date.AddDays(1));
        Assert.Equal(["a.jpg"], chosen.Select(i => i.Name));
        Assert.Equal(2, ImportKinds.Choose(items, new HashSet<ImportKind> { ImportKind.Camera }, null, null).Count);
    }

    [Fact]
    public void A_phone_folder_lists_only_its_photo_folders_with_their_place()
    {
        var storage = Path.Combine(_card.Path, "Internal storage");
        var camera = Directory.CreateDirectory(Path.Combine(storage, "DCIM", "Camera")).FullName;
        var shots = Directory.CreateDirectory(Path.Combine(storage, "Pictures", "Screenshots")).FullName;
        var other = Directory.CreateDirectory(Path.Combine(storage, "Documents")).FullName;
        var thumbs = Directory.CreateDirectory(Path.Combine(storage, "DCIM", ".thumbnails")).FullName;
        File.WriteAllBytes(Path.Combine(camera, "PXL_1.jpg"), [1]);
        File.WriteAllBytes(Path.Combine(shots, "Screenshot_1.png"), [1]);
        File.WriteAllBytes(Path.Combine(other, "scan.jpg"), [1]);
        File.WriteAllBytes(Path.Combine(thumbs, "t.jpg"), [1]);

        var items = DriveImport.ListPhoneItems(_card.Path, ["DCIM", "Pictures", "Movies"]).OrderBy(i => i.Name).ToList();

        Assert.Equal(["PXL_1.jpg", "Screenshot_1.png"], items.Select(i => i.Name));
        Assert.Equal(["DCIM/Camera", "Pictures/Screenshots"], items.Select(i => i.Folder));
    }

    [Fact]
    public void The_folders_of_a_choice_are_listed_biggest_first()
    {
        var day = new DateTime(2026, 10, 12);
        ImportItem[] items =
        [
            new("a.jpg", 1, day) { Folder = "DCIM/Camera" },
            new("b.jpg", 1, day) { Folder = "DCIM/Camera" },
            new("c.jpg", 1, day) { Folder = "Pictures/Holiday/Day 1" },
        ];
        Assert.Equal([("DCIM/Camera", 2), ("Pictures/Holiday", 1)], ImportKinds.Folders(items));
    }
}
