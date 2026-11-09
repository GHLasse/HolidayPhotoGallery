using PhotoGallery.Core.ExifTool;
using PhotoGallery.Core.Faces;
using PhotoGallery.Core.Metadata;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.Core.Editing;

/// <summary>
/// Saves pixel edits (crop, auto adjust) over the photo while keeping every earlier version in
/// <see cref="EditHistory"/>, and undoes or reverts them. Metadata always survives: an edited file gets
/// the metadata of the version it replaces, and an undone or reverted file gets the tags, rating,
/// caption, date, location and place names the photo had just before.
/// </summary>
public sealed class PhotoEditService(PhotoMetadataService metadata)
{
    /// <summary>
    /// Replaces the photo with an edited version. <paramref name="writeImage"/> writes the new, upright
    /// pixels (without metadata) to the path it is given, in the format of <paramref name="extension"/>.
    /// Returns the photo's path, which changes when the format does (e.g. HEIC saved as JPEG).
    /// <paramref name="crop"/> is the part of the upright photo that was kept, if it was cropped; the people
    /// named in the photo are moved with it, and dropped when they were cut off. <paramref name="turn"/> is the
    /// EXIF orientation of a quarter turn applied to the upright photo (6 clockwise, 8 the other way), so
    /// the people turn with it. <paramref name="metadataFrom"/> is the file to take the metadata from when the
    /// photo's own file can't be read (a copy that replaces it); by default the photo itself.
    /// <paramref name="beforeCrop"/> moves a person's box on the upright photo before the crop, for a photo that
    /// was straightened (<see cref="Straighten.Box"/>); null drops the person.
    /// </summary>
    /// With <paramref name="asCopy"/> the photo is left as it is and the edit goes into a new file next to it
    /// (<see cref="EditedCopyPath"/>), whose path is returned.
    /// <paramref name="colourProfile"/>: the new pixels were left in the photo's own colour space (Display P3, Adobe
    /// RGB…) instead of converted to sRGB, so the colour profile is kept (E-16). <paramref name="geometry"/>: what the
    /// edit did to the photo's shape; with it, an HDR JPEG's gain map gets the same edit and stays (E-16). Without
    /// it, or when the edited file can't carry the gain map, the HDR notes are dropped and the photo becomes SDR.
    /// Lightroom's edit settings are always dropped (see <see cref="InspectAsync"/> to ask first).
    public async Task<string> SaveEditAsync(string path, string extension, int width, int height,
                                            Func<string, Task> writeImage, FaceBox? crop = null, int turn = 1,
                                            string? metadataFrom = null, CancellationToken cancellationToken = default,
                                            Func<FaceBox, FaceBox?>? beforeCrop = null, bool asCopy = false,
                                            bool colourProfile = false, EditGeometry? geometry = null)
    {
        if (!asCopy && File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly))
            throw new ExifToolException(Tr("Could not save {0}: the file is read-only. Use \"Make editable\" to unlock the photos in this folder.", Path.GetFileName(path)));

