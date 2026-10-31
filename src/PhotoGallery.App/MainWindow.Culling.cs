using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhotoGallery.Core.Culling;
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
    private int _shotPage; // a stack of more than four is shown four at a time

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

    private async Task ShowShotGroupAsync(int index, int? page = null)
    {
        if (_shotGroups is not { } groups || index < 0 || index >= groups.Count)
            return;
        _shotGroupIndex = index;
        var (items, best) = groups[index];
        // Compare shows four at a time; a bigger stack is gone through page by page, starting with the sharpest.
        const int PerPage = Controls.CompareView.MaxPhotos;
        var pages = (items.Count + PerPage - 1) / PerPage;
        _shotPage = Math.Clamp(page ?? best / PerPage, 0, pages - 1);
        var first = _shotPage * PerPage;
        var shown = items.Skip(first).Take(PerPage).ToList();
        CompareLayer.Visibility = Visibility.Visible;
        var bestHere = best >= first && best < first + shown.Count;
        CompareLayer.Show(shown, bestHere ? best - first : 0);
        var text = pages > 1
            ? Tr("Similar shots {0} of {1} · photos {2}–{3} of {4}", index + 1, groups.Count, first + 1, first + shown.Count, items.Count)
            : Tr("Similar shots {0} of {1} · the sharpest highlighted", index + 1, groups.Count);
        CompareLayer.SetGroup(text, index > 0 || _shotPage > 0, index < groups.Count - 1 || _shotPage < pages - 1);
        PhotoGrid.SelectedItem = items[best];
        await ShowQualityHintsAsync();
    }

    // Page Down / the arrow: the stack's next four photos, then the next stack.
    private async Task StepShotGroupAsync(int step)
    {
        if (_shotGroups is not { } groups)
            return;
        var pages = (groups[_shotGroupIndex].Items.Count + Controls.CompareView.MaxPhotos - 1) / Controls.CompareView.MaxPhotos;
        if (_shotPage + step >= 0 && _shotPage + step < pages)
        {
            await ShowShotGroupAsync(_shotGroupIndex, _shotPage + step);
            return;
        }
        var next = _shotGroupIndex + step;
        if (next >= 0 && next < groups.Count)
            await ShowShotGroupAsync(next, step > 0 ? 0 : int.MaxValue);
        else if (next >= groups.Count)
            ViewModel.Inform(Tr("That was the last group of similar shots."));
    }

    private void EndShotGroups()
    {
        _shotGroups = null;
        CompareLayer.SetGroup(null);
    }

    /// <summary>
    /// What can be changed about the photo's stack of similar shots: its cover, taking it out, splitting the
    /// stack, unstacking it, or taking such a change back. Empty when the photo isn't in a stack.
    /// </summary>
    private List<MenuFlyoutItem> StackMenuItems(PhotoItemViewModel item)
    {
        var result = new List<MenuFlyoutItem>();
        void Add(string text, string glyph, string tip, Func<Task> run)
        {
            var menuItem = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
            ToolTipService.SetToolTip(menuItem, tip);
            menuItem.Click += async (_, _) => await ChangeStackAsync(item, run);
            result.Add(menuItem);
        }
        var mark = ViewModel.ShotMarkOf(item);
        if (ViewModel.StackOf(item) is { } stack)
        {
            if (!item.IsBestShot)
                Add(Tr("Show on top of the stack"), "\uE8A9", Tr("Shows this photo on top of its stack instead of the sharpest one"), () => ViewModel.MakeStackCoverAsync(item));
            else if (mark == ShotMark.Cover)
                Add(Tr("Let the app choose the top photo"), "\uE8A9", Tr("The sharpest photo goes on top of the stack again"), () => ViewModel.ResetStackMarkAsync(item));
            Add(Tr("Take out of the stack"), "\uE8BB", Tr("This photo stands on its own; the other shots stay stacked"), () => ViewModel.TakeOutOfStackAsync(item));
            if (stack[0] != item)
                Add(Tr("Start a new stack here"), "\uE8C6", Tr("Splits the stack: this photo and the ones after it form a stack of their own"), () => ViewModel.SplitStackAsync(item));
            Add(Tr("Unstack all {0} photos", stack.Count), "\uE8C8", Tr("Every photo of this stack stands on its own"), () => ViewModel.UnstackAsync(item));
        }
        if (mark == ShotMark.Out)
            Add(Tr("Put back into stacks"), "\uE8A9", Tr("This photo joins a stack again when it looks like the shots around it"), () => ViewModel.ResetStackMarkAsync(item));
        else if (mark == ShotMark.Split)
            Add(Tr("Join the stack before"), "\uE8A9", Tr("Joins this stack with the one before it again"), () => ViewModel.ResetStackMarkAsync(item));
        return result;
    }

    private MenuFlyoutSubItem? StackMenu(PhotoItemViewModel item)
    {
        var items = StackMenuItems(item);
        if (items.Count == 0)
            return null;
        var sub = new MenuFlyoutSubItem { Text = Tr("Stack"), Icon = new FontIcon { Glyph = "\uE8A9" } };
        foreach (var menuItem in items)
            sub.Items.Add(menuItem);
        return sub;
    }

    // After a change to a stack: the grid keeps the photo in view, and Compare shows the stack as it is now.
    private async Task ChangeStackAsync(PhotoItemViewModel item, Func<Task> change)
    {
        var neighbour = ViewModel.StackOf(item)?.FirstOrDefault(i => i != item);
        await change();
        var shown = ViewModel.Photos.Contains(item) ? item : ViewModel.StackOf(item)?.FirstOrDefault(i => ViewModel.Photos.Contains(i));
        if (shown is not null)
        {
            PhotoGrid.SelectedItem = shown;
            PhotoGrid.ScrollIntoView(shown);
        }
        if (_shotGroups is not null && CompareLayer.Visibility == Visibility.Visible)
        {
            var next = ViewModel.StackOf(item) is not null ? item : neighbour is not null && ViewModel.StackOf(neighbour) is not null ? neighbour : null;
            if (next is not null)
                await GoThroughSimilarShotsAsync(next);
            else
                EndShotGroups();
        }
    }
}
