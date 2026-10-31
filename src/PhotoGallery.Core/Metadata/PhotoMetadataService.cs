using System.Globalization;
using System.Text.Json;
using PhotoGallery.Core.ExifTool;
using PhotoGallery.Core.Faces;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.Core.Metadata;

/// <summary>
/// Reads and writes photo metadata in the image files themselves, through ExifTool.
/// Values are written to every place common Windows tools look (XMP, EXIF, IPTC and the
/// Microsoft tags Windows Explorer and Windows Live Photo Gallery use), so they stay in sync.
/// </summary>
public sealed class PhotoMetadataService(ExifToolProcess exifTool)
{
    /// <summary>
    /// A save was refused because the file's name claims another format than it has (a JPEG called .png), so
    /// the app can note it centrally and offer Fix extension. Raised on a background thread.
    /// </summary>
    public event Action<string>? WrongFormatFound;

    private static readonly string[] ReadTags =
    [
        "-XMP-dc:Subject", "-IPTC:Keywords", "-XMP-microsoft:LastKeywordXMP", "-IFD0:XPKeywords",
        "-XMP-lr:HierarchicalSubject", "-XMP-digiKam:TagsList",
        "-XMP-xmp:Rating", "-XMP-microsoft:RatingPercent", "-IFD0:Rating", "-XMP-digiKam:PickLabel",
        "-XMP-dc:Title", "-XMP-dc:Description", "-IFD0:XPTitle", "-IFD0:ImageDescription", "-IPTC:Caption-Abstract",
        "-ExifIFD:DateTimeOriginal", "-ExifIFD:OffsetTimeOriginal", "-XMP-exif:DateTimeOriginal",
        "-XMP-photoshop:DateCreated", "-ExifIFD:CreateDate",
        "-IFD0:Orientation", "-XMP-tiff:Orientation",
        "-Composite:GPSLatitude", "-Composite:GPSLongitude", "-IFD0:Make", "-IFD0:Model", "-Composite:ImageSize",
        "-XMP-photoshop:Country", "-XMP-iptcCore:CountryCode", "-XMP-photoshop:State", "-XMP-photoshop:City", "-XMP-iptcCore:Location",
        "-IPTC:Country-PrimaryLocationName", "-IPTC:Province-State", "-IPTC:City", "-IPTC:Sub-location",
        "-IFD0:Copyright", "-XMP-dc:Rights", "-IPTC:CopyrightNotice", "-IFD0:Artist", "-XMP-dc:Creator", "-IPTC:By-line",
        $"-{FaceRegions.MwgTag}", $"-{FaceRegions.MicrosoftTag}", $"-{AppNamespace.AutoTagsField}",
        // Videos (F-4): their own dates, length, turn, camera and place.
        "-Keys:CreationDate", "-QuickTime:CreateDate", "-QuickTime:Duration", "-Composite:Duration", "-Composite:Rotation", "-VideoFrameRate",
        "-Keys:Make", "-Keys:Model", "-UserData:Make", "-UserData:Model", "-XMP-exif:GPSLatitude", "-XMP-exif:GPSLongitude",
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
            : throw new ExifToolException(Tr("Could not read metadata from {0}.", path));
    }

