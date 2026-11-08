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

    // Gain maps (Google/Adobe "hdrgm", Apple's own), Apple's HDR headroom, and the transfer curve of HEIC/AVIF.
    private static readonly string[] Tags =
        ["-XMP-hdrgm:Version", "-XMP-apdi:AuxiliaryImageType", "-MakerNotes:HDRHeadroom", "-MakerNotes:HDRGain", "-TransferCharacteristics"];

    public static async Task<bool> IsHdrAsync(ExifToolProcess exifTool, string path, CancellationToken cancellationToken = default)
    {
        var output = await exifTool.RunAsync(["-json", "-G1", .. Tags, path], cancellationToken).ConfigureAwait(false);
        return !string.IsNullOrWhiteSpace(output.Output) && IsHdr(output.Output);
    }

    // E-16: the colour space for its badge, and other apps' edits that a pixel edit would make wrong.
    private static readonly string[] ColourTags =
        ["-ICC_Profile:ProfileDescription", "-ExifIFD:ColorSpace", "-InteropIndex", "-ColorPrimaries", "-XMP-crs:all"];

    /// <summary>
    /// What the Edit pane and the info pane need to know about a photo's colours (E-16): HDR, the colour space for
    /// its badge, and whether it holds another app's edits (Lightroom's, kept beside the pixels) that a pixel edit would remove.
    /// </summary>
    public static async Task<PhotoColour> ReadColourAsync(ExifToolProcess exifTool, string path, CancellationToken cancellationToken = default)
    {
        var output = await exifTool.RunAsync(["-json", "-G1", .. Tags, .. ColourTags, path], cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(output.Output) ? new PhotoColour(false, null, false) : Colour(output.Output);
    }

    /// <summary>The colours of one file from ExifTool's JSON (read with -G1).</summary>
    public static PhotoColour Colour(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
            return new PhotoColour(false, null, false);
        string? profile = null, colorSpace = null, interop = null, primaries = null, hasSettings = null;
        var lightroom = false;
        foreach (var property in doc.RootElement[0].EnumerateObject())
        {
            var value = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() ?? "" : property.Value.GetRawText();
            var name = property.Name;
            if (name.EndsWith(":ProfileDescription", StringComparison.Ordinal))
                profile = value;
            else if (name.EndsWith(":ColorSpace", StringComparison.Ordinal))
                colorSpace = value;
            else if (name.EndsWith(":InteropIndex", StringComparison.Ordinal))
                interop = value;
            else if (name.EndsWith(":ColorPrimaries", StringComparison.Ordinal))
                primaries = value;
            else if (name == "XMP-crs:HasSettings")
                hasSettings = value;
            else if (name.StartsWith("XMP-crs:", StringComparison.Ordinal) && name != "XMP-crs:RawFileName")
                lightroom = true;
        }
        // Lightroom says whether its settings are edits; without that, any of its settings count.
        var otherAppEdits = hasSettings is null ? lightroom : hasSettings.Equals("True", StringComparison.OrdinalIgnoreCase);
        return new PhotoColour(IsHdr(json), ColourSpace(profile, colorSpace, interop, primaries), otherAppEdits);
    }

    /// <summary>
    /// "P3", "Adobe RGB" or "sRGB" from the embedded profile's name, the HEIC colour primaries or the EXIF colour
    /// space (Adobe RGB cameras say "uncalibrated" plus R03); null when the photo doesn't say.
    /// </summary>
    public static string? ColourSpace(string? profile, string? colorSpace, string? interop, string? primaries)
    {
        if (!string.IsNullOrWhiteSpace(profile))
        {
            if (profile.Contains("P3", StringComparison.OrdinalIgnoreCase))
                return "P3";
            if (profile.Contains("Adobe RGB", StringComparison.OrdinalIgnoreCase) || profile.Contains("AdobeRGB", StringComparison.OrdinalIgnoreCase))
                return "Adobe RGB";
            if (profile.Contains("sRGB", StringComparison.OrdinalIgnoreCase))
                return "sRGB";
            return null; // some other profile, e.g. ProPhoto: no badge rather than a wrong one
        }
        if (!string.IsNullOrWhiteSpace(primaries))
        {
            if (primaries.Contains("P3", StringComparison.OrdinalIgnoreCase) || primaries.Contains("432", StringComparison.Ordinal))
                return "P3";
            if (primaries.Contains("709", StringComparison.Ordinal))
                return "sRGB";
        }
        if (colorSpace is not null && colorSpace.Equals("sRGB", StringComparison.OrdinalIgnoreCase))
            return "sRGB";
        if (interop is not null && interop.StartsWith("R03", StringComparison.Ordinal))
            return "Adobe RGB";
        return null;
    }

    /// <summary>Whether ExifTool's JSON for one file (read with -G1) shows HDR.</summary>
    public static bool IsHdr(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
            return false;
        foreach (var property in doc.RootElement[0].EnumerateObject())
        {
            var name = property.Name;
            var value = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() ?? "" : property.Value.GetRawText();
            if (name.StartsWith("XMP-hdrgm:", StringComparison.Ordinal) || name.EndsWith(":HDRHeadroom", StringComparison.Ordinal) ||
                name.EndsWith(":HDRGain", StringComparison.Ordinal))
                return true;
            if (name.EndsWith(":AuxiliaryImageType", StringComparison.Ordinal) && value.Contains("hdrgainmap", StringComparison.OrdinalIgnoreCase))
                return true;
            if (name.EndsWith(":TransferCharacteristics", StringComparison.Ordinal) &&
                (value.Contains("2084", StringComparison.Ordinal) || value.Contains("PQ", StringComparison.Ordinal) ||
                 value.Contains("HLG", StringComparison.Ordinal) || value.Contains("B67", StringComparison.Ordinal)))
                return true;
        }
        return false;
    }
}

/// <summary>A photo's colours (E-16): HDR, its colour space for the badge ("P3", "Adobe RGB", "sRGB" or null), and whether another app's edits are kept beside its pixels.</summary>
public sealed record PhotoColour(bool Hdr, string? Space, bool OtherAppEdits);
