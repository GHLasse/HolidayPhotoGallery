using System.Globalization;
using System.Text.Json;
using PhotoGallery.Core.ExifTool;

namespace PhotoGallery.Core.Metadata;

/// <summary>
/// Reads and writes photo metadata in the image files themselves, through ExifTool.
/// Values are written to every place common Windows tools look (XMP, EXIF, IPTC and the
/// Microsoft tags Windows Explorer and Windows Live Photo Gallery use), so they stay in sync.
/// </summary>
public sealed class PhotoMetadataService(ExifToolProcess exifTool)
{
    private static readonly string[] ReadTags =
    [
        "-XMP-dc:Subject", "-IPTC:Keywords", "-XMP-microsoft:LastKeywordXMP", "-IFD0:XPKeywords",
        "-XMP-xmp:Rating", "-XMP-microsoft:RatingPercent", "-IFD0:Rating",
        "-XMP-dc:Title", "-XMP-dc:Description", "-IFD0:XPTitle", "-IFD0:ImageDescription", "-IPTC:Caption-Abstract",
        "-ExifIFD:DateTimeOriginal", "-ExifIFD:OffsetTimeOriginal", "-XMP-exif:DateTimeOriginal",
        "-XMP-photoshop:DateCreated", "-ExifIFD:CreateDate",
        "-IFD0:Orientation", "-XMP-tiff:Orientation",
        "-Composite:GPSLatitude", "-Composite:GPSLongitude", "-IFD0:Make", "-IFD0:Model", "-Composite:ImageSize",
    ];