    /// <summary>
    /// The largest small copy of the photo that the camera stored in the file (preview or thumbnail), as an
    /// encoded image, or null. It sits at the start of the file, so it usually survives when the end of the
    /// file is lost. It is stored the way the photo's pixels are, before turning it upright.
    /// </summary>
    public async Task<byte[]?> ReadEmbeddedPreviewAsync(string path, CancellationToken cancellationToken = default)
    {
        // With -json, binary values come back as "base64:…".
        var output = await exifTool.RunAsync(["-json", "-b", "-PreviewImage", "-ThumbnailImage", "-JpgFromRaw", path], cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(output.Output))
            return null;
        byte[]? best = null;
        try
        {
            using var json = JsonDocument.Parse(output.Output);
            foreach (var file in json.RootElement.EnumerateArray())
            {
                foreach (var tag in file.EnumerateObject())
                {
                    if (tag.Name == "SourceFile" || tag.Value.ValueKind != JsonValueKind.String ||
                        tag.Value.GetString() is not { } text || !text.StartsWith("base64:", StringComparison.Ordinal))
                        continue;
                    var bytes = Convert.FromBase64String(text["base64:".Length..]);
                    if (bytes.Length > (best?.Length ?? 0))
                        best = bytes;
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            return null;
        }
        return best;
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
        // Videos ExifTool can't write keep their details in an XMP sidecar, read along with the video.
        var withSidecar = paths.Select(p => (Path: p, Sidecar: SupportedFormats.MetadataTarget(p)))
            .Where(p => p.Sidecar != p.Path && File.Exists(p.Sidecar))
            .ToList();
        args.AddRange(withSidecar.Select(p => p.Sidecar));
        var sidecars = withSidecar.ToDictionary(p => KeyOf(p.Sidecar), p => p.Path, PathComparer);

        var output = await exifTool.RunAsync(args, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(output.Output))
            return result;

        // ExifTool reports files with forward slashes on Windows; match them back to the caller's paths.
        var byKey = paths.ToDictionary(KeyOf, p => p, PathComparer);

        using var doc = JsonDocument.Parse(output.Output);
        var items = new Dictionary<string, JsonElement>(PathComparer);
        var sidecarItems = new Dictionary<string, JsonElement>(PathComparer);
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var source = GetString(item, "SourceFile");
            if (source is null || item.TryGetProperty("Error", out _))
                continue;
            if (sidecars.TryGetValue(KeyOf(source), out var owner))
                sidecarItems[owner] = item;
            else if (byKey.TryGetValue(KeyOf(source), out var originalPath))
                items[originalPath] = item;
        }
        foreach (var (path, item) in items)
            result[path] = Parse(path, sidecarItems.TryGetValue(path, out var sidecar) ? Merge(item, sidecar) : item);
        return result;
    }

    // The video's own values with the sidecar's on top.
    private static JsonElement Merge(JsonElement video, JsonElement sidecar)
    {
        var merged = new Dictionary<string, JsonElement>();
        foreach (var property in video.EnumerateObject())
            merged[property.Name] = property.Value;
        foreach (var property in sidecar.EnumerateObject().Where(p => p.Name != "SourceFile"))
            merged[property.Name] = property.Value;
        return JsonSerializer.SerializeToElement(merged);
    }

    /// <summary>
    /// How the app last read a file (from the index), so a save can find out that another PC or program changed
    /// it since (M-6); null when not known.
    /// </summary>
    public Func<string, Library.IndexedPhoto?>? BasisFor { get; set; }

    /// <summary>
    /// A save found the file changed by someone else and merged: the path, and the fields both sides changed
    /// differently (the user's value was saved). Raised on a background thread.
    /// </summary>
    public event Action<string, IReadOnlyList<string>>? ChangedElsewhere;

    public async Task WriteAsync(string path, MetadataChanges changes, CancellationToken cancellationToken = default)
    {
        if (changes.IsEmpty)
            return;
        if (BasisFor?.Invoke(path) is { } basis && SaveCheck.ChangedSince(basis, new FileInfo(path)))
        {
            var theirs = await ReadAsync(path, cancellationToken).ConfigureAwait(false);
            changes = SaveCheck.Merge(basis, theirs, changes, out var clashes);
            ChangedElsewhere?.Invoke(path, clashes);
        }
        if (SupportedFormats.IsVideo(path) && (changes.Orientation is not null || changes.People is not null))
            throw new NotSupportedException(Tr("Videos can't be turned or have people marked yet."));

        if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly))
            throw new ExifToolException(Tr("Could not save {0}: the file is read-only. Use \"Make editable\" to unlock the photos in this folder.", Path.GetFileName(path)));
        var target = SupportedFormats.MetadataTarget(path);

