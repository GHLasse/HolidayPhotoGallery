using System.Text.Json;

namespace PhotoGallery.Core.Metadata;

/// <summary>
/// The EXIF tags that describe the camera and the exposure: what other editing programs often drop when they
/// save a photo, and what pasting "camera" puts back (M-4).
/// </summary>
public static class CameraTags
{
    public static readonly IReadOnlySet<string> Names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "IFD0:Make", "IFD0:Model", "ExifIFD:LensMake", "ExifIFD:LensModel", "ExifIFD:LensInfo",
        "ExifIFD:FNumber", "ExifIFD:ExposureTime", "ExifIFD:ISO", "ExifIFD:FocalLength", "ExifIFD:FocalLengthIn35mmFormat",
        "ExifIFD:ExposureProgram", "ExifIFD:ExposureCompensation", "ExifIFD:MeteringMode", "ExifIFD:Flash", "ExifIFD:WhiteBalance",
    };

    /// <summary>The tags in ExifTool's JSON output (read with -n -G1), as text; empty when there are none.</summary>
    public static IReadOnlyDictionary<string, string> Parse(string? json)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json))
            return result;
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var file in doc.RootElement.EnumerateArray())
            {
                foreach (var tag in file.EnumerateObject())
                {
                    if (!Names.Contains(tag.Name))
                        continue;
                    var value = tag.Value.ValueKind == JsonValueKind.String ? tag.Value.GetString() : tag.Value.GetRawText();
                    if (!string.IsNullOrWhiteSpace(value))
                        result[Names.First(n => n.Equals(tag.Name, StringComparison.OrdinalIgnoreCase))] = value.Trim();
                }
            }
        }
        catch (JsonException)
        {
        }
        return result;
    }
}
