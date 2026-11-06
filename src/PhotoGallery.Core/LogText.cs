namespace PhotoGallery.Core;

/// <summary>
/// Text for the app's logs (activity.log, crash.log), which a user may send along with a bug report: the user's
/// own folder is written as %USERPROFILE%, so the logs don't carry the Windows user name (often the person's name).
/// </summary>
public static class LogText
{
    private static readonly string Profile =
        Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    public static string Redact(string text) => Redact(text, Profile);

    internal static string Redact(string text, string profile)
    {
        // A profile folder as short as "C:\" would turn every path into nonsense.
        if (profile.Length < 4 || string.IsNullOrEmpty(text))
            return text;
        return text.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
    }
}
