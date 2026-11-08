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
    public async Task<string> SaveEditAsync(string path, string extension, int width, int height,
                                            Func<string, Task> writeImage, FaceBox? crop = null, int turn = 1,
                                            string? metadataFrom = null, CancellationToken cancellationToken = default,
                                            Func<FaceBox, FaceBox?>? beforeCrop = null, bool asCopy = false,
                                            bool keepColourProfile = false, Func<string, Task>? writeThumbnail = null)
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
        // JPEG keeps a small preview in its EXIF block; it is made from the new pixels so it doesn't show the old photo (E-16).
        var thumbnail = writeThumbnail is not null && IsJpeg(extension) ? Path.Combine(Path.GetTempPath(), $"photogallery-thumb-{Guid.NewGuid():N}.jpg") : null;
        try
        {
            await writeImage(temp).ConfigureAwait(false);
            if (thumbnail is not null)
                await writeThumbnail!(thumbnail).ConfigureAwait(false);
            await metadata.CopyMetadataAsync(metadataFrom ?? path, temp, width, height, cancellationToken, keepColourProfile, thumbnail).ConfigureAwait(false);
            await MovePeopleAsync(metadataFrom ?? path, temp, crop, turn, beforeCrop, cancellationToken).ConfigureAwait(false);
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
            if (thumbnail is not null && File.Exists(thumbnail))
                File.Delete(thumbnail);
        }
    }

    private static bool IsJpeg(string extension) => extension.ToLowerInvariant() is ".jpg" or ".jpeg" or ".jpe";

    /// <summary>
    /// Whether a photo saved with this extension can keep its colour profile (E-16): ExifTool writes the profile into
    /// JPEG, PNG, TIFF and WebP, but not into HEIC, so a HEIC edit is turned into sRGB before saving.
    /// </summary>
    public static bool CanKeepColourProfile(string extension) =>
        extension.ToLowerInvariant() is ".jpg" or ".jpeg" or ".jpe" or ".png" or ".tif" or ".tiff" or ".webp";

    /// <summary>The size of the small preview kept in a JPEG: 160 pixels on the long edge, as cameras do.</summary>
    public static (int Width, int Height) ThumbnailSize(int width, int height)
    {
        var scale = 160.0 / Math.Max(width, height);
        return scale >= 1 ? (width, height) : (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
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
    private async Task MovePeopleAsync(string source, string target, FaceBox? crop, int turn, Func<FaceBox, FaceBox?>? beforeCrop,
                                       CancellationToken cancellationToken)
    {
        var before = await metadata.ReadAsync(source, cancellationToken).ConfigureAwait(false);
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
