using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.App.ViewModels;

/// <summary>
/// Don't overwrite a change someone else just made (M-6): every save first checks the file is as the index
/// read it (PhotoMetadataService.BasisFor). If another PC or program changed it meanwhile, the save merges:
/// both sides' tags and people are kept, and fields only they changed are left alone. Only when both changed
/// the same field differently does the user hear about it.
/// </summary>
public sealed partial class MainViewModel
{
    private void ChangedElsewhere(string path, IReadOnlyList<string> clashes)
    {
        var name = Path.GetFileName(path);
        App.Trace($"Save merged with a change made elsewhere: {name} ({string.Join(", ", clashes)})");
        if (clashes.Count == 0)
            return;
        Warn(Tr("{0} was changed on another PC or in another program since the app read it. Both changes are kept, except the {1}: yours was saved over the other one.",
                name, string.Join(", ", clashes)));
    }
}
