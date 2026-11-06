namespace PhotoGallery.App.ViewModels;

/// <summary>
/// What runs quietly in the background (quality check, faces, tagging, fingerprints, file checks), for the title
/// bar to show beside its ring, like the work the user asked for. Counts only, never file names (private photos
/// are looked at too).
/// </summary>
public sealed partial class MainViewModel
{
    private readonly Dictionary<string, string> _backgroundJobs = [];

    /// <summary>The first job running, for the title bar; empty when none.</summary>
    public string BackgroundText => _backgroundJobs.Values.FirstOrDefault() ?? "";

    /// <summary>Every job running, one per line, for the details under the ring.</summary>
    public string BackgroundDetails => string.Join(Environment.NewLine, _backgroundJobs.Values);

    public bool IsWorkingInBackground => _backgroundJobs.Count > 0;

    /// <summary>Shows how far a background job has got, or (null) that it has ended.</summary>
    private void ShowBackground(string job, string? text)
    {
        if (text is null ? !_backgroundJobs.Remove(job) : _backgroundJobs.TryGetValue(job, out var was) && was == text)
            return;
        if (text is not null)
            _backgroundJobs[job] = text;
        OnPropertyChanged(nameof(BackgroundText));
        OnPropertyChanged(nameof(BackgroundDetails));
        OnPropertyChanged(nameof(IsWorkingInBackground));
    }

    // ---- Keeping the background jobs going during a long session ----
    // Each job looks for its own work in the index (photos without a vector, a measure, a fingerprint for the file
    // as it is now, a file check, a face search, place names) and returns at once when it is running or has none.
    // So they are simply started again whenever photos may have arrived or changed: after every check of the
    // folders, after every job the user started (an import, a move, a rename, an edit, a new location...), after a
    // photo changed in another app, after the info pane's saves, and every 15 minutes for whatever failed for a
    // while (no connection, a file in use) or slipped through.

    /// <summary>Starts every background job that has work waiting.</summary>
    public void StartBackgroundJobs()
    {
        _ = FindFacesInNewPhotosAsync();
        _ = CheckFilesInBackgroundAsync();
        _ = FindDuplicatesInBackgroundAsync();
        _ = TagInBackgroundAsync();
        _ = CheckQualityInBackgroundAsync();
        _ = AddPlaceNamesInBackgroundAsync();
    }

    private CancellationTokenSource? _kickDelay;

    /// <summary>Starts the background jobs a few seconds from now, once for a burst of changes.</summary>
    private async void StartBackgroundJobsSoon()
    {
        _kickDelay?.Cancel();
        var cts = _kickDelay = new CancellationTokenSource();
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (!IsBusy)
            StartBackgroundJobs();
    }

    private bool _keepingJobsGoing;

    /// <summary>Every 15 minutes, the background jobs are started again (see above). Runs while the app does.</summary>
    public async Task KeepBackgroundJobsGoingAsync()
    {
        if (_keepingJobsGoing)
            return;
        _keepingJobsGoing = true;
        while (true)
        {
            await Task.Delay(TimeSpan.FromMinutes(15));
            if (!IsBusy)
                StartBackgroundJobs();
        }
    }
}
