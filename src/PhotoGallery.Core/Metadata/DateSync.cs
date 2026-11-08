using System.Globalization;
using System.Text.Json;
using PhotoGallery.Core.ExifTool;

namespace PhotoGallery.Core.Metadata;

/// <summary>A date in a photo that does not read the same as its date taken: the field and what it holds.</summary>
public sealed record DateMismatch(string Field, string Value);

/// <summary>What <see cref="DateSync.CheckAsync"/> found for one photo.</summary>
/// <param name="Taken">The date taken, which wins; null when the photo has none, then nothing is changed.</param>
/// <param name="Mismatches">The dates that read differently (or the date taken missing from its main field).</param>
/// <param name="EarlierDates">The dates a sync replaced before, kept in the file's own note (see <see cref="DateSync.NoteField"/>).</param>
public sealed record DateSyncCheck(string Path, DateTaken? Taken, IReadOnlyList<DateMismatch> Mismatches, string? EarlierDates = null)
{
    public bool NeedsSync => Taken is not null && Mismatches.Count > 0;
}

/// <summary>
/// Keeps the "when it was made" dates in a photo the same as its date taken (the modified dates, which say when
/// the pixels last changed, only when they are earlier), so other apps sort it right whichever date they look
/// at: Google Photos, Apple Photos and Windows read the date taken (EXIF DateTimeOriginal) first, but fall back to
/// the digitized date, the XMP and IPTC dates or the file's own created and modified times when it is missing or
/// stripped, and some (Explorer's Date created, a file shared through a chat or a drive) only show the file's times.
/// The date taken always wins. The dates a sync replaces are kept in the file's own XMP note the first time, so
/// nothing is lost (for a scanned photo the digitized date is the day it was scanned). Used before photos are
/// shared or uploaded and by the background sync (Settings › Dates and places).
/// </summary>
public static class DateSync
{
    /// <summary>The app's XMP field with the dates a sync replaced, as "field=value; …".</summary>
    public const string NoteField = AppNamespace.DatesBeforeSyncField;

    /// <summary>File times within this many seconds count as the same (FAT drives keep only every 2 seconds).</summary>
    private const double FileTolerance = 2;

    internal static readonly string[] ReadTags =
    [
        "-ExifIFD:DateTimeOriginal", "-ExifIFD:OffsetTimeOriginal", "-XMP-exif:DateTimeOriginal", "-XMP-photoshop:DateCreated",
        "-ExifIFD:CreateDate", "-ExifIFD:OffsetTimeDigitized", "-IFD0:ModifyDate", "-ExifIFD:OffsetTime",
        "-XMP-xmp:CreateDate", "-XMP-xmp:ModifyDate", "-IPTC:DateCreated", "-IPTC:TimeCreated",
        "-Keys:CreationDate", "-QuickTime:CreateDate", "-QuickTime:ModifyDate", "-ExifIFD:SubSecTimeOriginal", $"-{NoteField}",
    ];

    // Dates kept as the camera's local clock, compared with the date taken's clock time.
    private static readonly string[] ClockFields =
        ["XMP-exif:DateTimeOriginal", "XMP-photoshop:DateCreated", "ExifIFD:CreateDate", "XMP-xmp:CreateDate"];

    // When the pixels last changed: only wrong when earlier than the date taken (Lasse, 2026-11-08).
    private static readonly string[] ModifyFields = ["IFD0:ModifyDate", "XMP-xmp:ModifyDate"];

    /// <summary>The fields that say when the photo was last changed; a mismatch there raises them to the date taken.</summary>
    internal static readonly IReadOnlySet<string> ModifyFieldNames = new HashSet<string>(ModifyFields) { "File:Modified", "QuickTime:ModifyDate" };

    private static readonly string[] OffsetFields = ["ExifIFD:OffsetTimeDigitized"];

    /// <summary>
    /// Photos, and videos that keep their details in the file (MP4, MOV): a video with a sidecar (AVI, MTS…) is left
    /// as it is, since its own dates can't be written.
    /// </summary>
    public static bool Applies(string path) =>
        SupportedFormats.IsImage(path) || (SupportedFormats.IsVideo(path) && SupportedFormats.MetadataTarget(path) == path);

