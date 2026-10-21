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
/// <see cref="DamagedFrom"/> is how far down the picture data is known to be damaged at the latest (0 to 1, as
/// stored in the file), and <see cref="CanMend"/> says the file can be mended without touching the picture.
/// </summary>
public sealed record DamagedPhoto(DecodedImage? Image, PhotoDamage? Damage, byte[]? Preview, FileHealth Health)
{
    public double? DamagedFrom { get; init; }

    public bool CanMend { get; init; }
}

/// <summary>Ways to repair a photo, as offered in the repair wizard.</summary>
public enum RepairWay { Mend, FillFromPreview, FillFromCopy, ReplaceWithCopy, Crop }

/// <summary>
/// A repaired photo, not saved yet: its pixels, the part of the old one kept when cropped, and the file to take
/// the tags and other details from when the damaged file can't be read.
/// </summary>
public sealed record RepairedPhoto(DecodedImage Image, FaceBox? Kept, string? MetadataFrom = null)
{
    /// <summary>Only the file is mended (see <see cref="FileMend"/>); the picture stays as it is.</summary>
    public bool MendOnly { get; init; }

    /// <summary>How the repair was made, when worth telling (e.g. which AI model ran where, and how long).</summary>
    public string? Note { get; init; }
}

/// <summary>
/// AI upscaling would take long on this PC: about <see cref="Estimate"/>. The repair wizard asks before it
/// starts such a job.
/// </summary>
public sealed class SlowUpscaleException(TimeSpan estimate, bool onGraphicsCard) : Exception
{
    public TimeSpan Estimate { get; } = estimate;

    public bool OnGraphicsCard { get; } = onGraphicsCard;
}

/// <summary>Repairing damaged photos (see <see cref="PhotoRepair"/> and <see cref="FileCheck"/>).</summary>
public sealed partial class MainViewModel
{
    // Jobs longer than this are only started once the user has seen how long they take.
    private static readonly TimeSpan SlowUpscale = TimeSpan.FromSeconds(60);

    public bool UpscaleOnProcessorOnly => _settings.UpscaleOnProcessorOnly;

    public void SaveUpscaleOnProcessorOnly(bool processorOnly)
    {
        _settings.UpscaleOnProcessorOnly = processorOnly;
        _settings.Save(SettingsPath);
    }

    public static bool IsUpscaleModelInstalled(UpscaleQuality quality) =>
        UpscaleModel.IsInstalled(UpscaleModel.DefaultFolder(DataFolder, quality), quality);