    // Placeholder descriptions some cameras write into every photo.
    private static readonly HashSet<string> JunkCaptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "OLYMPUS DIGITAL CAMERA", "SONY DSC", "DCIM", "Default", "KONICA MINOLTA DIGITAL CAMERA", "Exif_JPEG_PICTURE",
    };

    public async Task<PhotoMetadata> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        var results = await ReadAsync([path], cancellationToken).ConfigureAwait(false);
        return results.TryGetValue(path, out var metadata)
            ? metadata
            : throw new ExifToolException($"Could not read metadata from {path}.");
    }

    /// <summary>Reads several files in one ExifTool call. Files ExifTool cannot read are left out.</summary>
    public async Task<IReadOnlyDictionary<string, PhotoMetadata>> ReadAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, PhotoMetadata>(PathComparer);
        if (paths.Count == 0)
            return result;

        var args = new List<string> { "-json", "-n", "-G1" };
        args.AddRange(ReadTags);
        args.AddRange(paths);

        var output = await exifTool.RunAsync(args, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(output.Output))
            return result;

        // ExifTool reports files with forward slashes on Windows; match them back to the caller's paths.
        var byKey = paths.ToDictionary(KeyOf, p => p, PathComparer);

        using var doc = JsonDocument.Parse(output.Output);
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var source = GetString(item, "SourceFile");
            if (source is null || !byKey.TryGetValue(KeyOf(source), out var originalPath))
                continue;
            if (item.TryGetProperty("Error", out _))
                continue;
            result[originalPath] = Parse(originalPath, item);
        }
        return result;
    }

    public async Task WriteAsync(string path, MetadataChanges changes, CancellationToken cancellationToken = default)
    {
        if (changes.IsEmpty)
            return;

        var args = BuildWriteArguments(path, changes);
        var output = await exifTool.RunAsync(args, cancellationToken).ConfigureAwait(false);

        var errors = output.ErrorLines.ToList();
        if (errors.Count > 0)
            throw new ExifToolException($"Could not save {Path.GetFileName(path)}: {string.Join(" ", errors)}");
    }

    internal static List<string> BuildWriteArguments(string path, MetadataChanges changes)
    {
        var hasIptc = HasIptc(path);
        var args = new List<string>
        {
            // Rewrite the file in place (keeps Windows creation date and attributes), keep the
            // modified date so "sort by date modified" in Explorer is not disturbed, and
            // tolerate minor format problems many older files have.
            "-overwrite_original_in_place", "-P", "-m",
        };

        if (changes.Tags is { } tags)
        {
            tags = TagList.Normalize(tags);
            if (tags.Count == 0)
            {
                args.AddRange(["-XMP-dc:Subject=", "-XMP-microsoft:LastKeywordXMP=", "-IFD0:XPKeywords="]);
                if (hasIptc)
                    args.Add("-IPTC:Keywords=");
            }
            else
            {
                foreach (var tag in tags)
                {
                    args.Add($"-XMP-dc:Subject={tag}");
                    args.Add($"-XMP-microsoft:LastKeywordXMP={tag}");
                    if (hasIptc)
                        args.Add($"-IPTC:Keywords={tag}");
                }
                args.Add($"-IFD0:XPKeywords={string.Join(";", tags)}");
            }
        }

        if (changes.Rating is { } rating)
        {
            rating = Math.Clamp(rating, 0, 5);
            if (rating == 0)
            {
                args.AddRange(["-XMP-xmp:Rating=", "-XMP-microsoft:RatingPercent=", "-IFD0:Rating=", "-IFD0:RatingPercent="]);
            }
            else
            {
                var percent = RatingToPercent(rating).ToString(CultureInfo.InvariantCulture);
                args.Add($"-XMP-xmp:Rating={rating}");
                args.Add($"-XMP-microsoft:RatingPercent={percent}");
                args.Add($"-IFD0:Rating={rating}");
                args.Add($"-IFD0:RatingPercent={percent}");
            }
        }

        if (changes.Caption is { } caption)
        {
            caption = caption.Trim();
            args.Add($"-XMP-dc:Title={caption}");
            args.Add($"-XMP-dc:Description={caption}");
            args.Add($"-IFD0:XPTitle={caption}");
            args.Add($"-IFD0:ImageDescription={caption}");
            if (hasIptc)
                args.Add($"-IPTC:Caption-Abstract={caption}");
        }

        if (changes.DateTaken is { } date)
        {
            var exif = date.ToExifDate();
            var offset = date.ToOffsetString();
            args.Add($"-ExifIFD:DateTimeOriginal={exif}");
            args.Add($"-ExifIFD:OffsetTimeOriginal={offset ?? ""}");
            args.Add($"-XMP-exif:DateTimeOriginal={exif}{offset}");
            args.Add($"-XMP-photoshop:DateCreated={exif}{offset}");
        }

        if (changes.ShiftCreateDateBy is { } shift && shift != TimeSpan.Zero)
        {
            // ExifTool's date shift syntax: "Y:M:D H:M:S", added with += or subtracted with -=.
            var abs = shift.Duration();
            var amount = $"0:0:{abs.Days} {abs.Hours}:{abs.Minutes}:{abs.Seconds}";
            var op = shift < TimeSpan.Zero ? "-=" : "+=";
            args.Add($"-ExifIFD:CreateDate{op}{amount}");
            args.Add($"-XMP-xmp:CreateDate{op}{amount}");
            if (changes.DateTaken?.ToOffsetString() is { } digitizedOffset)
                args.Add($"-ExifIFD:OffsetTimeDigitized={digitizedOffset}");
        }

        if (changes.Orientation is { } orientation)
        {
            if (!SupportedFormats.CanRotateLosslessly(path))
                throw new NotSupportedException($"Rotating {Path.GetExtension(path)} files is not supported yet.");
            args.Add($"-IFD0:Orientation#={Orientation.Normalize(orientation)}");
        }

        // IPTC needs to be told its text is UTF-8, or non-ASCII tags and captions are garbled in other apps.
        if (hasIptc && (changes.Tags is not null || changes.Caption is not null))
            args.Add("-IPTC:CodedCharacterSet=UTF8");

        args.Add(path);
        return args;
    }

    // IPTC only belongs in JPEG and TIFF; other formats keep tags and captions in XMP and EXIF.
    private static bool HasIptc(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".jpe" or ".tif" or ".tiff";

    // The percentages Windows itself writes for 1-5 stars.
    private static int RatingToPercent(int stars) => stars switch
    {
        1 => 1,
        2 => 25,
        3 => 50,
        4 => 75,
        _ => 99,
    };

    private static int PercentToRating(double percent) => percent switch
    {
        <= 0 => 0,
        < 13 => 1,
        < 38 => 2,
        < 63 => 3,
        < 88 => 4,
        _ => 5,
    };

    internal static PhotoMetadata Parse(string path, JsonElement item)
    {
        var tags = GetList(item, "XMP-dc:Subject")
            .Concat(GetList(item, "IPTC:Keywords"))
            .ToList();
        if (tags.Count == 0)
            tags.AddRange(GetList(item, "XMP-microsoft:LastKeywordXMP"));
        if (tags.Count == 0)
            tags.AddRange(TagList.Parse(GetString(item, "IFD0:XPKeywords")));

        var rating = 0;
        if (GetNumber(item, "XMP-xmp:Rating") is { } xmpRating)
            rating = (int)Math.Round(xmpRating);
        else if (GetNumber(item, "IFD0:Rating") is { } exifRating)
            rating = (int)Math.Round(exifRating);
        else if (GetNumber(item, "XMP-microsoft:RatingPercent") is { } percent)
            rating = PercentToRating(percent);

        var caption = new[] { "XMP-dc:Title", "XMP-dc:Description", "IFD0:XPTitle", "IFD0:ImageDescription", "IPTC:Caption-Abstract" }
            .Select(key => GetString(item, key)?.Trim())
            .FirstOrDefault(c => !string.IsNullOrEmpty(c) && !JunkCaptions.Contains(c));

        int? width = null, height = null;
        if (GetString(item, "Composite:ImageSize") is { } size)
        {
            var parts = size.Split([' ', 'x'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 &&
                int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var w) &&
                int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h))
            {
                width = w;
                height = h;
            }
        }

        var orientation = (int)(GetNumber(item, "IFD0:Orientation") ?? GetNumber(item, "XMP-tiff:Orientation") ?? 1);

        return new PhotoMetadata
        {
            Path = path,
            Tags = TagList.Normalize(tags),
            Rating = Math.Clamp(rating, 0, 5),
            Caption = caption,
            DateTaken = ParseDateTaken(item),
            Orientation = Orientation.Normalize(orientation),
            Width = width,
            Height = height,
            Latitude = GetNumber(item, "Composite:GPSLatitude"),
            Longitude = GetNumber(item, "Composite:GPSLongitude"),
            Camera = CameraName(GetString(item, "IFD0:Make"), GetString(item, "IFD0:Model")),
        };
    }

    /// <summary>
    /// Combines make and model the way people say them: "Apple" + "iPhone 15 Pro" gives "Apple iPhone 15 Pro",
    /// while "Canon" + "Canon EOS R6" stays "Canon EOS R6".
    /// </summary>
    internal static string? CameraName(string? make, string? model)
    {
        make = make?.Trim();
        model = model?.Trim();
        if (string.IsNullOrEmpty(model))
            return string.IsNullOrEmpty(make) ? null : make;
        if (string.IsNullOrEmpty(make))
            return model;

        // Makers often write their full company name ("NIKON CORPORATION") but only the brand in the model.
        var brand = make.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        return model.StartsWith(brand, StringComparison.OrdinalIgnoreCase) ? model : $"{make} {model}";
    }

    private static DateTaken? ParseDateTaken(JsonElement item)
    {
        DateTaken? xmp = DateTaken.TryParse(GetString(item, "XMP-exif:DateTimeOriginal"), out var x) ? x : null;

        if (DateTaken.TryParse(GetString(item, "ExifIFD:DateTimeOriginal"), out var exif))
        {
            if (DateTaken.TryParseOffset(GetString(item, "ExifIFD:OffsetTimeOriginal"), out var offset))
                return exif with { Offset = offset };
            // Older files may only carry the offset in the XMP copy of the same date.
            if (xmp is { Offset: not null } && xmp.Value.Local == exif.Local)
                return exif with { Offset = xmp.Value.Offset };
            return exif;
        }

        if (xmp is not null)
            return xmp;
        if (DateTaken.TryParse(GetString(item, "XMP-photoshop:DateCreated"), out var created))
            return created;
        if (DateTaken.TryParse(GetString(item, "ExifIFD:CreateDate"), out var digitized))
            return digitized;
        return null;
    }

    private static string? GetString(JsonElement item, string key)
    {
        if (!item.TryGetProperty(key, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.Array => string.Join(", ", value.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText())),
            _ => null,
        };
    }

    private static IEnumerable<string> GetList(JsonElement item, string key)
    {
        if (!item.TryGetProperty(key, out var value))
            return [];
        return value.ValueKind switch
        {
            JsonValueKind.Array => value.EnumerateArray()
                .Select(v => v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.GetRawText())
                .ToList(),
            JsonValueKind.String => [value.GetString() ?? ""],
            JsonValueKind.Number => [value.GetRawText()],
            _ => [],
        };
    }

    private static double? GetNumber(JsonElement item, string key)
    {
        if (!item.TryGetProperty(key, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.Number)
            return value.GetDouble();
        if (value.ValueKind == JsonValueKind.String &&
            double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            return d;
        return null;
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static string KeyOf(string path) => path.Replace('\\', '/');
}
