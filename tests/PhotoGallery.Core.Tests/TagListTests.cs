using PhotoGallery.Core.Metadata;

namespace PhotoGallery.Core.Tests;

public class TagListTests
{
    [Fact]
    public void Parse_splits_trims_and_removes_duplicates() =>
        Assert.Equal(["Beach", "Italy", "Family"], TagList.Parse(" Beach, Italy;Family , beach,, "));

    [Fact]
    public void Batch_edit_keeps_tags_only_one_file_has()
    {
        // Two photos share "Italy"; the user removes it and adds "Holiday 2024".
        var result = TagList.ApplyEdit(["Italy", "Anna"], originalShared: ["Italy"], editedShared: ["Holiday 2024"]);
        Assert.Equal(["Anna", "Holiday 2024"], result);
    }

    [Fact]
    public void Common_returns_tags_in_every_list() =>
        Assert.Equal(["Italy"], TagList.Common([["Italy", "Anna"], ["Beach", "italy"]]));

    [Fact]
    public void Badges_remove_tags_and_add_tags_to_all_but_leave_partial_tags_alone()
    {
        var result = TagList.ApplyBadges(["Beach", "Italy", "Sunset"], removed: ["italy"], addToAll: ["Family", "beach"]);

        Assert.Equal(["Beach", "Sunset", "Family"], result);
    }

    [Fact]
    public void Counts_how_many_photos_have_each_tag()
    {
        var counts = TagList.Counts([["Beach", "Italy"], ["italy"], ["Italy", "Rome", "Rome"]]);

        Assert.Equal([("Beach", 1), ("Italy", 3), ("Rome", 1)], counts);
    }
}
