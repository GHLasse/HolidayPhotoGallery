namespace PhotoGallery.Core.ExifTool;

public static class ExifToolLocator
{
    /// <summary>
    /// Finds ExifTool: first the copy bundled next to the app (exiftool\exiftool.exe),
    /// then an exiftool on the PATH. Returns null when there is none.
    /// </summary>
    public static string? Find()
    {
        var exeName = OperatingSystem.IsWindows() ? "exiftool.exe" : "exiftool";

        var bundled = Path.Combine(AppContext.BaseDirectory, "exiftool", exeName);
        if (File.Exists(bundled))
            return bundled;

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), exeName);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (ArgumentException)
            {
                // Malformed PATH entry.
            }
        }
        return null;
    }
}