        var newPath = asCopy
            ? EditedCopyPath(path, extension)
            : string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase)
                ? path
                : Path.ChangeExtension(path, extension);
        if (newPath != path && File.Exists(newPath))
            throw new IOException(Tr("Could not save as {0}: a photo with that name already exists.", Path.GetFileName(newPath)));

        // Written next to the photo under a hidden name, then swapped in, so a failure leaves the photo as it was.
        var temp = Path.Combine(Path.GetDirectoryName(path)!, $".edit-{Guid.NewGuid():N}{extension}");
        var source = metadataFrom ?? path;
        try
        {
            // An HDR JPEG saved as JPEG keeps its gain map, edited the same way (E-16).
            var gainMap = geometry is not null && IsJpeg(source) && IsJpeg(extension)
                ? await Task.Run(() => GainMapJpeg.Find(File.ReadAllBytes(source)), cancellationToken).ConfigureAwait(false)
                : null;
            await writeImage(temp).ConfigureAwait(false);
            await metadata.CopyMetadataAsync(source, temp, width, height, cancellationToken, colourProfile, keepHdr: gainMap is not null).ConfigureAwait(false);
            var before = await metadata.ReadAsync(source, cancellationToken).ConfigureAwait(false);
            await MovePeopleAsync(before, temp, crop, turn, beforeCrop, cancellationToken).ConfigureAwait(false);
            if (IsJpeg(extension))
                await WriteThumbnailAsync(temp, cancellationToken).ConfigureAwait(false);
            if (gainMap is not null)
            {
                var iso = GainMapJpeg.IsoSegment(await File.ReadAllBytesAsync(source, cancellationToken).ConfigureAwait(false));
                var edited = await Task.Run(() => GainMapJpeg.Edit(gainMap, before.Orientation, geometry!), cancellationToken).ConfigureAwait(false);
                var photo = await File.ReadAllBytesAsync(temp, cancellationToken).ConfigureAwait(false);
                await File.WriteAllBytesAsync(temp, GainMapJpeg.Attach(photo, edited, iso), cancellationToken).ConfigureAwait(false);
            }
            // A full disk or a failing drive can leave the new file short; then the photo stays as it was.
            if (Library.FileCheck.Check(temp) != Library.FileHealth.Whole)
                throw new IOException(Tr("The edited file of {0} came out damaged, so the photo was left as it was. Is the disk full or failing?", Path.GetFileName(path)));

            if (asCopy)
            {
                File.Move(temp, newPath, overwrite: false);
                return newPath;
            }
            EditHistory.BackUp(path);
            // The edited file replaces the photo but is a new file: it keeps the photo's creation date (Windows).
            var created = File.GetCreationTimeUtc(path);
            File.Move(temp, newPath, overwrite: true);
            if (OperatingSystem.IsWindows())
                File.SetCreationTimeUtc(newPath, created);
            if (newPath != path)
            {
                File.Delete(path);
                EditHistory.Rename(path, newPath);
            }
            return newPath;
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    /// <summary>
    /// Where an edit saved as a copy goes: next to the photo as "name (edited).ext", then "name (edited 2).ext" and
    /// so on, so no file is ever overwritten. <paramref name="extension"/> is the format of the copy.
    /// </summary>
    public static string EditedCopyPath(string path, string extension)
    {
        var folder = Path.GetDirectoryName(path) ?? "";
        var name = Path.GetFileNameWithoutExtension(path);
        for (var n = 1; ; n++)
        {
            var candidate = Path.Combine(folder, n == 1 ? $"{name} (edited){extension}" : $"{name} (edited {n}){extension}");
            if (!File.Exists(candidate))
                return candidate;
        }
    }

    /// <summary>
    /// The edited photo is upright (orientation 1) and maybe cropped, so the people's boxes are turned and
    /// cropped to match. Other programs' regions are not copied (see CopyMetadataAsync): they describe the old pixels.
    /// </summary>
    private async Task MovePeopleAsync(PhotoMetadata before, string target, FaceBox? crop, int turn, Func<FaceBox, FaceBox?>? beforeCrop,
                                       CancellationToken cancellationToken)
    {
        if (before.People.Count == 0)
            return;
        var people = new List<PersonTag>();
        foreach (var person in before.People)
        {
            var box = person.Box.ToUpright(before.Orientation);
            if (beforeCrop is not null)
            {
                if (beforeCrop(box) is not { } straightened)
                    continue;
                box = straightened;
            }
            if (crop is { } c)
            {
                if (box.AfterCrop(c) is not { } moved)
                    continue;
                box = moved;
            }
            people.Add(person with { Box = box.ToUpright(turn) });
        }
        await metadata.WriteAsync(target, new MetadataChanges { People = people }, cancellationToken, ownReplacement: true).ConfigureAwait(false);
    }

    private static bool IsJpeg(string pathOrExtension) =>
        Path.GetExtension(pathOrExtension).ToLowerInvariant() is ".jpg" or ".jpeg" or ".jpe" ||
        pathOrExtension.ToLowerInvariant() is ".jpg" or ".jpeg" or ".jpe";

    // The old embedded thumbnail was left out with the old pixels; a new one shows the edit (E-16).
    private async Task WriteThumbnailAsync(string path, CancellationToken cancellationToken)
    {
        var thumbnail = await Task.Run(() => EmbeddedThumbnail.Make(path), cancellationToken).ConfigureAwait(false);
        if (thumbnail is null)
            return;
        var file = Path.Combine(Path.GetDirectoryName(path)!, $".thumb-{Guid.NewGuid():N}.jpg");
        try
        {
            await File.WriteAllBytesAsync(file, thumbnail, cancellationToken).ConfigureAwait(false);
            await metadata.WriteThumbnailAsync(path, file, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>What saving a pixel edit of the photo as <paramref name="extension"/> would lose, to ask first (E-16).</summary>
    /// <param name="LosesHdr">The photo is HDR, and the edited file can't keep its gain map, so it becomes SDR.</param>
    /// <param name="RemovesLightroomEdits">Lightroom or Camera Raw left edit settings in the photo; they are removed.</param>
    /// <param name="ColourProfile">The photo has an embedded colour profile, so the edit should keep its colours as they are.</param>
    public sealed record EditCheck(bool LosesHdr, bool RemovesLightroomEdits, bool ColourProfile);

    /// <summary>
    /// Looks at the photo before a pixel edit is saved as <paramref name="extension"/>: whether it would lose HDR (only
    /// a JPEG saved as JPEG keeps its gain map), whether Lightroom's edits would be removed, and whether it has a
    /// colour profile to keep.
    /// </summary>
    public async Task<EditCheck> InspectAsync(string path, string extension, CancellationToken cancellationToken = default)
    {
        var quality = await metadata.ReadQualityAsync(path, cancellationToken).ConfigureAwait(false);
        var keepsHdr = quality.Hdr && IsJpeg(path) && IsJpeg(extension) &&
                       await Task.Run(() => GainMapJpeg.Find(File.ReadAllBytes(path)), cancellationToken).ConfigureAwait(false) is not null;
        return new EditCheck(quality.Hdr && !keepsHdr, quality.LightroomEdits, quality.ProfileName is not null);
    }

    /// <summary>Undoes the most recent edit; returns the photo's path, which may change back to the old name.</summary>
    public Task<string> UndoAsync(string path, CancellationToken cancellationToken = default) =>
        RestoreAsync(path, EditHistory.Undo, cancellationToken);

    /// <summary>Restores the original file; returns the photo's path, which may change back to the old name.</summary>
    public Task<string> RevertAsync(string path, CancellationToken cancellationToken = default) =>
        RestoreAsync(path, EditHistory.Revert, cancellationToken);

    private async Task<string> RestoreAsync(string path, Func<string, string> restore, CancellationToken cancellationToken)
    {
        var current = await metadata.ReadAsync(path, cancellationToken).ConfigureAwait(false);
        var restored = restore(path);

        // Ratings, tags and so on made after the edit belong to the photo, not to the edit.
        await metadata.WriteAsync(restored, new MetadataChanges
        {
            Tags = current.Tags,
            Rating = current.Rating,
            Flagged = current.Flagged ? true : null,
            Rejected = current.Rejected ? true : null,
            Caption = current.Caption ?? "",
            DateTaken = current.DateTaken,
            Location = current.Latitude is { } lat && current.Longitude is { } lon ? new GeoPosition(lat, lon) : null,
            RemoveLocation = current.Latitude is null,
            Place = current.Place ?? new PlaceName(),
        }, cancellationToken, ownReplacement: true).ConfigureAwait(false);
        return restored;
    }
}
