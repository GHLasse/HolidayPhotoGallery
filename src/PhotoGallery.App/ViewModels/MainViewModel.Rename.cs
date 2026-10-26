using PhotoGallery.Core.Library;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.App.ViewModels;

/// <summary>Renaming many photos at once with a pattern (F-2), with Undo.</summary>
public sealed partial class MainViewModel
{
    public string RenamePatternText
    {
        get => _settings.RenamePattern == RenamePattern.OldDefault ? RenamePattern.Default : _settings.RenamePattern;
        set
        {
            _settings.RenamePattern = value;
            _settings.Save(SettingsPath);
        }
    }

    /// <summary>The photos in the grid's order, so the counter follows what the user sees.</summary>
    public List<IndexedPhoto> InGridOrder(IReadOnlyList<PhotoItemViewModel> items)
    {
        var order = new Dictionary<PhotoItemViewModel, int>();
        for (var i = 0; i < Photos.Count; i++)
            order[Photos[i]] = i;
        return items.OrderBy(i => order.TryGetValue(i, out var at) ? at : int.MaxValue).Select(i => i.Photo).ToList();
    }

    /// <summary>Renames the photos; the banner offers Undo, and after an undo (<paramref name="undoing"/>) Redo.</summary>
    public async Task RenamePhotosAsync(IReadOnlyList<(IndexedPhoto Photo, string NewName)> plan, bool undoing = false)
    {
        if (IsBusy)
        {
            Warn(Tr("The app is still busy. Try again when it has finished."));
            return;
        }
        var failed = new List<string>();
        List<(IndexedPhoto Photo, string NewPath)> done;
        IsBusy = true;
        StatusText = Tr("Renaming {0}…", PhotoCount(plan.Count));
        try
        {
            done = await Task.Run(() => PhotoMover.Rename(plan, _index, failed));
        }
        finally
        {
            IsBusy = false;
            StatusText = _countText;
        }
        if (failed.Count > 0)
            ErrorMessage = Tr("{0:N0} photo(s) could not be renamed.", failed.Count) + " " + string.Join(" ", failed.Take(3));
        if (done.Count > 0)
        {
            var back = done.Select(d => (d.Photo.MovedTo(d.NewPath), d.Photo.FileName)).ToList();
            if (undoing)
                Offer(Tr("Gave {0} their names back.", PhotoCount(done.Count)),
                      [new NoticeAction(Tr("Redo"), () => RenamePhotosAsync(back))]);
            else
                Inform(Tr("Renamed {0}.", PhotoCount(done.Count)), () => RenamePhotosAsync(back, undoing: true));
        }
        await AfterTransferAsync();
    }
}
