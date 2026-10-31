using PhotoGallery.Core.ExifTool;
using PhotoGallery.Core.Import;
using PhotoGallery.Core.Metadata;
using Windows.Media.Import;
using PhotoGallery.App.Native;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.App.ViewModels;

/// <summary>Where photos can be imported from: a card or camera with a drive letter, or a phone or camera Windows reaches.</summary>
public sealed class ImportSourceChoice(string name, string? drive, PhotoImportSource? device, string? portable = null)
{
    public string Name { get; } = name;

    public string? Drive { get; } = drive;

    public PhotoImportSource? Device { get; } = device;

    /// <summary>
    /// A phone as File Explorer shows it under "This PC" (MTP), for when Windows' import doesn't list it: the
    /// shell's name for it, through which its files are read and copied (<see cref="ShellDevices"/>).
    /// </summary>
    public string? Portable { get; } = portable;

    // A phone's items, found by Windows' import API; importing selects among them.
    internal PhotoImportFindItemsResult? Found { get; set; }

    public override string ToString() => Name;
}

/// <summary>Importing from a phone, camera or SD card (F-1): new photos only, into dated folders, in the background.</summary>
public sealed partial class MainViewModel
{
    public string ImportFolder
    {
        get => _settings.ImportFolder ?? _settings.Folders.FirstOrDefault() ?? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        set
        {
            _settings.ImportFolder = value;
            _settings.Save(SettingsPath);
        }
    }

    public string ImportPattern
    {
        get => _settings.ImportPattern;
        set
        {
            _settings.ImportPattern = value;
            _settings.Save(SettingsPath);
        }
    }

    /// <summary>Cards and cameras with a drive letter, then the phones and cameras Windows' import finds.</summary>
    public async Task<List<ImportSourceChoice>> FindImportSourcesAsync()
    {
        var choices = (await Task.Run(DriveImport.FindCards))
            .Select(d => new ImportSourceChoice(string.IsNullOrEmpty(d.VolumeLabel) ? d.Name : $"{d.VolumeLabel} ({d.Name.TrimEnd('\\')})", d.RootDirectory.FullName, null))
            .ToList();
        try
        {
            if (await PhotoImportManager.IsSupportedAsync())
            {
                var cards = choices.Count;
                foreach (var source in await PhotoImportManager.FindAllSourcesAsync())
                {
                    // A card reader shows up here as a plain removable medium too; it is already listed by its drive letter.
                    var isCard = source.Type == PhotoImportSourceType.Generic &&
                                 source.StorageMedia.Count > 0 &&
                                 source.StorageMedia.All(m => m.StorageMediumType == PhotoImportStorageMediumType.Removable);
                    if (!(isCard && cards > 0))
                        choices.Add(new ImportSourceChoice(source.DisplayName, null, source));
                }
            }
        }
        catch (Exception ex)
        {
            // Without Windows' import (or with the phone locked) cards still work.
            App.Trace($"photo import sources: {ex.Message}");
        }

        // Phones File Explorer shows under "This PC" without a drive letter, which Windows' import didn't list
        // (it missed a Pixel that Explorer showed). Read through the shell, like Explorer does.
        try
        {
            foreach (var device in await ShellDevices.FindAsync(PhoneFolders, line => App.Trace($"portable devices: {line}")))
            {
                App.Trace($"portable device found: {device.Name}");
                // Listed by Windows' import already (by the same name); an empty name matches nothing.
                if (choices.Any(c => c.Name.Length > 0 && device.Name.Length > 0 &&
                                     (c.Name.Contains(device.Name, StringComparison.CurrentCultureIgnoreCase) ||
                                      device.Name.Contains(c.Name, StringComparison.CurrentCultureIgnoreCase))))
                    continue;
                choices.Add(new ImportSourceChoice(device.Name, null, null, device.ParsingName));
            }
        }
        catch (Exception ex)
        {
            App.Trace($"portable devices: {ex.Message}");
        }
        return choices;
    }

    // The folders on a phone that hold photos, in each of its storages ("Internal shared storage", a card).
    private static readonly string[] PhoneFolders = ["DCIM", "Pictures", "Movies"];

    private static async Task<List<ImportItem>> ListPortableItemsAsync(string device) =>
        (await ShellDevices.ListFilesAsync(device, PhoneFolders, ImportPlanner.IsImportable))
            .Select(f => new ImportItem(f.Name, f.Size, f.Taken) { Handle = f.ParsingName })
            .ToList();

