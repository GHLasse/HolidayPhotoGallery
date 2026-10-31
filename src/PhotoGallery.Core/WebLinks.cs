namespace PhotoGallery.Core;

/// <summary>
/// Checks web addresses before the app hands them to Windows to open. Some come from files in the photo
/// folders (an album's link to its copy in Google Photos or OneDrive), which anyone who made the folder can
/// write, and Windows opens a file:// or network address by running whatever it points to.
/// </summary>
public static class WebLinks
{
    /// <summary>Only an absolute https address with a host is opened.</summary>
    public static bool IsSafe(Uri? uri) =>
        uri is { IsAbsoluteUri: true } && uri.Scheme == Uri.UriSchemeHttps && !string.IsNullOrEmpty(uri.Host) && !uri.IsUnc;

    /// <summary>The address as a <see cref="Uri"/> when it is safe to open, else null (also for text that isn't an address).</summary>
    public static Uri? Parse(string? text) =>
        Uri.TryCreate(text?.Trim(), UriKind.Absolute, out var uri) && IsSafe(uri) ? uri : null;
}
