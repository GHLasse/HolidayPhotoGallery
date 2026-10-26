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

    // Gain maps (Google/Adobe "hdrgm", Apple's own), Apple's HDR headroom, and the transfer curve of HEIC/AVIF.
    private static readonly string[] Tags =
        ["-XMP-hdrgm:Version", "-XMP-apdi:AuxiliaryImageType", "-MakerNotes:HDRHeadroom", "-MakerNotes:HDRGain", "-TransferCharacteristics"];

    public static async Task<bool> IsHdrAsync(ExifToolProcess exifTool, string path, CancellationToken cancellationToken = default)
    {
        var output = await exifTool.RunAsync(["-json", "-G1", .. Tags, path], cancellationToken).ConfigureAwait(false);
        return !string.IsNullOrWhiteSpace(output.Output) && IsHdr(output.Output);
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
