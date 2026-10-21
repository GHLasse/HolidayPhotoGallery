using PhotoGallery.Core.Tagging;

namespace PhotoGallery.App.ViewModels;

/// <summary>A tag shown as a badge in the info pane, with how many of the selected photos carry it.</summary>
/// <param name="AutoTag">Auto-tagging added it on the photos that have it.</param>
public sealed record TagBadge(string Tag, int Count, int Of, bool AutoTag = false)
{
    public bool IsOnAll => Count >= Of;

    /// <summary>Added by auto-tagging; shown with a mark (and tags from earlier versions without their "ai/" group).</summary>
    public bool IsAuto => AutoTag || AutoTags.IsAuto(Tag);

    public string Label => AutoTags.Label(Tag) + (IsOnAll ? "" : $" {Count}/{Of}");
}
