using PhotoGallery.Core.ExifTool;
using PhotoGallery.Core.Metadata;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.App.ViewModels;

/// <summary>
/// Ctrl+Z: takes back the last change, newest first. Every banner with an Undo button is a step, and so is
/// each rating, flag and reject mark (they show on the photos, so they get no banner). A step undone with
/// the banner's button is gone from the list too, and what an undo itself reports (a Redo) is not a step.
/// </summary>
public sealed partial class MainViewModel
{
    private sealed class UndoStep(string what, Func<Task> undo)
    {
        public string What { get; } = what;

        public bool Done { get; private set; }

        /// <summary>What the banner's Undo button runs, so it and Ctrl+Z take the step only once.</summary>
        public Func<Task>? Button { get; set; }

        public async Task RunAsync()
        {
            if (Done)
                return;
            Done = true;
            await undo();
        }
    }

    private const int MaxUndoSteps = 30;
    private readonly List<UndoStep> _undoSteps = [];
    private bool _undoing;
    private int _noticesMade;

    /// <summary>Whether Ctrl+Z has something to take back.</summary>
    public bool CanUndo => _undoSteps.Count > 0;

    /// <summary>Keeps the change for Ctrl+Z and gives back what a banner's Undo button should run.</summary>
    private Func<Task> RememberUndo(string what, Func<Task> undo)
    {
        var step = new UndoStep(what, undo);
        step.Button = () => RunUndoAsync(step);
        if (!_undoing)
        {
            _undoSteps.Add(step);
            if (_undoSteps.Count > MaxUndoSteps)
                _undoSteps.RemoveAt(0);
        }
        return step.Button;
    }

    private async Task RunUndoAsync(UndoStep step)
    {
        _undoSteps.Remove(step);
        var was = _undoing;
        _undoing = true;
        try
        {
            await step.RunAsync();
        }
        finally
        {
            _undoing = was;
        }
    }

    /// <summary>Takes back the newest change still kept (Ctrl+Z) and says what it was.</summary>
    public async Task UndoLastAsync()
    {
        if (_undoSteps.Count == 0)
        {
            Inform(Tr("There is nothing to undo."));
            return;
        }
        var step = _undoSteps[^1];
        if (Notices.FirstOrDefault(n => n.Undo == step.Button) is { } banner)
            Notices.Remove(banner);
        var made = _noticesMade;
        await RunUndoAsync(step);
        // An undo that reports its own result (such as a rename's Redo) is not reported twice.
        if (_noticesMade == made)
            Inform(Tr("Undone: {0}", step.What));
    }

    /// <summary>The marks of the photos before a rating, flag or reject, to put back with Ctrl+Z.</summary>
    private static List<(PhotoItemViewModel Item, int Rating, bool Flagged, bool Rejected)> Marks(IEnumerable<PhotoItemViewModel> items) =>
        items.Select(i => (i, i.Photo.Rating, i.Photo.Flagged, i.Photo.Rejected)).ToList();

    private void RememberMarks(string what, List<(PhotoItemViewModel Item, int Rating, bool Flagged, bool Rejected)> before)
    {
        // Only the photos whose marks the change really touched.
        before = before.Where(b => b.Item.Photo.Rating != b.Rating || b.Item.Photo.Flagged != b.Flagged || b.Item.Photo.Rejected != b.Rejected).ToList();
        if (before.Count > 0)
            RememberUndo(what, () => RestoreMarksAsync(before));
    }

    private async Task RestoreMarksAsync(List<(PhotoItemViewModel Item, int Rating, bool Flagged, bool Rejected)> before)
    {
        if (_metadata is null || _indexer is null)
            return;
        var failures = new List<string>();
        foreach (var (item, rating, flagged, rejected) in before)
        {
            var now = item.Photo;
            var changes = new MetadataChanges
            {
                Rating = now.Rating != rating ? rating : null,
                Flagged = now.Flagged != flagged ? flagged : null,
                Rejected = now.Rejected != rejected ? rejected : null,
            };
            if (changes.IsEmpty)
                continue;
            try
            {
                await _metadata.WriteAsync(item.Path, changes);
                if (await _indexer.RefreshFileAsync(item.Path) is { } updated)
                    item.Photo = updated;
            }
            catch (Exception ex) when (ex is ExifToolException or IOException or UnauthorizedAccessException)
            {
                failures.Add($"{item.FileName}: {ex.Message}");
            }
        }
        ErrorMessage = failures.Count == 0 ? null : Tr("Some photos could not be changed back.") + " " + string.Join(" ", failures);
        FlagsChanged?.Invoke();
    }
}
