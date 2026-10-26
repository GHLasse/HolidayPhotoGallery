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
}
