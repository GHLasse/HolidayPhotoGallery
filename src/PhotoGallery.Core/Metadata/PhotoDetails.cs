using System.Text.Json;
using PhotoGallery.Core.ExifTool;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.Core.Metadata;

/// <summary>
/// The extra facts a photo can tell in the info pane's "More details": who took it, with which camera and
/// lens, and how it was exposed. Read on demand for one photo, not kept in the index.
/// </summary>
public static class PhotoDetails
{
    /// <summary>One detail: a key for the settings, its label, the ExifTool tags tried in order, and how to show the value.</summary>
    public sealed record Detail(string Key, string Label, string[] Tags, Func<string, string>? Format = null, bool ShownByDefault = false);

    public static IReadOnlyList<Detail> All { get; } =
    [
        new("author", Tr("Author"), ["XMP-dc:Creator", "IFD0:Artist", "IPTC:By-line", "IFD0:XPAuthor"], ShownByDefault: true),
        new("copyright", Tr("Copyright"), ["XMP-dc:Rights", "IFD0:Copyright", "IPTC:CopyrightNotice"]),
        new("camera", Tr("Camera"), ["IFD0:Model"], ShownByDefault: true),
        new("make", Tr("Camera maker"), ["IFD0:Make"]),
        new("lens", Tr("Lens"), ["Composite:LensID", "ExifIFD:LensModel", "XMP-aux:Lens"], ShownByDefault: true),
        new("focalLength", Tr("Focal length"), ["ExifIFD:FocalLength"], ShownByDefault: true),
        new("focalLength35", Tr("Focal length (35 mm equivalent)"), ["ExifIFD:FocalLengthIn35mmFormat"]),
        new("aperture", Tr("Aperture"), ["ExifIFD:FNumber", "Composite:Aperture"], v => "f/" + v, ShownByDefault: true),
        new("exposure", Tr("Exposure time"), ["ExifIFD:ExposureTime", "Composite:ShutterSpeed"], v => v + " s", ShownByDefault: true),
        new("iso", Tr("ISO"), ["ExifIFD:ISO", "Composite:ISO"], ShownByDefault: true),
        new("exposureBias", Tr("Exposure compensation"), ["ExifIFD:ExposureCompensation"]),
        new("exposureProgram", Tr("Exposure program"), ["ExifIFD:ExposureProgram"]),
        new("metering", Tr("Metering"), ["ExifIFD:MeteringMode"]),
        new("flash", Tr("Flash"), ["ExifIFD:Flash"]),
        new("focusMode", Tr("Focus mode"), ["MakerNotes:FocusMode", "MakerNotes:AFMode"]),
        new("focusDistance", Tr("Focus distance"), ["MakerNotes:FocusDistance", "ExifIFD:SubjectDistance", "MakerNotes:ApproximateFocusDistance"]),
        new("whiteBalance", Tr("White balance"), ["ExifIFD:WhiteBalance"]),
        new("size", Tr("Size in pixels"), ["Composite:ImageSize"], v => v.Replace('x', '×').Replace(" ", ""), ShownByDefault: true),
        new("fileSize", Tr("File size"), ["System:FileSize"]),
        new("software", Tr("Software"), ["IFD0:Software", "XMP-xmp:CreatorTool"]),
    ];

    /// <summary>The keys shown when the user has not chosen.</summary>
    public static IReadOnlyList<string> DefaultKeys { get; } = All.Where(d => d.ShownByDefault).Select(d => d.Key).ToList();

    /// <summary>
    /// Reads the chosen details (by key, in the order of <see cref="All"/>) from one photo. Details the file
    /// doesn't have are left out.
    /// </summary>
    public static async Task<IReadOnlyList<(string Label, string Value)>> ReadAsync(
        ExifToolProcess exifTool, string path, IEnumerable<string> keys, CancellationToken cancellationToken = default)
    {
        var wanted = keys.ToHashSet();
        var details = All.Where(d => wanted.Contains(d.Key)).ToList();
        if (details.Count == 0)
            return [];

        // Printed values (no -n), so ExifTool writes "1/250", "50.0 mm" and "Off, Did not fire".
        var args = new List<string> { "-json", "-G1" };
        args.AddRange(details.SelectMany(d => d.Tags).Distinct().Select(t => "-" + t));
        args.Add(path);
        var output = await exifTool.RunAsync(args, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(output.Output) ? [] : Parse(output.Output, details);
    }

    /// <summary>Picks each detail's first tag with a value out of ExifTool's JSON for one file.</summary>
    public static IReadOnlyList<(string Label, string Value)> Parse(string json, IEnumerable<Detail> details)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
            return [];
        var item = doc.RootElement[0];
        var result = new List<(string, string)>();
        foreach (var detail in details)
        {
            foreach (var tag in detail.Tags)
            {
                // With -G1 a maker note tag is grouped by maker (e.g. "Canon:FocusMode"), so "MakerNotes:" matches any.
                var value = tag.StartsWith("MakerNotes:", StringComparison.Ordinal)
                    ? FirstWithName(item, tag["MakerNotes:".Length..])
                    : item.TryGetProperty(tag, out var v) ? Text(v) : null;
                if (string.IsNullOrWhiteSpace(value))
                    continue;
                result.Add((detail.Label, detail.Format is { } format ? format(value) : value));
                break;
            }
        }
        return result;
    }

    private static string? FirstWithName(JsonElement item, string name)
    {
        foreach (var property in item.EnumerateObject())
        {
            if (property.Name.EndsWith(":" + name, StringComparison.Ordinal))
                return Text(property.Value);
        }
        return null;
    }

    private static string? Text(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString()?.Trim(),
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.Array => string.Join(", ", value.EnumerateArray().Select(Text).Where(t => !string.IsNullOrWhiteSpace(t))),
        _ => null,
    };
}