    /// <summary>Checks the photos and videos (sidecar videos and missing files are left out), reading their dates in one go.</summary>
    public static async Task<IReadOnlyList<DateSyncCheck>> CheckAsync(ExifToolProcess exifTool, IReadOnlyList<string> paths,
                                                                      CancellationToken cancellationToken = default)
    {
        var result = new List<DateSyncCheck>();
        foreach (var chunk in paths.Where(p => Applies(p) && File.Exists(p)).Chunk(100))
        {
            var args = new List<string> { "-json", "-G1", "-m" };
            args.AddRange(ReadTags);
            args.AddRange(chunk);
            var output = await exifTool.RunAsync(args, cancellationToken).ConfigureAwait(false);
            var byKey = chunk.ToDictionary(PhotoMetadataService.KeyOf, p => p, StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(output.Output))
                continue;
            using var doc = JsonDocument.Parse(output.Output);
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (PhotoMetadataService.GetString(item, "SourceFile") is not { } source || item.TryGetProperty("Error", out _) ||
                    !byKey.TryGetValue(PhotoMetadataService.KeyOf(source), out var path))
                    continue;
                var file = new FileInfo(path);
                result.Add(Compare(path, item, OperatingSystem.IsWindows() ? file.CreationTimeUtc : null, file.LastWriteTimeUtc));
            }
        }
        return result;
    }

    /// <summary>
    /// Compares the dates read from a photo with its date taken. <paramref name="createdUtc"/> is the file's
    /// creation time where the system keeps one that can be set (Windows), else null.
    /// </summary>
    internal static DateSyncCheck Compare(string path, JsonElement item, DateTime? createdUtc, DateTime modifiedUtc)
    {
        var taken = PhotoMetadataService.ParseDateTaken(item);
        var note = PhotoMetadataService.GetString(item, NoteField);
        if (taken is not { } date)
            return new DateSyncCheck(path, null, [], note);

        var mismatches = new List<DateMismatch>();
        string? Get(string field) => PhotoMetadataService.GetString(item, field);

        // GIF has no EXIF and videos keep their own dates in QuickTime; both keep the rest in XMP.
        var isVideo = SupportedFormats.IsVideo(path);
        var hasExif = !isVideo && !Path.GetExtension(path).Equals(".gif", StringComparison.OrdinalIgnoreCase);
        if (hasExif)
        {
            if (Get("ExifIFD:DateTimeOriginal") is not { } original)
                mismatches.Add(new("ExifIFD:DateTimeOriginal", ""));
            else if (!SameClock(original, date))
                mismatches.Add(new("ExifIFD:DateTimeOriginal", original));
            if (date.Offset is { } offset && Get("ExifIFD:OffsetTimeOriginal") is var originalOffset &&
                !(DateTaken.TryParseOffset(originalOffset, out var o) && o == offset))
                mismatches.Add(new("ExifIFD:OffsetTimeOriginal", originalOffset ?? ""));
        }
        foreach (var field in ClockFields)
        {
            if (Get(field) is { } value && !SameClock(value, date))
                mismatches.Add(new(field, value));
        }
        foreach (var field in ModifyFields)
        {
            if (Get(field) is { } value && EarlierClock(value, date))
                mismatches.Add(new(field, value));
        }
        if (date.Offset is { } known)
        {
            foreach (var field in OffsetFields)
            {
                if (Get(field) is { } value && !(DateTaken.TryParseOffset(value, out var o) && o == known))
                    mismatches.Add(new(field, value));
            }
        }
        if (isVideo)
            CompareVideo(item, date, mismatches);
        if (Get("IPTC:DateCreated") is { } iptcDate)
        {
            var iptc = Get("IPTC:TimeCreated") is { } time ? $"{iptcDate} {time}" : iptcDate;
            if (!SameClock(iptc, date))
                mismatches.Add(new("IPTC:DateCreated", iptc));
        }
        if (createdUtc is { } created && !SameFileTime(created, date))
            mismatches.Add(new("File:Created", FileTimeText(created)));
        if (FileTimeUtc(date) - DateTime.SpecifyKind(modifiedUtc, DateTimeKind.Utc) > TimeSpan.FromSeconds(FileTolerance))
            mismatches.Add(new("File:Modified", FileTimeText(modifiedUtc)));
        return new DateSyncCheck(path, date, mismatches, note);
    }

    /// <summary>
    /// Sets the photo's dates to its date taken (the "Keep all dates in sync" set: digitized, XMP, IPTC and the
    /// file's creation date; the modified dates only when they are earlier than the date taken). The first time, the dates it replaces are kept in the file's note.
    /// Returns false when there was nothing to do or the file is read-only (left as it is).
    /// </summary>
    /// <exception cref="ExifToolException">ExifTool could not write the file.</exception>
    public static async Task<bool> SyncAsync(PhotoMetadataService metadata, DateSyncCheck check, CancellationToken cancellationToken = default)
    {
        if (!check.NeedsSync || check.Taken is not { } taken || !File.Exists(check.Path) ||
            File.GetAttributes(check.Path).HasFlag(FileAttributes.ReadOnly))
            return false;
        await metadata.WriteAsync(check.Path, new MetadataChanges
        {
            DateTaken = taken,
            SyncOtherDates = true,
            RaiseModifyDates = check.Mismatches.Any(m => ModifyFieldNames.Contains(m.Field)),
            DatesBeforeSync = check.EarlierDates is null ? Note(check.Mismatches) : null,
        }, cancellationToken).ConfigureAwait(false);
        return true;
    }

    // A video's QuickTime dates are UTC (what Google Photos, Windows and most players sort by); Keys:CreationDate,
    // which Apple writes, is the local time with its zone. Zero (1904) is a camera without a clock: missing.
    private static void CompareVideo(JsonElement item, DateTaken date, List<DateMismatch> mismatches)
    {
        var utc = FileTimeUtc(date);
        var created = PhotoMetadataService.GetString(item, "QuickTime:CreateDate");
        if (!DateTaken.TryParse(created, out var quickTime) || quickTime.Local.Year <= 1904)
            mismatches.Add(new("QuickTime:CreateDate", ""));
        else if (Math.Abs((Truncate(quickTime.Local) - Truncate(utc)).TotalSeconds) > 1)
            mismatches.Add(new("QuickTime:CreateDate", created!));
        if (PhotoMetadataService.GetString(item, "QuickTime:ModifyDate") is { } modified &&
            DateTaken.TryParse(modified, out var m) && m.Local.Year > 1904 && Truncate(utc) - Truncate(m.Local) > TimeSpan.FromSeconds(1))
            mismatches.Add(new("QuickTime:ModifyDate", modified));
        if (PhotoMetadataService.GetString(item, "Keys:CreationDate") is { } keys &&
            (!SameClock(keys, date) || (date.Offset is { } o && DateTaken.TryParse(keys, out var k) && k.Offset is { } ko && ko != o)))
            mismatches.Add(new("Keys:CreationDate", keys));
    }

    /// <summary>The replaced dates as kept in the note: "field=value; …" (an empty value: the field was missing).</summary>
    internal static string Note(IEnumerable<DateMismatch> mismatches) =>
        string.Join("; ", mismatches.Where(m => m.Value.Length > 0).Select(m => $"{m.Field}={m.Value}"));

    // Two clock times read the same to the second; a date without a time only needs the same day.
    private static bool SameClock(string text, DateTaken taken)
    {
        if (!DateTaken.TryParse(text, out var value))
            return true; // unreadable or a placeholder: not something another app would sort by
        if (text.Trim().Length <= 10)
            return value.Local.Date == taken.Local.Date;
        return Truncate(value.Local) == Truncate(taken.Local);
    }

    private static bool EarlierClock(string text, DateTaken taken) =>
        DateTaken.TryParse(text, out var value) && Truncate(value.Local) < Truncate(taken.Local) &&
        !(text.Trim().Length <= 10 && value.Local.Date == taken.Local.Date);

    // With a time zone the moment is known; without one ExifTool sets the file time as this PC's local time.
    private static DateTime FileTimeUtc(DateTaken taken) =>
        taken.Instant?.UtcDateTime ?? TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(taken.Local, DateTimeKind.Unspecified), TimeZoneInfo.Local);

    private static bool SameFileTime(DateTime fileUtc, DateTaken taken) =>
        Math.Abs((DateTime.SpecifyKind(fileUtc, DateTimeKind.Utc) - FileTimeUtc(taken)).TotalSeconds) <= FileTolerance;

    private static string FileTimeText(DateTime utc) =>
        new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToLocalTime().ToString("yyyy:MM:dd HH:mm:sszzz", CultureInfo.InvariantCulture);

    private static DateTime Truncate(DateTime t) => new(t.Year, t.Month, t.Day, t.Hour, t.Minute, t.Second);
}
