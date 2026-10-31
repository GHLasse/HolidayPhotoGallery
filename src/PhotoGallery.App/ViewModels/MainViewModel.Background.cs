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
}
