using PhotoGallery.Core.Tagging;

namespace PhotoGallery.App.ViewModels;

/// <summary>A tag shown as a badge in the info pane, with how many of the selected photos carry it.</summary>
public sealed record TagBadge(string Tag, int Count, int Of)
{
    public bool IsOnAll => Count >= Of;

    /// <summary>Added by auto-tagging (Auto/…); shown without the prefix and with a mark.</summary>
    public bool IsAuto => AutoTags.IsAuto(Tag);

    public string Label => (IsAuto ? Tag[(Tag.IndexOf('/') + 1)..] : Tag) + (IsOnAll ? "" : $" {Count}/{Of}");
}
