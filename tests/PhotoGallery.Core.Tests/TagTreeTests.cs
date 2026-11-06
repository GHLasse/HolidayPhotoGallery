using PhotoGallery.Core.ExifTool;
using PhotoGallery.Core.Library;
using PhotoGallery.Core.Metadata;

namespace PhotoGallery.Core.Tests;

public sealed class TagTreeTests : IAsyncLifetime
{
    private ExifToolProcess? _exifTool;
    private readonly TempFolder _temp = new();

    public Task InitializeAsync()
    {
        _exifTool = ExifToolProcess.TryStart();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_exifTool is not null)
            await _exifTool.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _temp.Dispose();
    }

    [Fact]
    public void Paths_replace_the_plain_tags_they_are_made_of()
    {
        // As Lightroom writes it: the parts as plain tags, and the path.
        var tags = TagTree.Merge(["Animals", "Birds", "Seagull", "Italy"], ["Animals/Birds/Seagull", "Travel"]);
        Assert.Equal(["Italy", "Travel", "Animals/Birds/Seagull"], tags);
    }

    [Fact]
    public void Converts_between_lightroom_digikam_and_the_app()
    {
        Assert.Equal("Animals/Birds/Seagull", TagTree.FromLightroom("Animals|Birds|Seagull"));
        Assert.Equal("Animals|Birds|Seagull", TagTree.ToLightroom("Animals/Birds/Seagull"));
        Assert.Equal("Animals/Birds", TagTree.FromDigiKam("/Animals//Birds/"));
        Assert.Equal("Seagull", TagTree.Leaf("Animals/Birds/Seagull"));
        Assert.Equal("Animals › Birds › Seagull", TagTree.Display("Animals/Birds/Seagull"));
        Assert.Equal(["Animals", "Animals/Birds"], TagTree.Ancestors("Animals/Birds/Seagull"));
    }

    [Fact]
    public void Old_auto_tags_are_not_trees()
    {
        Assert.False(TagTree.IsPath("ai/beach"));
        Assert.Equal(["ai/beach", "Seagull", "Birds", "Animals"], TagTree.Plain(["ai/beach", "Animals/Birds/Seagull"]));
        Assert.Equal(["Animals/Birds/Seagull"], TagTree.Paths(["ai/beach", "Animals/Birds/Seagull"]));
    }

    [ExifToolFact]
    public async Task Trees_from_lightroom_and_digikam_are_read_kept_and_written_back()
    {
        var path = _temp.CopySample(".jpg");
        // As Lightroom and digiKam leave a file.
        await _exifTool!.RunAsync(["-overwrite_original", "-XMP-dc:Subject=Animals", "-XMP-dc:Subject=Birds", "-XMP-dc:Subject=Seagull",
                                    "-XMP-dc:Subject=Rome", "-XMP-lr:HierarchicalSubject=Animals|Birds|Seagull",
                                    "-XMP-digiKam:TagsList=Places/Italy/Venice", path]);
        var service = new PhotoMetadataService(_exifTool);
        var m = await service.ReadAsync(path);
        Assert.Equal(["Rome", "Animals/Birds/Seagull", "Places/Italy/Venice"], m.Tags);

        // A save of other details leaves the tree fields as they are.
        await service.WriteAsync(path, new MetadataChanges { Rating = 3 });
        Assert.Equal("Animals|Birds|Seagull", (await _exifTool.RunAsync(["-s3", "-XMP-lr:HierarchicalSubject", path])).Output.Trim());

        // Changing the tags writes the trees back the way both apps read them.
        await service.WriteAsync(path, new MetadataChanges { Tags = [.. m.Tags.Where(t => t != "Rome"), "Sunset"] });
        var again = await service.ReadAsync(path);
        Assert.Equal(["Sunset", "Animals/Birds/Seagull", "Places/Italy/Venice"], again.Tags);
        var lr = (await _exifTool.RunAsync(["-s3", "-sep", ";", "-XMP-lr:HierarchicalSubject", path])).Output.Trim();
        Assert.Equal("Animals|Birds|Seagull;Places|Italy|Venice", lr);
        var subject = (await _exifTool.RunAsync(["-s3", "-sep", ";", "-XMP-dc:Subject", path])).Output.Trim();
        Assert.Equal("Seagull;Birds;Animals;Venice;Italy;Places;Sunset", subject);

        // A tag list from before the app knew trees keeps the file's tree; removing the tag removes its tree.
        await service.WriteAsync(path, new MetadataChanges { Tags = ["Seagull", "Birds", "Animals", "Venice", "Boat"] });
        Assert.Equal(["Boat", "Animals/Birds/Seagull", "Places/Italy/Venice"], (await service.ReadAsync(path)).Tags);
        await service.WriteAsync(path, new MetadataChanges { Tags = ["Boat", "Places/Italy/Venice"] });
        Assert.Equal(["Boat", "Places/Italy/Venice"], (await service.ReadAsync(path)).Tags);

        // Without trees, a plain file gets no tree fields.
        var plain = _temp.CopySample(".jpg", "plain");
        await service.WriteAsync(plain, new MetadataChanges { Tags = ["Beach"] });
        Assert.Equal("", (await _exifTool.RunAsync(["-s3", "-XMP-lr:HierarchicalSubject", "-XMP-digiKam:TagsList", plain])).Output.Trim());
    }

    [Fact]
    public void A_level_in_the_filter_finds_the_tags_under_it_and_the_list_is_in_tree_order()
    {
        using var index = new PhotoIndex(Path.Combine(_temp.Path, "index.db"));
        IndexedPhoto Photo(string name, params string[] tags) =>
            new() { Path = $"/p/{name}", Folder = "/p", FileModifiedUtc = new DateTime(2026, 1, 1), Tags = tags };
        index.Upsert([Photo("gull.jpg", "Animals/Birds/Seagull"), Photo("crow.jpg", "Animals/Birds/Crow"),
                      Photo("cat.jpg", "Animals/Cat"), Photo("beach.jpg", "Beach", "Animals")]);

        Assert.Equal(new[] { "beach.jpg", "cat.jpg", "crow.jpg", "gull.jpg" },
                     index.Search(new PhotoQuery { Tags = ["Animals"] }).Select(p => p.FileName).Order());
        Assert.Equal(["crow.jpg", "gull.jpg"], index.Search(new PhotoQuery { Tags = ["Animals/Birds"] }).Select(p => p.FileName).Order());
        Assert.Equal(["Animals", "Animals/Birds", "Animals/Birds/Crow", "Animals/Birds/Seagull", "Animals/Cat", "Beach"],
                     index.GetTagTreeCounts().Select(t => t.Tag));
        Assert.Equal(4, index.GetTagTreeCounts().First(t => t.Tag == "Animals").Count);
        Assert.Equal(2, index.GetTagTreeCounts().First(t => t.Tag == "Animals/Birds").Count);
    }
}
