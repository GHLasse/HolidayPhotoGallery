using PhotoGallery.Core.ExifTool;
using PhotoGallery.Core.Metadata;

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
    /// </summary>
    public async Task<string> SaveEditAsync(string path, string extension, int width, int height,
                                            Func<string, Task> writeImage, CancellationToken cancellationToken = default)
    {
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly))
            throw new ExifToolException($"Could not save {Path.GetFileName(path)}: the file is read-only. Use \"Make editable\" to unlock the photos in this folder.");

        var newPath = string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase)
            ? path
            : Path.ChangeExtension(path, extension);
        if (newPath != path && File.Exists(newPath))
            throw new IOException($"Could not save as {Path.GetFileName(newPath)}: a photo with that name already exists.");

        // Written next to the photo under a hidden name, then swapped in, so a failure leaves the photo as it was.
        var temp = Path.Combine(Path.GetDirectoryName(path)!, $".edit-{Guid.NewGuid():N}{extension}");
        try
        {
            await writeImage(temp).ConfigureAwait(false);
            await metadata.CopyMetadataAsync(path, temp, width, height, cancellationToken).ConfigureAwait(false);

            EditHistory.BackUp(path);
            File.Move(temp, newPath, overwrite: true);
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
            Caption = current.Caption ?? "",
            DateTaken = current.DateTaken,
            Location = current.Latitude is { } lat && current.Longitude is { } lon ? new GeoPosition(lat, lon) : null,
            RemoveLocation = current.Latitude is null,
            Place = current.Place ?? new PlaceName(),
        }, cancellationToken).ConfigureAwait(false);
        return restored;
    }
}
