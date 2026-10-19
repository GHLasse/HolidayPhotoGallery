using System.Globalization;
using System.Text.Json;
using PhotoGallery.Core.ExifTool;
using PhotoGallery.Core.Faces;

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
        "-XMP-xmp:Rating", "-XMP-microsoft:RatingPercent", "-IFD0:Rating", "-XMP-digiKam:PickLabel",
        "-XMP-dc:Title", "-XMP-dc:Description", "-IFD0:XPTitle", "-IFD0:ImageDescription", "-IPTC:Caption-Abstract",
        "-ExifIFD:DateTimeOriginal", "-ExifIFD:OffsetTimeOriginal", "-XMP-exif:DateTimeOriginal",
        "-XMP-photoshop:DateCreated", "-ExifIFD:CreateDate",
        "-IFD0:Orientation", "-XMP-tiff:Orientation",
        "-Composite:GPSLatitude", "-Composite:GPSLongitude", "-IFD0:Make", "-IFD0:Model", "-Composite:ImageSize",
        "-XMP-photoshop:Country", "-XMP-iptcCore:CountryCode", "-XMP-photoshop:State", "-XMP-photoshop:City", "-XMP-iptcCore:Location",
        "-IPTC:Country-PrimaryLocationName", "-IPTC:Province-State", "-IPTC:City", "-IPTC:Sub-location",
        $"-{FaceRegions.MwgTag}", $"-{FaceRegions.MicrosoftTag}",
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

        // -struct keeps each face region together instead of splitting it into parallel lists.
        var args = new List<string> { "-json", "-n", "-G1", "-struct" };
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

        if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly))
            throw new ExifToolException($"Could not save {Path.GetFileName(path)}: the file is read-only. Use \"Make editable\" to unlock the photos in this folder.");

        // People tags are rewritten as a whole, keeping the file's other regions; Windows' copy of them
        // is measured on the upright photo, so it changes when the photo is rotated.
        var current = changes.People is not null || changes.Orientation is not null
            ? await ReadAsync(path, cancellationToken).ConfigureAwait(false)
            : null;
        var args = BuildWriteArguments(path, changes, current);
        for (var attempt = 1; ; attempt++)
        {
            await RemoveStaleTempFileAsync(path, cancellationToken).ConfigureAwait(false);
            var output = await exifTool.RunAsync(args, cancellationToken).ConfigureAwait(false);
            // ExifTool ignores a failure to delete its temp file after a successful save; tidy up so the
            // next save of this photo is not blocked.
            try
            {
                await RemoveStaleTempFileAsync(path, cancellationToken).ConfigureAwait(false);
            }
            catch (ExifToolException)
            {
                // Still locked: the save itself worked, and the next save tries again.
            }

            var errors = output.ErrorLines.ToList();
            if (errors.Count == 0)
                return;

            // Windows briefly locks a file that was just changed, e.g. while a virus scanner checks it,
            // so a second save of the same photo straight after the first can fail. Wait and try again.
            if (attempt < WriteAttempts && errors.Any(IsFileLockedError))
            {
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var reason = string.Join(" ", errors);
            if (errors.Any(IsFileLockedError))
                reason += " Another program may have the photo open; close it and try again.";
            throw new ExifToolException($"Could not save {Path.GetFileName(path)}: {reason}");
        }
    }

    /// <summary>
    /// Copies all metadata from <paramref name="source"/> into a freshly encoded <paramref name="target"/>
    /// whose pixels are already upright and <paramref name="width"/> × <paramref name="height"/>. The old
    /// embedded thumbnail, colour profile and regions are left out, since they describe the old pixels.
    /// </summary>
    public async Task CopyMetadataAsync(string source, string target, int width, int height, CancellationToken cancellationToken = default)
    {
        var output = await exifTool.RunAsync(
        [
            "-overwrite_original", "-m",
            "-tagsFromFile", source, "-all:all", "--IFD1:all", "--PreviewImage", "--ThumbnailImage",
            // Face and other regions are measured on the old pixels; the people are moved separately.
            "--XMP-mwg-rs:all", "--XMP-MP:all",
            "-IFD0:Orientation#=1", "-XMP-tiff:Orientation=",
            $"-ExifIFD:ExifImageWidth={width}", $"-ExifIFD:ExifImageHeight={height}",
            target,
        ], cancellationToken).ConfigureAwait(false);
        var errors = output.ErrorLines.ToList();
        if (errors.Count > 0)
            throw new ExifToolException($"Could not copy the metadata of {Path.GetFileName(source)}: {string.Join(" ", errors)}");
    }

    private const int WriteAttempts = 5;

    private static bool IsFileLockedError(string error) =>
        error.Contains("for writing", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("Temporary file already exists", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("Error renaming", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// ExifTool writes to "photo.jpg_exiftool_tmp" first and refuses to start if that file exists.
    /// One is left behind when a save was interrupted, or when Windows still had it open (e.g. a virus
    /// scanner) at the moment ExifTool tried to delete it. This app saves one file at a time, so an
    /// existing one is always stale. Deleting is retried briefly in case it is still locked.
    /// </summary>
    private static async Task RemoveStaleTempFileAsync(string path, CancellationToken cancellationToken)
    {
        var temp = path + "_exiftool_tmp";
        for (var attempt = 1; File.Exists(temp); attempt++)
        {
            try
            {
                File.SetAttributes(temp, FileAttributes.Normal);
                File.Delete(temp);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 10)
                    throw new ExifToolException($"Could not save {Path.GetFileName(path)}: the leftover file {Path.GetFileName(temp)} cannot be removed ({ex.Message}).");
                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    internal static List<string> BuildWriteArguments(string path, MetadataChanges changes, PhotoMetadata? current = null)
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

        // The flag is digiKam's pick label "accepted" (3), the nearest thing to Lightroom's pick flag that
        // other programs can read. Unflagging only removes that value, so a reject mark from elsewhere stays.
        if (changes.Flagged is { } flagged)
            args.Add(flagged ? "-XMP-digiKam:PickLabel=3" : "-XMP-digiKam:PickLabel-=3");

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

        if (changes.RemoveLocation)
            args.AddRange(["-GPS:All=", "-XMP-exif:GPS*="]);

        if (changes.Location is { } location)
        {
            if (!location.IsValid)
                throw new ArgumentOutOfRangeException(nameof(changes), $"{location} is not a valid GPS position.");
            // EXIF stores the distance from the equator and meridian plus a hemisphere letter; XMP keeps the sign.
            static string Degrees(double value) => value.ToString("0.0000000", CultureInfo.InvariantCulture);
            args.Add($"-GPS:GPSLatitude={Degrees(Math.Abs(location.Latitude))}");
            args.Add($"-GPS:GPSLatitudeRef={(location.Latitude < 0 ? "S" : "N")}");
            args.Add($"-GPS:GPSLongitude={Degrees(Math.Abs(location.Longitude))}");
            args.Add($"-GPS:GPSLongitudeRef={(location.Longitude < 0 ? "W" : "E")}");
            args.Add($"-XMP-exif:GPSLatitude={Degrees(location.Latitude)}");
            args.Add($"-XMP-exif:GPSLongitude={Degrees(location.Longitude)}");
        }

        if (changes.Place is { } place)
        {
            // The IPTC Core fields in XMP, which Lightroom, Bridge and digiKam use, plus the older IPTC
            // fields in JPEG and TIFF. An empty value removes the field.
            static string Clean(string? value) => value?.Trim() ?? "";
            args.Add($"-XMP-photoshop:Country={Clean(place.Country)}");
            args.Add($"-XMP-iptcCore:CountryCode={Clean(place.CountryCode).ToUpperInvariant()}");
            args.Add($"-XMP-photoshop:State={Clean(place.State)}");
            args.Add($"-XMP-photoshop:City={Clean(place.City)}");
            args.Add($"-XMP-iptcCore:Location={Clean(place.Sublocation)}");
            if (hasIptc)
            {
                args.Add($"-IPTC:Country-PrimaryLocationName={Clean(place.Country)}");
                args.Add($"-IPTC:Province-State={Clean(place.State)}");
                args.Add($"-IPTC:City={Clean(place.City)}");
                args.Add($"-IPTC:Sub-location={Clean(place.Sublocation)}");
            }
        }

        var people = changes.People ?? (changes.Orientation is not null && current is { People.Count: > 0 } ? current.People : null);
        if (people is not null)
        {
            args.AddRange(FaceRegions.WriteArguments(
                people, current?.OtherRegions ?? [], current?.Width, current?.Height,
                changes.Orientation ?? current?.Orientation ?? 1));
        }

        // IPTC needs to be told its text is UTF-8, or non-ASCII tags and captions are garbled in other apps.
        if (hasIptc && (changes.Tags is not null || changes.Caption is not null || changes.Place is not null))
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
        var (people, otherRegions) = FaceRegions.Read(item, Orientation.Normalize(orientation));

        return new PhotoMetadata
        {
            Path = path,
            Tags = TagList.Normalize(tags),
            Rating = Math.Clamp(rating, 0, 5),
            Flagged = GetNumber(item, "XMP-digiKam:PickLabel") == 3,
            Caption = caption,
            DateTaken = ParseDateTaken(item),
            Orientation = Orientation.Normalize(orientation),
            Width = width,
            Height = height,
            Latitude = GetNumber(item, "Composite:GPSLatitude"),
            Longitude = GetNumber(item, "Composite:GPSLongitude"),
            Camera = CameraName(GetString(item, "IFD0:Make"), GetString(item, "IFD0:Model")),
            Place = ParsePlace(item),
            People = people,
            OtherRegions = otherRegions,
        };
    }

    private static PlaceName? ParsePlace(JsonElement item)
    {
        string? First(params string[] keys) =>
            keys.Select(k => GetString(item, k)?.Trim()).FirstOrDefault(v => !string.IsNullOrEmpty(v));

        var place = new PlaceName
        {
            Country = First("XMP-photoshop:Country", "IPTC:Country-PrimaryLocationName"),
            CountryCode = First("XMP-iptcCore:CountryCode"),
            State = First("XMP-photoshop:State", "IPTC:Province-State"),
            City = First("XMP-photoshop:City", "IPTC:City"),
            Sublocation = First("XMP-iptcCore:Location", "IPTC:Sub-location"),
        };
        return place.IsEmpty ? null : place;
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
