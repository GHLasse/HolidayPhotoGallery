using System.Text.Json;
using PhotoGallery.Core.ExifTool;

namespace PhotoGallery.Core.Metadata;

/// <summary>
/// The badges at the top of the info pane (U-7): the resolution class by the photo's long edge, and whether the
/// file is HDR (an Ultra HDR / gain map JPEG, an Apple HDR photo, or a HEIC or AVIF with PQ or HLG colour).
/// </summary>
public static class PhotoQuality
{
    /// <summary>SD, HD, Full HD, 4K or 8K, by the long edge in pixels.</summary>
    public static string ResolutionClass(int width, int height) => Math.Max(width, height) switch
    {
        >= 7680 => "8K",
        >= 3840 => "4K",
        >= 1920 => "Full HD",
        >= 1280 => "HD",
        _ => "SD",
    };

    /// <summary>
    /// A video's frame rate as one of the usual classes, for a rough idea like the resolution class: 24, 30, 60,
    /// 120 or 240 fps (25 counts as 24, 50 and 59.94 as 60). Null below 23.5 fps or when unknown.
    /// </summary>
    public static string? FrameRateClass(double? fps) => fps switch
    {
        null or < 23.5 => null,
        < 27 => "24fps",
        < 45 => "30fps",
        < 90 => "60fps",
        < 180 => "120fps",
        _ => "240fps",
    };

    // Gain maps (Google/Adobe "hdrgm", Apple's own), Apple's HDR headroom, the transfer curve of HEIC/AVIF, the
    // number of pictures in a JPEG (a gain map is the second one), the colour profile, and Lightroom's edits (E-16).
    private static readonly string[] Tags =
        ["-XMP-hdrgm:Version", "-XMP-apdi:AuxiliaryImageType", "-MakerNotes:HDRHeadroom", "-MakerNotes:HDRGain", "-TransferCharacteristics",
         "-MPF:NumberOfImages", "-ICC_Profile:ProfileDescription", "-ExifIFD:ColorSpace", "-InteropIFD:InteropIndex", "-QuickTime:ColorPrimaries",
         "-XMP-crs:all"];

    /// <summary>What the info pane's badges and a pixel edit need to know about a photo's colours (E-16).</summary>
    /// <param name="Hdr">Whether the photo is HDR.</param>
    /// <param name="ColourSpace">"P3", "sRGB" or "Adobe RGB"; null when unknown or another space.</param>
    /// <param name="ProfileName">The name of the embedded colour profile, if there is one.</param>
    /// <param name="LightroomEdits">Whether Lightroom or Camera Raw left its edit settings (XMP crs) in the photo.</param>
    public sealed record Info(bool Hdr, string? ColourSpace, string? ProfileName, bool LightroomEdits);

    public static async Task<Info> ReadAsync(ExifToolProcess exifTool, string path, CancellationToken cancellationToken = default)
    {
        var output = await exifTool.RunAsync(["-json", "-G1", .. Tags, path], cancellationToken).ConfigureAwait(false);
        try
        {
            return string.IsNullOrWhiteSpace(output.Output) ? new Info(false, null, null, false) : Read(output.Output);
        }
        catch (JsonException)
        {
            return new Info(false, null, null, false);
        }
    }

    public static async Task<bool> IsHdrAsync(ExifToolProcess exifTool, string path, CancellationToken cancellationToken = default) =>
        (await ReadAsync(exifTool, path, cancellationToken).ConfigureAwait(false)).Hdr;

    /// <summary>Whether ExifTool's JSON for one file (read with -G1) shows HDR.</summary>
    public static bool IsHdr(string json) => Read(json).Hdr;

    /// <summary>Reads ExifTool's JSON for one file (read with -G1).</summary>
    public static Info Read(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
            return new Info(false, null, null, false);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in doc.RootElement[0].EnumerateObject())
            values[property.Name] = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() ?? "" : property.Value.GetRawText();

        // A JPEG's gain map is a second picture after the photo. Without it, the HDR notes left in the photo (Apple
        // writes them into every photo, and an edit in another program may keep them) describe nothing.
        var jpeg = values.TryGetValue("SourceFile", out var file) &&
                   Path.GetExtension(file).ToLowerInvariant() is ".jpg" or ".jpeg" or ".jpe";
        var pictures = values.TryGetValue("MPF0:NumberOfImages", out var count) && int.TryParse(count, out var n) ? n : 1;
        var hdr = false;
        foreach (var (name, value) in values)
        {
            if (name.StartsWith("XMP-hdrgm:", StringComparison.Ordinal) && (!jpeg || pictures >= 2))
                hdr = true;
            else if ((name.EndsWith(":HDRHeadroom", StringComparison.Ordinal) || name.EndsWith(":HDRGain", StringComparison.Ordinal)) && jpeg && pictures >= 2)
                hdr = true;
            else if (name.EndsWith(":AuxiliaryImageType", StringComparison.Ordinal) && value.Contains("hdrgainmap", StringComparison.OrdinalIgnoreCase))
                hdr = true;
            else if (name.EndsWith(":TransferCharacteristics", StringComparison.Ordinal) &&
                     (value.Contains("2084", StringComparison.Ordinal) || value.Contains("PQ", StringComparison.Ordinal) ||
                      value.Contains("HLG", StringComparison.Ordinal) || value.Contains("B67", StringComparison.Ordinal)))
                hdr = true;
        }

        var profile = values.TryGetValue("ICC_Profile:ProfileDescription", out var description) && description.Trim().Length > 0 ? description.Trim() : null;
        var lightroom = values.Keys.Any(k => k.StartsWith("XMP-crs:", StringComparison.Ordinal));
        return new Info(hdr, ColourSpace(profile, values), profile, lightroom);
    }

    // The profile names the space; without one, HEIC/AVIF say it in their colour box and JPEGs in EXIF.
    private static string? ColourSpace(string? profile, Dictionary<string, string> values)
    {
        if (profile is not null)
        {
            if (profile.Contains("P3", StringComparison.OrdinalIgnoreCase))
                return "P3";
            if (profile.Contains("sRGB", StringComparison.OrdinalIgnoreCase) || profile.Contains("61966-2", StringComparison.Ordinal))
                return "sRGB";
            if (profile.Replace(" ", "").Contains("AdobeRGB", StringComparison.OrdinalIgnoreCase) ||
                profile.Contains("compatible with Adobe RGB", StringComparison.OrdinalIgnoreCase))
                return "Adobe RGB";
            return null;
        }
        foreach (var (name, value) in values)
        {
            if (!name.EndsWith(":ColorPrimaries", StringComparison.Ordinal))
                continue;
            if (value.Contains("432", StringComparison.Ordinal) || value.Contains("P3", StringComparison.Ordinal))
                return "P3";
            if (value.Contains("709", StringComparison.Ordinal))
                return "sRGB";
        }
        if (values.TryGetValue("InteropIFD:InteropIndex", out var interop) && interop.StartsWith("R03", StringComparison.Ordinal))
            return "Adobe RGB";
        if (values.TryGetValue("ExifIFD:ColorSpace", out var space) && space == "sRGB")
            return "sRGB";
        return null;
    }
}