    // Copies each photo from the phone through a temporary name next to its target.
    private async Task ImportFromPortableAsync(IReadOnlyList<(ImportItem Item, string Target)> plan, bool delete, List<string> imported, List<string> failures)
    {
        for (var i = 0; i < plan.Count; i++)
        {
            if (_stopRequested)
                break;
            var (item, target) = plan[i];
            if (item.Handle is not string file)
                continue;
            StatusText = Tr("Importing {0:N0} of {1:N0}…", i + 1, plan.Count);
            var folder = Path.GetDirectoryName(target)!;
            var temp = Path.GetFileName(target) + ".importing";
            try
            {
                Directory.CreateDirectory(folder);
                var copy = Path.Combine(folder, temp);
                var copied = await ShellDevices.CopyAsync(file, copy);
                // A size of 0 means the phone didn't tell it.
                if (copied == 0 || (item.Size > 0 && copied != item.Size))
                {
                    File.Delete(copy);
                    throw new IOException(Tr("The copy of {0} is not complete.", item.Name));
                }
                var info = new FileInfo(copy) { Attributes = System.IO.FileAttributes.Normal, LastWriteTime = item.Taken };
                File.Move(info.FullName, target);
                imported.Add(target);
                if (delete)
                    await ShellDevices.DeleteAsync(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
            {
                failures.Add($"{item.Name}: {ex.Message}");
                try
                {
                    File.Delete(Path.Combine(folder, temp));
                }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }

    /// <summary>The photos and videos on the source.</summary>
    public async Task<List<ImportItem>> ListImportItemsAsync(ImportSourceChoice source)
    {
        if (source.Drive is { } drive)
            return await Task.Run(() => DriveImport.ListItems(drive));
        if (source.Portable is { } phone)
            return await ListPortableItemsAsync(phone);
        if (source.Device is not { } device)
            return [];
        var session = device.CreateImportSession();
        source.Found = await session.FindItemsAsync(PhotoImportContentTypeFilter.ImagesAndVideos, PhotoImportItemSelectionMode.SelectNone);
        return source.Found.FoundItems
            .Select(i => new ImportItem(i.Name, (long)i.SizeInBytes, i.Date.LocalDateTime) { Handle = i })
            .ToList();
    }

    /// <summary>Which of the items are new and where they go.</summary>
    public async Task<(List<(ImportItem Item, string Target)> New, int Skipped)> PlanImportAsync(IReadOnlyList<ImportItem> items, string destination, string pattern, string? eventName)
    {
        var have = await Task.Run(_index.GetNamesAndSizes);
        return ImportPlanner.Plan(items, destination, pattern, eventName, (name, size) => have.Contains((name.ToLowerInvariant(), size)));
    }

    public async Task ImportAsync(ImportSourceChoice source, IReadOnlyList<(ImportItem Item, string Target)> plan, string destination,
                                  IReadOnlyList<string> tags, bool deleteFromSource)
    {
        if (plan.Count == 0)
            return;
        if (IsBusy)
        {
            Warn(Tr("The app is still busy. Try again when it has finished."));
            return;
        }
        var imported = new List<string>();
        var failures = new List<string>();
        IsBusy = true;
        try
        {
            if (source.Drive is not null)
                await ImportFromDriveAsync(plan, deleteFromSource, imported, failures);
            else if (source.Portable is not null)
                await ImportFromPortableAsync(plan, deleteFromSource, imported, failures);
            else
                await ImportFromDeviceAsync(source, plan, destination, deleteFromSource, imported, failures);
        }
        finally
        {
            IsBusy = false;
            StatusText = _countText;
        }

        if (failures.Count > 0)
            ErrorMessage = Tr("{0:N0} photo(s) could not be imported.", failures.Count) + " " + string.Join(" ", failures.Take(3));
        if (imported.Count == 0)
            return;

        // The new photos join the library: a destination outside it is added to it.
        if (!_settings.Folders.Any(f => IsUnder(destination, f)))
            await AddFolderAsync(destination);
        else
            await IndexImportedAsync(imported);
        if (tags.Count > 0)
            await TagImportedAsync(imported, tags);
        Inform(Tr("Imported {0} into {1}.", PhotoCount(imported.Count), destination));
        SelectedFolder = new FolderNodeViewModel(Path.GetDirectoryName(imported[0])!, isRoot: false);
    }

    private static bool IsUnder(string path, string folder)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        return (Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) + Path.DirectorySeparatorChar).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private async Task ImportFromDriveAsync(IReadOnlyList<(ImportItem Item, string Target)> plan, bool delete, List<string> imported, List<string> failures)
    {
        for (var i = 0; i < plan.Count; i++)
        {
            if (_stopRequested)
                break; // what is copied stays copied
            var (item, target) = plan[i];
            StatusText = Tr("Importing {0:N0} of {1:N0}…", i + 1, plan.Count);
            try
            {
                await Task.Run(() =>
                {
                    // Only a copy checked byte for byte lets the original be deleted.
                    DriveImport.Copy(item.SourcePath!, target, verify: delete);
                    if (delete)
                    {
                        File.SetAttributes(item.SourcePath!, System.IO.FileAttributes.Normal);
                        File.Delete(item.SourcePath!);
                    }
                });
                imported.Add(target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add($"{item.Name}: {ex.Message}");
            }
        }
    }

    // Windows copies from the phone into a hidden folder in the destination; from there each photo moves into its dated folder.
    private async Task ImportFromDeviceAsync(ImportSourceChoice source, IReadOnlyList<(ImportItem Item, string Target)> plan, string destination,
                                             bool delete, List<string> imported, List<string> failures)
    {
        if (source.Found is not { } found)
            return;
        var staging = Directory.CreateDirectory(Path.Combine(destination, ".importing-" + Guid.NewGuid().ToString("N")[..8]));
        staging.Attributes |= System.IO.FileAttributes.Hidden;
        try
        {
            // By the device's key for each item: Windows may hand back other objects for the same items.
            var targets = new Dictionary<ulong, string>();
            foreach (var (item, target) in plan)
            {
                if (item.Handle is PhotoImportItem device)
                    targets[device.ItemKey] = target;
            }
            foreach (var item in found.FoundItems)
                item.IsSelected = targets.ContainsKey(item.ItemKey);
            found.Session.DestinationFolder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(staging.FullName);
            found.Session.SubfolderCreationMode = PhotoImportSubfolderCreationMode.DoNotCreateSubfolders;

            var operation = found.ImportItemsAsync();
            operation.Progress = (_, p) => StatusText = Tr("Importing {0:N0} of {1:N0}…", (int)p.ItemsImported, plan.Count);
            var result = await operation;
            foreach (var item in result.ImportedItems)
            {
                if (!targets.TryGetValue(item.ItemKey, out var target))
                    continue;
                foreach (var name in item.ImportedFileNames)
                {
                    var copied = Path.Combine(staging.FullName, name);
                    if (!File.Exists(copied))
                        continue;
                    // A sidecar (e.g. an iPhone's .AAE) goes next to its photo.
                    var to = Path.GetExtension(name).Equals(Path.GetExtension(item.Name), StringComparison.OrdinalIgnoreCase)
                        ? target
                        : Path.Combine(Path.GetDirectoryName(target)!, Path.GetFileNameWithoutExtension(target) + Path.GetExtension(name));
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                        File.SetAttributes(copied, System.IO.FileAttributes.Normal);
                        File.Move(copied, to);
                        if (to == target)
                            imported.Add(target);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        failures.Add($"{name}: {ex.Message}");
                    }
                }
            }
            if (delete && imported.Count > 0)
                await result.DeleteImportedItemsFromSourceAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failures.Add($"{source.Name}: {ex.Message}");
        }
        finally
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(staging.FullName).Any())
                    staging.Delete();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private async Task IndexImportedAsync(IReadOnlyList<string> imported)
    {
        if (_indexer is null)
            return;
        IsBusy = true;
        try
        {
            foreach (var folder in imported.Select(Path.GetDirectoryName).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
                await _indexer.SyncFolderAsync(folder, null, fileFailed: (path, ex) => App.Trace($"Could not read {path}: {ex}"));
        }
        finally
        {
            IsBusy = false;
        }
        _counts = null;
    }

    // The tags typed for the whole import, added to each photo's own.
    private async Task TagImportedAsync(IReadOnlyList<string> imported, IReadOnlyList<string> tags)
    {
        if (_metadata is null || _indexer is null)
            return;
        IsBusy = true;
        try
        {
            var photos = imported.Where(Core.SupportedFormats.IsSupported).ToList();
            for (var i = 0; i < photos.Count; i++)
            {
                StatusText = Tr("Adding tags to {0:N0} of {1:N0}…", i + 1, photos.Count);
                try
                {
                    var current = await _metadata.ReadAsync(photos[i]);
                    await _metadata.WriteAsync(photos[i], new MetadataChanges { Tags = TagList.Normalize(current.Tags.Concat(tags)) });
                    await _indexer.RefreshFileAsync(photos[i]);
                }
                catch (Exception ex) when (ex is ExifToolException or IOException or UnauthorizedAccessException)
                {
                    App.Trace($"tagging {photos[i]}: {ex.Message}");
                }
            }
        }
        finally
        {
            IsBusy = false;
            StatusText = _countText;
        }
    }
}
