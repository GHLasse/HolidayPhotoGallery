using Microsoft.UI.Xaml;
using PhotoGallery.App.ViewModels;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.App;

/// <summary>
/// Help picking the best shots (C-4): going through the groups of similar shots among the photos shown, each
/// in Compare with the sharpest highlighted, and the quality hints under the photos in Compare.
/// </summary>
public sealed partial class MainWindow
{
    private List<(List<PhotoItemViewModel> Items, int Best)>? _shotGroups;
    private int _shotGroupIndex;

    private async Task ShowQualityHintsAsync()
    {
        await ViewModel.LoadQualityHintsAsync();
        CompareLayer.RefreshHints();
    }

    /// <summary>Library › Similar shots: every group among the photos shown, one after another in Compare.</summary>
    private async Task GoThroughSimilarShotsAsync(PhotoItemViewModel? startAt = null)
    {
        var shown = ViewModel.AllShown.ToList(); // the shots stacked away too
        if (shown.Count < 2)
        {
            ViewModel.Inform(Tr("No similar shots among the photos shown."));
            return;
        }
        var groups = await ViewModel.SimilarShotGroupsAsync(shown);
        if (groups.Count == 0)
        {
            ViewModel.Inform(Tr("No similar shots among the photos shown. Shots count as similar when they look alike and were taken within 5 minutes of each other."));
            return;
        }
        var start = startAt is null ? 0 : Math.Max(0, groups.FindIndex(g => g.Items.Contains(startAt)));
        if (startAt is not null && !groups[start].Items.Contains(startAt))
        {
            ViewModel.Inform(Tr("{0} has no similar shots among the photos shown.", startAt.FileName));
            return;
        }
        _shotGroups = groups;
        await ShowShotGroupAsync(start);
    }

    private async Task ShowShotGroupAsync(int index)
    {
        if (_shotGroups is not { } groups || index < 0 || index >= groups.Count)
            return;
        _shotGroupIndex = index;
        var (items, best) = groups[index];
        // Compare shows four at most: the sharpest and the three next to it in time.
        var first = Math.Clamp(best - 1, 0, Math.Max(0, items.Count - Controls.CompareView.MaxPhotos));
        var shown = items.Skip(first).Take(Controls.CompareView.MaxPhotos).ToList();
        CompareLayer.Visibility = Visibility.Visible;
        CompareLayer.Show(shown, best - first);
        var text = items.Count > shown.Count
            ? Tr("Similar shots {0} of {1} · {2} of {3} photos, the sharpest highlighted", index + 1, groups.Count, shown.Count, items.Count)
            : Tr("Similar shots {0} of {1} · the sharpest highlighted", index + 1, groups.Count);
        CompareLayer.SetGroup(text, index > 0, index < groups.Count - 1);
        PhotoGrid.SelectedItem = items[best];
        await ShowQualityHintsAsync();
    }

    private async Task StepShotGroupAsync(int step)
    {
        if (_shotGroups is { } groups)
        {
            var next = _shotGroupIndex + step;
            if (next >= 0 && next < groups.Count)
                await ShowShotGroupAsync(next);
            else if (next >= groups.Count)
                ViewModel.Inform(Tr("That was the last group of similar shots."));
        }
    }

    private void EndShotGroups()
    {
        _shotGroups = null;
        CompareLayer.SetGroup(null);
    }
}
