namespace PhotoGallery.App.ViewModels;

public enum NoticeSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>A message shown as a banner at the top of the window: blue, yellow or red by severity.</summary>
/// <remarks>A class, not a record: two banners with the same text are still two banners.</remarks>
public sealed class Notice(string message, NoticeSeverity severity, Func<Task>? undo = null, IReadOnlyList<NoticeAction>? actions = null)
{
    public string Message { get; } = message;

    public NoticeSeverity Severity { get; } = severity;

    /// <summary>Takes back the change the banner reports; the banner then shows an Undo button.</summary>
    public Func<Task>? Undo { get; } = undo;

    /// <summary>Buttons on the banner other than Undo, such as "What's new" and "Update now".</summary>
    public IReadOnlyList<NoticeAction> Actions { get; } = actions ?? [];

    /// <summary>How long a banner stays before it closes by itself; one with buttons stays longer.</summary>
    public TimeSpan ShowFor => Undo is null && Actions.Count == 0 ? TimeSpan.FromSeconds(20) : TimeSpan.FromSeconds(60);
}

/// <summary>A button on a banner. <paramref name="Closes"/>: the banner goes when it is clicked.</summary>
public sealed record NoticeAction(string Label, Func<Task> Run, bool Closes = true);