        // People tags are rewritten as a whole, keeping the file's other regions; Windows' copy of them
        // is measured on the upright photo, so it changes when the photo is rotated.
        var current = changes.People is not null || changes.Orientation is not null
            ? await ReadAsync(path, cancellationToken).ConfigureAwait(false)
            : null;
        // Tag trees (M-5): a tag list from before the app knew trees ("Seagull" for "Animals/Birds/Seagull")
        // keeps the file's tree, and a file without trees gets no tree fields.
        IReadOnlyList<string>? fileTrees = null;
        if (changes.Tags is { } newTags)
        {
            fileTrees = current?.Tags.Where(TagTree.IsPath).ToList() ?? await ReadTreesAsync(target, cancellationToken).ConfigureAwait(false);
            if (fileTrees.Count > 0)
                changes = changes with { Tags = TagTree.KeepTrees(newTags, fileTrees) };
        }
        var args = BuildWriteArguments(path, changes, current, fileTrees);
        for (var attempt = 1; ; attempt++)
        {
            await RemoveStaleTempFileAsync(target, cancellationToken).ConfigureAwait(false);
            var output = await exifTool.RunAsync(args, cancellationToken).ConfigureAwait(false);
            // ExifTool ignores a failure to delete its temp file after a successful save; tidy up so the
            // next save of this photo is not blocked.
            try
            {
                await RemoveStaleTempFileAsync(target, cancellationToken).ConfigureAwait(false);
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
            if (WrongFormat(reason) is var (claimed, real) && real is not null)
            {
                WrongFormatFound?.Invoke(path);
                reason = Tr("the name ends in .{0}, but the file is really a {1}. Choose Repair to give it the right extension, .{2}, then try again.",
                    claimed!.ToLowerInvariant(), real, real.ToLowerInvariant());
            }
            if (errors.Any(IsFileLockedError))
                reason += " " + Tr("Another program may have the photo open; close it and try again.");
            throw new ExifToolException(Tr("Could not save {0}: {1}", Path.GetFileName(path), reason));
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
            throw new ExifToolException(Tr("Could not copy the metadata of {0}: {1}", Path.GetFileName(source), string.Join(" ", errors)));
    }

    private const int WriteAttempts = 5;

    // ExifTool goes by the file's name: a JPEG called .png is refused with "Not a valid PNG (looks
    // more like a JPEG)". Phone transfers and downloads do produce such files, so say what is wrong
    // rather than passing ExifTool's wording on.
    internal static (string? Claimed, string? Real) WrongFormat(string error)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            error, @"Not a valid (\w+) \(looks more like an? (\w+)\)");
        return match.Success ? (match.Groups[1].Value, match.Groups[2].Value) : (null, null);
    }

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
                    throw new ExifToolException(Tr("Could not save {0}: the leftover file {1} cannot be removed ({2}).", Path.GetFileName(path), Path.GetFileName(temp), ex.Message));
                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    // The tag trees in the file, as the app's paths ("Animals/Birds/Seagull").
    private async Task<IReadOnlyList<string>> ReadTreesAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return [];
        var output = await exifTool.RunAsync(["-json", "-G1", "-XMP-lr:HierarchicalSubject", "-XMP-digiKam:TagsList", path], cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(output.Output))
            return [];
        try
        {
            using var json = JsonDocument.Parse(output.Output);
            return json.RootElement.EnumerateArray().SelectMany(item =>
                    GetList(item, "XMP-lr:HierarchicalSubject").Select(TagTree.FromLightroom)
                        .Concat(GetList(item, "XMP-digiKam:TagsList").Select(TagTree.FromDigiKam)))
                .Where(TagTree.IsPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <param name="fileTrees">The tag trees the file has; null when unknown, then the tree fields are always rewritten.</param>
    internal static List<string> BuildWriteArguments(string path, MetadataChanges changes, PhotoMetadata? current = null,
                                                     IReadOnlyList<string>? fileTrees = null)
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
            // Tag trees (M-5): the plain fields get each part, Windows' own field and the tree fields the paths.
            var plain = TagTree.Plain(tags);
            var paths = TagTree.Paths(tags);
            if (tags.Count == 0)
            {
                args.AddRange(["-XMP-dc:Subject=", "-XMP-microsoft:LastKeywordXMP=", "-IFD0:XPKeywords="]);
                if (hasIptc)
                    args.Add("-IPTC:Keywords=");
            }
            else
            {
                foreach (var tag in plain)
                {
                    args.Add($"-XMP-dc:Subject={tag}");
                    if (hasIptc)
                        args.Add($"-IPTC:Keywords={tag}");
                }
                foreach (var tag in tags)
                    args.Add($"-XMP-microsoft:LastKeywordXMP={tag}");
                args.Add($"-IFD0:XPKeywords={string.Join(";", plain)}");
            }
            // Only touched when there is a tree now or was one, so files without trees get no new fields.
            if (paths.Count > 0 || fileTrees is null || fileTrees.Count > 0)
            {
                args.AddRange(["-XMP-lr:HierarchicalSubject=", "-XMP-digiKam:TagsList="]);
                foreach (var tree in paths)
                {
                    args.Add($"-XMP-lr:HierarchicalSubject={TagTree.ToLightroom(tree)}");
                    args.Add($"-XMP-digiKam:TagsList={tree}");
                }
            }
        }

        if (changes.AutoTags is { } autoTags)
        {
            args.Add($"-{AppNamespace.AutoTagsField}=");
            foreach (var tag in TagList.Normalize(autoTags))
                args.Add($"-{AppNamespace.AutoTagsField}={tag}");
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

        // A reject is digiKam's pick label "rejected" (1), which replaces the flag; the star rating stays.
        // Windows and Adobe write a reject as rating -1, so taking the mark off clears that too.
        if (changes.Rejected is { } rejected)
        {
            if (rejected)
                args.Add("-XMP-digiKam:PickLabel=1");
            else if (changes.Rating is null)
                args.AddRange(["-XMP-digiKam:PickLabel-=1", "-XMP-xmp:Rating-=-1", "-IFD0:Rating-=-1"]);
            else
                args.Add("-XMP-digiKam:PickLabel-=1");
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
            if (changes.SyncOtherDates)
            {
                args.Add($"-ExifIFD:CreateDate={exif}");
                args.Add($"-ExifIFD:OffsetTimeDigitized={offset ?? ""}");
                args.Add($"-XMP-xmp:CreateDate={exif}{offset}");
                args.Add($"-IFD0:ModifyDate={exif}");
                args.Add($"-ExifIFD:OffsetTime={offset ?? ""}");
                args.Add($"-XMP-xmp:ModifyDate={exif}{offset}");
                // The file's own dates, which Explorer shows; only Windows keeps a creation date ExifTool can set.
                args.Add($"-FileModifyDate={exif}{offset}");
                if (OperatingSystem.IsWindows())
                    args.Add($"-FileCreateDate={exif}{offset}");
            }
        }

        if (changes.ShiftCreateDateBy is { } shift && shift != TimeSpan.Zero && !changes.SyncOtherDates)
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
                throw new NotSupportedException(Tr("Rotating {0} files is not supported yet.", Path.GetExtension(path)));
            args.Add($"-IFD0:Orientation#={Orientation.Normalize(orientation)}");
        }

        if (changes.RemoveLocation)
            args.AddRange(["-GPS:All=", "-XMP-exif:GPS*="]);

        if (changes.Location is { } location)
        {
            if (!location.IsValid)
                throw new ArgumentOutOfRangeException(nameof(changes), Tr("{0} is not a valid GPS position.", location));
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

        if (changes.Camera is { } camera)
        {
            foreach (var (name, value) in camera)
            {
                if (CameraTags.Names.Contains(name))
                    args.Add($"-{name}#={value}");
            }
        }

        if (changes.Copyright is { } copyright)
        {
            copyright = copyright.Trim();
            args.Add($"-IFD0:Copyright={copyright}");
            args.Add($"-XMP-dc:Rights={copyright}");
            if (hasIptc)
                args.Add($"-IPTC:CopyrightNotice={copyright}");
        }

        if (changes.Creator is { } creator)
        {
            creator = creator.Trim();
            args.Add($"-IFD0:Artist={creator}");
            args.Add($"-XMP-dc:Creator={creator}");
            if (hasIptc)
                args.Add($"-IPTC:By-line={creator}");
        }

        var people = changes.People ?? (changes.Orientation is not null && current is { People.Count: > 0 } ? current.People : null);
        if (people is not null)
        {
            args.AddRange(FaceRegions.WriteArguments(
                people, current?.OtherRegions ?? [], current?.Width, current?.Height,
                changes.Orientation ?? current?.Orientation ?? 1));
        }

        // IPTC needs to be told its text is UTF-8, or non-ASCII tags and captions are garbled in other apps.
        if (hasIptc && (changes.Tags is not null || changes.Caption is not null || changes.Place is not null ||
                       changes.Copyright is not null || changes.Creator is not null))
            args.Add("-IPTC:CodedCharacterSet=UTF8");

        if (SupportedFormats.IsVideo(path))
            return VideoArguments(path, changes, args);
        args.Add(path);
        return args;
    }

    /// <summary>
    /// A video keeps its details in XMP, in the file (MP4, MOV) or in a sidecar (AVI, MTS…), plus the QuickTime
    /// date and place that phones, Windows and Google Photos read. The photo-only EXIF and IPTC tags are left out.
    /// </summary>
    private static List<string> VideoArguments(string path, MetadataChanges changes, List<string> photoArgs)
    {
        var target = SupportedFormats.MetadataTarget(path);
        var args = photoArgs.Where(a => !a.StartsWith('-') || a.StartsWith("-overwrite", StringComparison.Ordinal) || a is "-P" or "-m" ||
                                         a.StartsWith("-XMP", StringComparison.Ordinal) ||
                                         (target == path && a.StartsWith("-File", StringComparison.Ordinal)))
            .ToList();
        if (target == path)
        {
            if (changes.DateTaken is { } date)
            {
                // QuickTime's own dates are in UTC; a date without a time zone is taken as this PC's.
                var instant = date.Instant ?? new DateTimeOffset(date.Local, TimeZoneInfo.Local.GetUtcOffset(date.Local));
                var utc = instant.UtcDateTime.ToString("yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture);
                args.Add($"-Keys:CreationDate={date.ToExifDate()}{DateTaken.FormatOffset(instant.Offset)}");
                args.Add($"-QuickTime:CreateDate={utc}");
                if (changes.SyncOtherDates)
                    args.Add($"-QuickTime:ModifyDate={utc}");
            }
            if (changes.RemoveLocation)
                args.AddRange(["-Keys:GPSCoordinates=", "-UserData:GPSCoordinates="]);
            if (changes.Location is { } location)
            {
                var text = FormattableString.Invariant($"{location.Latitude:0.0000000}, {location.Longitude:0.0000000}");
                args.Add($"-Keys:GPSCoordinates={text}");
            }
        }
        args.Add(target);
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
        // Tag trees from Lightroom and digiKam (M-5) become one tag with its path.
        var paths = GetList(item, "XMP-lr:HierarchicalSubject").Select(TagTree.FromLightroom)
            .Concat(GetList(item, "XMP-digiKam:TagsList").Select(TagTree.FromDigiKam))
            .Where(p => p.Length > 0).ToList();
        if (paths.Count > 0)
            tags = [.. TagTree.Merge(tags, paths)];

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

        // A phone video filmed upright is stored on its side with a turn to show it at.
        if (GetNumber(item, "Composite:Rotation") is 90 or 270 && width is not null)
            (width, height) = (height, width);

        var orientation = (int)(GetNumber(item, "IFD0:Orientation") ?? GetNumber(item, "XMP-tiff:Orientation") ?? 1);
        var (people, otherRegions) = FaceRegions.Read(item, Orientation.Normalize(orientation));

        return new PhotoMetadata
        {
            Path = path,
            Tags = TagList.Normalize(tags),
            // Only tags the photo still has; the user may have removed one in another app.
            AutoTags = TagList.Normalize(GetList(item, AppNamespace.AutoTagsField)
                .Where(t => tags.Contains(t, StringComparer.OrdinalIgnoreCase))),
            Rating = Math.Clamp(rating, 0, 5),
            Flagged = GetNumber(item, "XMP-digiKam:PickLabel") == 3,
            Rejected = GetNumber(item, "XMP-digiKam:PickLabel") == 1 || rating < 0,
            Caption = caption,
            DateTaken = ParseDateTaken(item),
            Orientation = Orientation.Normalize(orientation),
            Width = width,
            Height = height,
            // A video's sidecar has only the XMP copy of the position.
            Latitude = GetNumber(item, "Composite:GPSLatitude") ?? GetNumber(item, "XMP-exif:GPSLatitude"),
            Longitude = GetNumber(item, "Composite:GPSLongitude") ?? GetNumber(item, "XMP-exif:GPSLongitude"),
            Camera = CameraName(FirstText(item, "IFD0:Make", "Keys:Make", "UserData:Make"), FirstText(item, "IFD0:Model", "Keys:Model", "UserData:Model")),
            Place = ParsePlace(item),
            Copyright = FirstText(item, "XMP-dc:Rights", "IFD0:Copyright", "IPTC:CopyrightNotice"),
            Creator = FirstText(item, "XMP-dc:Creator", "IFD0:Artist", "IPTC:By-line"),
            People = people,
            OtherRegions = otherRegions,
            Duration = SupportedFormats.IsVideo(path) ? GetNumber(item, "QuickTime:Duration") ?? GetNumber(item, "Composite:Duration") : null,
            FrameRate = SupportedFormats.IsVideo(path) ? FrameRateOf(item) : null,
        };
    }

    // The first field that has text; a list (XMP Creator) gives its entries joined.
    private static string? FirstText(JsonElement item, params string[] keys) =>
        keys.Select(k => string.Join("; ", GetList(item, k)).Trim()).FirstOrDefault(v => v.Length > 0);

    /// <summary>
    /// The camera and exposure tags of a photo (<see cref="CameraTags.Names"/>), as numbers so they can be
    /// written into another photo unchanged. Tags the photo doesn't have are left out.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> ReadCameraAsync(string path, CancellationToken cancellationToken = default)
    {
        var args = new List<string> { "-json", "-n", "-G1" };
        args.AddRange(CameraTags.Names.Select(n => "-" + n));
        args.Add(path);
        var output = await exifTool.RunAsync(args, cancellationToken).ConfigureAwait(false);
        return CameraTags.Parse(output.Output);
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
        // A phone's video: the local time with its time zone.
        if (DateTaken.TryParse(GetString(item, "Keys:CreationDate"), out var keys))
            return keys;
        if (DateTaken.TryParse(GetString(item, "XMP-photoshop:DateCreated"), out var created))
            return created;
        if (DateTaken.TryParse(GetString(item, "ExifIFD:CreateDate"), out var digitized))
            return digitized;
        // Other videos only have QuickTime's date in UTC (zero when the camera had no clock), shown in this PC's time zone.
        if (DateTaken.TryParse(GetString(item, "QuickTime:CreateDate"), out var quickTime) && quickTime.Local.Year > 1904)
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(quickTime.Local, DateTimeKind.Utc), TimeZoneInfo.Local);
            return new DateTaken(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), TimeZoneInfo.Local.GetUtcOffset(local));
        }
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

    // The video track's rate: its group depends on the container (Track1 in MP4/MOV, RIFF in AVI, Matroska …).
    private static double? FrameRateOf(JsonElement item) =>
        item.EnumerateObject().Where(p => p.Name.EndsWith(":VideoFrameRate", StringComparison.Ordinal))
            .Select(p => GetNumber(item, p.Name)).FirstOrDefault(r => r is > 0);

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
