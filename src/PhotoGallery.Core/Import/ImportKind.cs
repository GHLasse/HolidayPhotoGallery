namespace PhotoGallery.Core.Import;

/// <summary>What a photo on a phone is, by the folder it is in and its name, so the import can leave out the kinds nobody wants in the library.</summary>
public enum ImportKind
{
    Camera,
    Screenshot,
    Messenger,
    Download,
    Other,
}

/// <summary>
/// Sorts the photos on a phone into kinds (F-1): what the camera took, screenshots and screen recordings, pictures
/// from messengers (WhatsApp, Signal, Telegram…), downloads, and everything else. The folder decides when it is
/// known; the file name decides for a device that gives no folders (Windows' import).
/// </summary>
public static class ImportKinds
{
    // Lower case parts of a folder path, as phones (and their languages) name them.
    private static readonly string[] ScreenshotFolders = ["screenshots", "screenshot", "bildschirmfotos", "screen recordings", "screenrecorder", "capturas de pantalla", "截屏", "屏幕截图"];
    private static readonly string[] MessengerFolders = ["whatsapp", "signal", "telegram", "threema", "messenger", "viber", "wechat", "line", "snapchat", "instagram", "facebook"];
    private static readonly string[] DownloadFolders = ["download", "downloads"];
    private static readonly string[] CameraFolders = ["camera", "100andro", "100apple", "100media", "kamera", "cámara"];

    public static ImportKind Of(ImportItem item) => Of(item.Folder, item.Name);

    public static ImportKind Of(string? folder, string name)
    {
        var parts = (folder ?? "").ToLowerInvariant().Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        // The deepest folder that says something wins: DCIM/Screenshots is a screenshot, Pictures/WhatsApp a messenger picture.
        for (var i = parts.Length - 1; i >= 0; i--)
        {
            var part = parts[i];
            if (ScreenshotFolders.Any(part.StartsWith))
                return ImportKind.Screenshot;
            if (MessengerFolders.Any(m => part == m || part.StartsWith(m + " ")))
                return ImportKind.Messenger;
            if (DownloadFolders.Contains(part))
                return ImportKind.Download;
            if (CameraFolders.Contains(part))
                return ImportKind.Camera;
        }
        var lower = name.ToLowerInvariant();
        if (lower.StartsWith("screenshot") || lower.StartsWith("screen_recording") || lower.StartsWith("screenrecord") || lower.StartsWith("bildschirmfoto"))
            return ImportKind.Screenshot;
        // WhatsApp: IMG-20240101-WA0001.jpg, VID-…-WA…; Signal: signal-2024-01-01-123456.jpg; Telegram: photo_2024-….
        if (lower.Contains("-wa0") || lower.StartsWith("signal-") || lower.StartsWith("photo_20") || lower.StartsWith("video_20"))
            return ImportKind.Messenger;
        if (parts.Length == 0 || parts[0] == "dcim")
            return ImportKind.Camera;
        return ImportKind.Other;
    }

    /// <summary>The kinds left out unless asked for: nobody imports their screenshots or chat pictures with the trip.</summary>
    public static bool IsLeftOutAtFirst(ImportKind kind) => kind is ImportKind.Screenshot or ImportKind.Messenger or ImportKind.Download;

    /// <summary>The items of the chosen kinds taken between the two days (both included; null: no limit).</summary>
    public static List<ImportItem> Choose(IEnumerable<ImportItem> items, IReadOnlySet<ImportKind> kinds, DateTime? from, DateTime? to) =>
        items.Where(i => kinds.Contains(Of(i)) &&
                         (from is not { } start || i.Taken.Date >= start.Date) &&
                         (to is not { } end || i.Taken.Date <= end.Date))
             .ToList();

    /// <summary>
    /// The folders the items are in, biggest first, each as its first two levels ("DCIM/Camera"), so a choice
    /// can say where its photos are.
    /// </summary>
    public static List<(string Folder, int Count)> Folders(IEnumerable<ImportItem> items) =>
        items.GroupBy(i => string.Join('/', (i.Folder ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries).Take(2)), StringComparer.OrdinalIgnoreCase)
             .Select(g => (g.Key, g.Count()))
             .OrderByDescending(f => f.Item2)
             .ToList();
}
