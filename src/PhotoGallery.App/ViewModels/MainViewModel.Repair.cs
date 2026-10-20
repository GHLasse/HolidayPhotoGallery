using PhotoGallery.App.Imaging;
using PhotoGallery.Core.Editing;
using PhotoGallery.Core.ExifTool;
using PhotoGallery.Core.Faces;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.App.ViewModels;

/// <summary>A damaged photo at full size, what was lost, and the preview stored in its file (if any).</summary>
public sealed record DamagedPhoto(DecodedImage Image, PhotoDamage Damage, byte[]? Preview);

/// <summary>Repairing photos whose file lost its end (see <see cref="PhotoRepair"/>).</summary>
public sealed partial class MainViewModel
{
    /// <summary>The lost part of the photo, or null when it looks whole (or can't be read).</summary>
    public async Task<DamagedPhoto?> FindDamageAsync(PhotoItemViewModel item)
    {
        IsBusy = true;
        StatusText = Tr("Looking for the damaged part of {0}…", item.FileName);
        try
        {
            var image = await ImageLoader.DecodePixelsAsync(item.Path, uint.MaxValue);
            var damage = await Task.Run(() => PhotoRepair.Find(image.Pixels, image.Width, image.Height));
            if (damage is null)
                return null;
            var preview = _metadata is null ? null : await _metadata.ReadEmbeddedPreviewAsync(item.Path);
            return new DamagedPhoto(image, damage, preview);
        }
        catch (Exception ex) when (ex is ExifToolException or IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            ErrorMessage = Tr("Could not read {0}: {1}", item.FileName, ex.Message);
            return null;
        }
        finally
        {
            IsBusy = false;
            StatusText = _countText;
        }
    }

    /// <summary>
    /// Saves the photo repaired: the lost part filled from the preview, or cropped away. The version before is
    /// kept in the hidden .originals folder, like any edit.
    /// </summary>
    public async Task<bool> RepairAsync(PhotoItemViewModel item, DamagedPhoto damaged, bool fill)
    {
        if (_photoEditor is null)
            return false;
        var path = item.Path;
        IsBusy = true;
        StatusText = Tr("Repairing {0}…", item.FileName);
        try
        {
            var image = damaged.Image;
            FaceBox? kept = null;
            DecodedImage? repaired;
            if (fill)
            {
                var pixels = damaged.Preview is { } preview
                    ? await Task.Run(() => PhotoRepair.Fill(image.Pixels, image.Width, image.Height, damaged.Damage, preview, item.Photo.Orientation))
                    : null;
                repaired = pixels is null ? null : image with { Pixels = pixels };
            }
            else
            {
                var crop = await Task.Run(() => PhotoRepair.Crop(image.Pixels, image.Width, image.Height, damaged.Damage));
                repaired = new DecodedImage(crop.Pixels, crop.Width, crop.Height);
                kept = new FaceBox(crop.X, crop.Y, crop.W, crop.H);
            }
            if (repaired is not { } result)
            {
                ErrorMessage = Tr("The preview stored in {0} can't be used to fill it. Crop it to the undamaged part instead.", item.FileName);
                return false;
            }

            // A JPEG stays a JPEG; formats the app can't write are saved as JPEG.
            var extension = Path.GetExtension(path);
            if (!ImageEncoder.CanEncode(extension))
                extension = ".jpg";
            var newPath = await _photoEditor.SaveEditAsync(path, extension, result.Width, result.Height,
                                                           target => ImageEncoder.EncodeAsync(result, target), kept);
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
