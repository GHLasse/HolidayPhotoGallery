using PhotoGallery.App.Imaging;
using PhotoGallery.Core.Editing;
using PhotoGallery.Core.ExifTool;
using PhotoGallery.Core.Faces;
using PhotoGallery.Core.Library;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.App.ViewModels;

/// <summary>
/// A photo looked at for repair: its pixels at full size (null when they can't be read at all), the part that
/// was lost (null when none shows), the preview stored in its file (if any) and what the file check found.
/// </summary>
public sealed record DamagedPhoto(DecodedImage? Image, PhotoDamage? Damage, byte[]? Preview, FileHealth Health);

/// <summary>Ways to repair a photo, as offered in the repair wizard.</summary>
public enum RepairWay { FillFromPreview, FillFromCopy, ReplaceWithCopy, Crop }

/// <summary>
/// A repaired photo, not saved yet: its pixels, the part of the old one kept when cropped, and the file to take
/// the tags and other details from when the damaged file can't be read.
/// </summary>
public sealed record RepairedPhoto(DecodedImage Image, FaceBox? Kept, string? MetadataFrom = null);

/// <summary>Repairing damaged photos (see <see cref="PhotoRepair"/> and <see cref="FileCheck"/>).</summary>
public sealed partial class MainViewModel
{
    /// <summary>Copies of the photo elsewhere in the library (the same file name), if they are whole.</summary>
    public Task<IReadOnlyList<string>> FindCopiesAsync(PhotoItemViewModel item) => Task.Run(() =>
        (IReadOnlyList<string>)_index.FindByFileName(item.FileName, item.Path)
            .Where(path =>
            {
                try
                {
                    return File.Exists(path) && FileCheck.Check(path) == FileHealth.Whole;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return false;
                }
            })
            .ToList());

    /// <summary>Looks at the photo for the repair wizard.</summary>
    public async Task<DamagedPhoto> FindDamageAsync(PhotoItemViewModel item)
    {
        IsBusy = true;
        StatusText = Tr("Looking for the damaged part of {0}…", item.FileName);
        try
        {
            var health = await Task.Run(() =>
            {
                try
                {
                    return FileCheck.Check(item.Path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return FileHealth.Unreadable;
                }
            });
            DecodedImage? image = null;
            try
            {
                image = await ImageLoader.DecodePixelsAsync(item.Path, uint.MaxValue);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or ArgumentException)
            {
                // Not even a broken picture: only a copy can help.
            }
            var damage = image is null ? null : await Task.Run(() => PhotoRepair.Find(image.Pixels, image.Width, image.Height));
            byte[]? preview = null;
            if (_metadata is not null && damage is not null)
            {
                try
                {
                    preview = await _metadata.ReadEmbeddedPreviewAsync(item.Path);
                }
                catch (ExifToolException)
                {
                }
            }
            return new DamagedPhoto(image, damage, preview, health);
        }
        finally
        {
            IsBusy = false;
            StatusText = _countText;
        }
    }

    /// <summary>
    /// The repaired photo, not saved yet, or null with <see cref="ErrorMessage"/> saying why. <paramref name="copyPath"/>
    /// is the other copy of the photo for <see cref="RepairWay.FillFromCopy"/> and <see cref="RepairWay.ReplaceWithCopy"/>.
    /// </summary>
    public async Task<RepairedPhoto?> BuildRepairAsync(PhotoItemViewModel item, DamagedPhoto damaged, RepairWay way, string? copyPath)
    {
        IsBusy = true;
        StatusText = Tr("Repairing {0}…", item.FileName);
        try
        {
            switch (way)
            {
                case RepairWay.Crop when damaged is { Image: { } image, Damage: { } damage }:
                {
                    var crop = await Task.Run(() => PhotoRepair.Crop(image.Pixels, image.Width, image.Height, damage));
                    return new RepairedPhoto(new DecodedImage(crop.Pixels, crop.Width, crop.Height), new FaceBox(crop.X, crop.Y, crop.W, crop.H));
                }
                case RepairWay.FillFromPreview when damaged is { Image: { } image, Damage: { } damage, Preview: { } preview }:
                {
                    var pixels = await Task.Run(() => PhotoRepair.Fill(image.Pixels, image.Width, image.Height, damage, preview, item.Photo.Orientation));
                    if (pixels is not null)
                        return new RepairedPhoto(image with { Pixels = pixels }, null);
                    ErrorMessage = Tr("The preview stored in {0} has another shape than the photo, so it can't fill it. Try a copy, or crop.", item.FileName);
                    return null;
                }
                case RepairWay.FillFromCopy or RepairWay.ReplaceWithCopy when copyPath is not null:
                {
                    var copy = await ImageLoader.DecodePixelsAsync(copyPath, uint.MaxValue);
                    if (way == RepairWay.ReplaceWithCopy || damaged is not { Image: { } image, Damage: { } damage })
                        return new RepairedPhoto(copy, null, damaged.Health == FileHealth.Unreadable ? copyPath : null);
                    var pixels = await Task.Run(() => PhotoRepair.Fill(image.Pixels, image.Width, image.Height, damage, copy.Pixels, copy.Width, copy.Height));
                    if (pixels is not null)
                        return new RepairedPhoto(image with { Pixels = pixels }, null);
                    ErrorMessage = Tr("{0} has another shape than the photo, so it can't fill it. Is it a copy of the same photo, maybe cropped?", Path.GetFileName(copyPath));
                    return null;
                }
                default:
                    return null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            ErrorMessage = Tr("Could not read {0}: {1}", Path.GetFileName(copyPath ?? item.Path), ex.Message);
            return null;
        }
        finally
        {
            IsBusy = false;
            StatusText = _countText;
        }
    }

    /// <summary>Saves the repaired photo over the damaged one, which is kept in the hidden .originals folder.</summary>
    public async Task<bool> SaveRepairAsync(PhotoItemViewModel item, RepairedPhoto repaired)
    {
        if (_photoEditor is null)
            return false;
        var path = item.Path;
        IsBusy = true;
        StatusText = Tr("Repairing {0}…", item.FileName);
        try
        {
            // A JPEG stays a JPEG; formats the app can't write are saved as JPEG.
            var extension = Path.GetExtension(path);
            if (!ImageEncoder.CanEncode(extension))
                extension = ".jpg";
            var image = repaired.Image;
            var newPath = await _photoEditor.SaveEditAsync(path, extension, image.Width, image.Height,
                                                           target => ImageEncoder.EncodeAsync(image, target), repaired.Kept,
                                                           metadataFrom: repaired.MetadataFrom);
            await AfterFileReplacedAsync(item, path, newPath);
            Inform(Tr("Repaired {0}. The damaged file is in the hidden .originals folder.", item.FileName));
            return true;
        }
        catch (Exception ex) when (ex is ExifToolException or IOException or UnauthorizedAccessException or NotSupportedException or System.Runtime.InteropServices.COMException)
        {
            ErrorMessage = Tr("Could not save the edit of {0}: {1}", item.FileName, ex.Message);
            return false;
        }
        finally
        {
            IsBusy = false;
            StatusText = _countText;
        }
    }
}
