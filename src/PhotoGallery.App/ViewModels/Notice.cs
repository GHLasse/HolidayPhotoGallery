namespace PhotoGallery.App.ViewModels;

public enum NoticeSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>A message shown as a banner at the top of the window: blue, yellow or red by severity.</summary>
/// <remarks>A class, not a record: two banners with the same text are still two banners.</remarks>
public sealed class Notice(string message, NoticeSeverity severity, Func<Task>? undo = null)
{
    public string Message { get; } = message;

    public NoticeSeverity Severity { get; } = severity;

    /// <summary>Takes back the change the banner reports; the banner then shows an Undo button.</summary>
    public Func<Task>? Undo { get; } = undo;

    /// <summary>How long a banner stays before it closes by itself; one with Undo stays longer.</summary>
    public TimeSpan ShowFor => Undo is null ? TimeSpan.FromSeconds(20) : TimeSpan.FromSeconds(60);
}
