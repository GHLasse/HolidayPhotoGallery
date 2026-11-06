using PhotoGallery.Core.Tagging;

namespace PhotoGallery.App.ViewModels;

/// <summary>A tag shown as a badge in the info pane, with how many of the selected photos carry it.</summary>
/// <param name="AutoTag">Auto-tagging added it on the photos that have it.</param>
public sealed record TagBadge(string Tag, int Count, int Of, bool AutoTag = false)
{
    public bool IsOnAll => Count >= Of;

    /// <summary>Added by auto-tagging; shown with a mark (and tags from earlier versions without their "ai/" group).</summary>
    public bool IsAuto => AutoTag || AutoTags.IsAuto(Tag);

    /// <summary>A tag in a tree (M-5) shows its path: "Animals › Birds › Seagull".</summary>
    public string Label => (PhotoGallery.Core.Metadata.TagTree.IsPath(Tag) ? PhotoGallery.Core.Metadata.TagTree.Display(Tag) : AutoTags.Label(Tag)) +
                           (IsOnAll ? "" : $" {Count}/{Of}");
}
