using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.App;

/// <summary>
/// Guidance that shows once and then stays out of the way (navigation proposal D): a tip the first time the
/// viewer, Compare, the Map or People opens, and the key of a command after it was clicked three times.
/// The welcome card in the empty library is in MainWindow.xaml. Ctrl+Z is here too.
/// </summary>
public sealed partial class MainWindow
{
    private const int ClicksBeforeKeyHint = 3;
    private readonly Dictionary<string, int> _clicks = [];

    private void TipForViewer() => ViewModel.TipOnce("viewer",
        Tr("Tip: ← → go through the photos, 0–5 rate, I shows the details, Esc goes back. Ctrl+K finds any command."));

    private void TipForCompare() => ViewModel.TipOnce("compare",
        Tr("Tip: Tab or the arrows pick a photo, 0–5 rate it, P flags, X rejects, Esc goes back."));

    private void TipForWorkspace(Workspace workspace)
    {
        if (workspace == Workspace.Map)
            ViewModel.TipOnce("map", Tr("Tip: this map shows all photos with a position. To place photos, select them in the Library and press M for the map beside them."));
        else if (workspace == Workspace.People)
            ViewModel.TipOnce("people", Tr("Tip: Library › Find people looks for faces in the photos shown. Click a person to see their photos."));
    }

    /// <summary>Counts clicks on a command with a key; the third one brings a hint naming the key.</summary>
    private void CountClick(string keys, string label)
    {
        var count = _clicks[keys] = _clicks.GetValueOrDefault(keys) + 1;
        if (count == ClicksBeforeKeyHint)
            ViewModel.TipOnce("key:" + keys, Tr("Tip: next time press {0} for “{1}”. ? shows all keys.", keys, label));
    }

    private void CountRatingClick()
    {
        var count = _clicks["rating"] = _clicks.GetValueOrDefault("rating") + 1;
        if (count == ClicksBeforeKeyHint)
            ViewModel.TipOnce("key:rating", Tr("Tip: next time press 1 to 5 to rate, 0 takes the rating off. ? shows all keys."));
    }

    // Ctrl+Z: the newest change still kept. The info pane shows the photos' marks and tags again.
    private async Task UndoAsync()
    {
        await ViewModel.UndoLastAsync();
        if (!IsViewerOpen && !ViewModel.Editor.IsDirty)
            ViewModel.Editor.Load(SelectedPhotos);
    }
}
