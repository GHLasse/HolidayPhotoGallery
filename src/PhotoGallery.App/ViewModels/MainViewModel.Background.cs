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
        _ = SyncDatesInBackgroundAsync();
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

    /// <summary>
    /// While the PC's memory is nearly full (another app took it), a background job waits before its next photo,
    /// up to half a minute at a time, so it doesn't push the PC further into swapping. It always goes on after that,
    /// so a PC that runs with full memory still gets its work done, only slower.
    /// </summary>
    private static async Task WaitForMemoryAsync()
    {
        for (var waited = 0; waited < 30 && Native.SystemMemory.IsLow; waited += 5)
            await Task.Delay(TimeSpan.FromSeconds(5));
    }

    // ---- One background job at a time, and only while the user isn't busy with the app ----
    // The jobs used to run side by side: faces, auto-tagging, quality, fingerprints and file checks each took a
    // photo at the same moment, two of them with AI models on nearly every core. After many changes (naming
    // people on 20, then 50 photos...) they all had work for minutes, and the window got no processor time and
    // stopped reacting (Lasse 2026-11-27). Now they take turns, photo by photo, and wait while the user is
    // clicking or typing, so what the user does always comes first.

    private readonly SemaphoreSlim _backgroundTurn = new(1, 1);
    private long _lastUserActivity = Environment.TickCount64;

    /// <summary>How long after the last key press or click the background jobs wait before their next photo.</summary>
    private const int QuietMilliseconds = 1500;

    /// <summary>The window calls this on every key press, click and wheel turn.</summary>
    public void NoteUserActivity() => _lastUserActivity = Environment.TickCount64;

    private bool UserIsActive => Environment.TickCount64 - _lastUserActivity < QuietMilliseconds;

    /// <summary>
    /// Waits until the app is free (nothing the user asked for running, no saves waiting, no recent input,
    /// enough memory, and <paramref name="alsoWaitWhile"/> false) and no other background job is on a photo,
    /// then holds the turn until disposed. Hold it for one photo only, so the jobs take turns.
    /// </summary>
    private async Task<IDisposable> BackgroundTurnAsync(Func<bool>? alsoWaitWhile = null)
    {
        while (true)
        {
            while (IsBusy || HasPendingSaves || alsoWaitWhile?.Invoke() == true)
                await Task.Delay(2000);
            while (UserIsActive)
                await Task.Delay(500);
            await WaitForMemoryAsync();
            await _backgroundTurn.WaitAsync();
            // Things may have changed while waiting for the turn.
            if (!IsBusy && !HasPendingSaves && alsoWaitWhile?.Invoke() != true && !UserIsActive)
                return new Turn(_backgroundTurn);
            _backgroundTurn.Release();
        }
    }

    private sealed class Turn(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                gate.Release();
        }
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
