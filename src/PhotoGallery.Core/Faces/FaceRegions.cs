using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PhotoGallery.Core.Faces;

/// <summary>
/// A region in the photo's metadata that is not a named person (a focus area, a pet, or a face the
/// camera found without a name). It is written back unchanged, so other programs' regions survive.
/// </summary>
/// <param name="Value">The region in ExifTool's structure syntax.</param>
/// <param name="UnnamedFace">For an unnamed face, where it is on the stored pixels.</param>
public sealed record OtherRegion(string Value, FaceBox? UnnamedFace);

/// <summary>
/// Reads and writes the people named in a photo, in the two places programs keep them:
/// <list type="bullet">
/// <item>Metadata Working Group regions (XMP mwg-rs), used by Lightroom, digiKam, Picasa and phones.
/// Their box is the centre and size of the face on the pixels as stored, before the EXIF orientation.</item>
/// <item>Microsoft's people tags (XMP MP), used by Windows Live Photo Gallery. Their box is the top-left
/// corner and size of the face on the photo as shown upright.</item>
/// </list>
/// </summary>
public static class FaceRegions
{
    public const string MwgTag = "XMP-mwg-rs:RegionInfo";
    public const string MicrosoftTag = "XMP-MP:RegionInfoMP";

    /// <summary>Reads the named people and the other regions from ExifTool's JSON (read with -struct).</summary>
    public static (IReadOnlyList<PersonTag> People, IReadOnlyList<OtherRegion> Others) Read(JsonElement item, int orientation)
    {
        var people = new List<PersonTag>();
        var others = new List<OtherRegion>();

        if (item.TryGetProperty(MwgTag, out var info) && info.ValueKind == JsonValueKind.Object &&
            info.TryGetProperty("RegionList", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var region in list.EnumerateArray())
            {
                if (region.ValueKind != JsonValueKind.Object)
                    continue;
                var type = Text(region, "Type");
                var isFace = type is null || type.Equals("Face", StringComparison.OrdinalIgnoreCase);
                var box = MwgBox(region);
                var name = Text(region, "Name")?.Trim();
                if (isFace && box is { } face && !string.IsNullOrEmpty(name))
                    people.Add(new PersonTag(name, face));
                else
                    others.Add(new OtherRegion(ToStructure(region), isFace ? box : null));
            }
        }

        if (item.TryGetProperty(MicrosoftTag, out var mp) && mp.ValueKind == JsonValueKind.Object &&
            mp.TryGetProperty("Regions", out var regions) && regions.ValueKind == JsonValueKind.Array)
        {
            foreach (var region in regions.EnumerateArray())
            {
                var name = region.ValueKind == JsonValueKind.Object ? Text(region, "PersonDisplayName")?.Trim() : null;
                if (string.IsNullOrEmpty(name) || people.Any(p => p.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase)))
                    continue;
                // Windows Live Photo Gallery could tag a person without marking the face: the whole photo then.
                var box = ParseRectangle(Text(region, "Rectangle"))?.FromUpright(orientation) ?? new FaceBox(0, 0, 1, 1);
                people.Add(new PersonTag(name, box));
            }
        }

        return (people, others);
    }

    /// <summary>
    /// ExifTool arguments that replace the people in a photo (boxes on the stored pixels) while keeping
    /// its other regions. Unnamed faces where a person is now named are dropped, so they are not doubled.
    /// </summary>
    public static IEnumerable<string> WriteArguments(IReadOnlyList<PersonTag> people, IReadOnlyList<OtherRegion> others,
                                                     int? width, int? height, int orientation)
    {
        var keep = others.Where(o => o.UnnamedFace is not { } face || !people.Any(p => p.Box.Overlap(face) > 0.5)).ToList();
        if (people.Count == 0 && keep.Count == 0)
        {
            yield return $"-{MwgTag}=";
        }
        else
        {
            var regions = people.Select(p =>
            {
                var b = p.Box.Clamp();
                return $"{{Area={{X={Number(b.CenterX)},Y={Number(b.CenterY)},W={Number(b.Width)},H={Number(b.Height)},Unit=normalized}},Name={Escape(p.Name)},Type=Face}}";
            }).Concat(keep.Select(o => o.Value));
            var dimensions = width is { } w && height is { } h ? $"AppliedToDimensions={{W={w},H={h},Unit=pixel}}," : "";
            yield return $"-{MwgTag}={{{dimensions}RegionList=[{string.Join(",", regions)}]}}";
        }

        if (people.Count == 0)
        {
            yield return $"-{MicrosoftTag}=";
        }
        else
        {
            var regions = people.Select(p =>
            {
                var b = p.Box.Clamp().ToUpright(orientation);
                // The rectangle is one text value whose commas have to be escaped for ExifTool.
                return $"{{PersonDisplayName={Escape(p.Name)},Rectangle={Number(b.X)}|, {Number(b.Y)}|, {Number(b.Width)}|, {Number(b.Height)}}}";
            });
            yield return $"-{MicrosoftTag}={{Regions=[{string.Join(",", regions)}]}}";
        }
    }

    internal static FaceBox? ParseRectangle(string? rectangle)
    {
        var parts = rectangle?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts is not { Length: 4 })
            return null;
        var values = new double[4];
        for (var i = 0; i < 4; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
                return null;
        }
        return values[2] > 0 && values[3] > 0 ? new FaceBox(values[0], values[1], values[2], values[3]).Clamp() : null;
    }

    private static FaceBox? MwgBox(JsonElement region)
    {
        if (!region.TryGetProperty("Area", out var area) || area.ValueKind != JsonValueKind.Object)
            return null;
        var unit = Text(area, "Unit");
        if (unit is not null && !unit.Equals("normalized", StringComparison.OrdinalIgnoreCase))
            return null;
        if (Number(area, "X") is not { } x || Number(area, "Y") is not { } y || Number(area, "W") is not { } w || Number(area, "H") is not { } h ||
            w <= 0 || h <= 0)
        {
            return null;
        }
        return new FaceBox(x - w / 2, y - h / 2, w, h).Clamp();
    }

    /// <summary>A JSON value from ExifTool written back in ExifTool's structure syntax.</summary>
    internal static string ToStructure(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", value.EnumerateObject().Select(p => $"{p.Name}={ToStructure(p.Value)}")) + "}",
        JsonValueKind.Array => "[" + string.Join(",", value.EnumerateArray().Select(ToStructure)) + "]",
        JsonValueKind.String => Escape(value.GetString() ?? ""),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null or JsonValueKind.Undefined => "",
        _ => value.GetRawText(),
    };

    /// <summary>Escapes a value for ExifTool's structure syntax, where , = [ ] { } | and leading spaces are special.</summary>
    internal static string Escape(string value)
    {
        var sb = new StringBuilder(value.Length + 4);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c is ',' or '=' or '[' or ']' or '{' or '}' or '|' || (i == 0 && char.IsWhiteSpace(c)))
                sb.Append('|');
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static string Number(double value) => Math.Round(value, 6).ToString("0.######", CultureInfo.InvariantCulture);

    private static string? Text(JsonElement item, string key) =>
        item.TryGetProperty(key, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        } : null;

    private static double? Number(JsonElement item, string key)
    {
        if (!item.TryGetProperty(key, out var v))
            return null;
        if (v.ValueKind == JsonValueKind.Number)
            return v.GetDouble();
        return v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
    }
}
