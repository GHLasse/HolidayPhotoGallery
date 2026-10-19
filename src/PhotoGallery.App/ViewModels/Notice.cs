namespace PhotoGallery.App.ViewModels;

public enum NoticeSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>A message shown as a banner at the top of the window: blue, yellow or red by severity.</summary>
/// <remarks>A class, not a record: two banners with the same text are still two banners.</remarks>
public sealed class Notice(string message, NoticeSeverity severity)
{
    public string Message { get; } = message;

    public NoticeSeverity Severity { get; } = severity;

    /// <summary>How long a banner stays before it closes by itself.</summary>
    public static readonly TimeSpan ShowFor = TimeSpan.FromSeconds(20);
}