    /// <summary>Downloads an upscaling model once; false with <see cref="ErrorMessage"/> saying why.</summary>
    public async Task<bool> DownloadUpscaleModelAsync(UpscaleQuality quality, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        try
        {
            await UpscaleModel.DownloadAsync(UpscaleModel.DefaultFolder(DataFolder, quality), quality, progress, cancellationToken: cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ErrorMessage = Tr("The AI model could not be downloaded ({0}). Check the internet connection and try again.", ex.Message);
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

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
            var damage = image is null ? null : await Task.Run(() => PhotoRepair.Find(image.Pixels, image.Width, image.Height, item.Photo.Orientation));
            double? damagedFrom = null;
            var canMend = false;
            try
            {
                if (health == FileHealth.Garbled)
                    damagedFrom = await Task.Run(() => JpegScan.Check(item.Path)?.DamagedFrom);
                else if (health == FileHealth.CutOff)
                    canMend = await Task.Run(() => FileMend.CanMend(item.Path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
            byte[]? preview = null;
            if (_metadata is not null && (damage is not null || health == FileHealth.Garbled))
            {
                try
                {
                    preview = await _metadata.ReadEmbeddedPreviewAsync(item.Path);
                }
                catch (ExifToolException)
                {
                }
            }
            return new DamagedPhoto(image, damage, preview, health) { DamagedFrom = damagedFrom, CanMend = canMend };
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
    /// <remarks>
    /// With <paramref name="upscale"/>, a fill source much smaller than the photo is first scaled up with that AI
    /// model (already downloaded). A job that would take long throws <see cref="SlowUpscaleException"/> before it
    /// starts, unless <paramref name="evenIfSlow"/>.
    /// </remarks>
    public async Task<RepairedPhoto?> BuildRepairAsync(PhotoItemViewModel item, DamagedPhoto damaged, RepairWay way, string? copyPath,
                                                       UpscaleQuality? upscale = null, bool evenIfSlow = false,
                                                       IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        StatusText = Tr("Repairing {0}…", item.FileName);
        Upscaler? upscaler = null;
        string? note = null;
        try
        {
            UpscalePicture? ai = upscale is not { } quality ? null : (pixels, width, height) =>
            {
                upscaler ??= Upscaler.Load(UpscaleModel.ModelPath(UpscaleModel.DefaultFolder(DataFolder, quality), quality), UpscaleOnProcessorOnly);
                var estimate = upscaler.TimeTile() * Upscaler.TileCount(width, height);
                if (estimate > SlowUpscale && !evenIfSlow)
                    throw new SlowUpscaleException(estimate, upscaler.OnGraphicsCard);
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var big = upscaler.Upscale(pixels, width, height, progress, cancellationToken);
                note = Tr("Detail brought back with AI in {2:N0} s ({0}, {1}).",
                          quality == UpscaleQuality.Best ? Tr("best quality") : Tr("fast"),
                          upscaler.OnGraphicsCard ? Tr("graphics card") : Tr("processor"), watch.Elapsed.TotalSeconds);
                return (big, width * UpscaleModel.Scale, height * UpscaleModel.Scale);
            };
            switch (way)
            {
                case RepairWay.Mend when damaged is { Image: { } image, CanMend: true }:
                    return new RepairedPhoto(image, null) { MendOnly = true };
                case RepairWay.Crop when damaged is { Image: { } image, Damage: { } damage }:
                {
                    var crop = await Task.Run(() => PhotoRepair.Crop(image.Pixels, image.Width, image.Height, damage));
                    return new RepairedPhoto(new DecodedImage(crop.Pixels, crop.Width, crop.Height), new FaceBox(crop.X, crop.Y, crop.W, crop.H));
                }
                case RepairWay.FillFromPreview when damaged is { Image: { } image, Damage: { } damage, Preview: { } preview }:
                {
                    var pixels = await Task.Run(() => PhotoRepair.Fill(image.Pixels, image.Width, image.Height, damage, preview, item.Photo.Orientation, ai), cancellationToken);
                    if (pixels is not null)
                        return new RepairedPhoto(image with { Pixels = pixels }, null) { Note = note };
                    ErrorMessage = Tr("The preview stored in {0} has another shape than the photo, so it can't fill it. Try a copy, or crop.", item.FileName);
                    return null;
                }
                case RepairWay.FillFromCopy or RepairWay.ReplaceWithCopy when copyPath is not null:
                {
                    var copy = await ImageLoader.DecodePixelsAsync(copyPath, uint.MaxValue);
                    if (way == RepairWay.ReplaceWithCopy || damaged is not { Image: { } image, Damage: { } damage })
                        return new RepairedPhoto(copy, null, damaged.Health == FileHealth.Unreadable ? copyPath : null);
                    var pixels = await Task.Run(() => PhotoRepair.Fill(image.Pixels, image.Width, image.Height, damage, copy.Pixels, copy.Width, copy.Height, ai), cancellationToken);
                    if (pixels is not null)
                        return new RepairedPhoto(image with { Pixels = pixels }, null) { Note = note };
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
        catch (Microsoft.ML.OnnxRuntime.OnnxRuntimeException ex)
        {
            ErrorMessage = Tr("AI upscaling failed: {0}. Try it without AI, or turn on “Use only the processor” in Settings › Editing.", ex.Message);
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            upscaler?.Dispose();
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
            if (repaired.MendOnly)
            {
                await Task.Run(() => FileMend.Mend(path));
                await AfterFileReplacedAsync(item, path, path);
                Inform(Tr("Mended {0}. The file as it was is in the hidden .originals folder.", item.FileName));
                return true;
            }
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
