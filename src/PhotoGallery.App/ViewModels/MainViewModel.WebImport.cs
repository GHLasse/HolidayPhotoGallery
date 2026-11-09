using PhotoGallery.Core.ExifTool;
using PhotoGallery.Core.Import;
using PhotoGallery.Core.Library;
using PhotoGallery.Core.Metadata;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.App.ViewModels;

/// <summary>Importing photos from a website (F-8): the chosen photos are downloaded in the background, one at a time.</summary>
public sealed partial class MainViewModel
{
    /// <summary>
    /// Downloads <paramref name="photos"/> into <paramref name="folder"/> with the address each came from in its
    /// details, leaves out those already in the library (same name and size), and adds the new ones to the library.
    /// Takes over <paramref name="client"/> and closes it when done.
    /// </summary>
    /// <param name="names">The name each photo is saved under (without ending); null keeps its own file name.</param>
    public async Task WebImportAsync(WebSiteClient client, IReadOnlyList<WebPhoto> photos, IReadOnlyList<string?> names, string folder, IReadOnlyList<string> tags)
    {
        using var site = client;
        if (photos.Count == 0)
            return;
        // Another job is running (reading the library, finding people…): the download waits for it.
        if (IsBusy)
        {
            Inform(Tr("The download starts as soon as the app has finished: {0}", StatusText));
            while (IsBusy)
                await Task.Delay(500);
        }
        var imported = new List<string>();
        int failed = 0, already = 0;
        await WaitForSavesAsync(); // the info pane's edits are in the files first
        var have = await Task.Run(_index.GetNamesAndSizes);
        IsBusy = true;
        try
        {
            for (var i = 0; i < photos.Count; i++)
            {
                if (_stopRequested)
                    break; // what is downloaded stays
                var photo = photos[i];
                StatusText = Tr("Downloading {0:N0} of {1:N0}…", i + 1, photos.Count);
                var result = await site.GetAsync(photo.Url, image: true, CancellationToken.None);
                if (!result.Succeeded || WebSiteClient.ImageExtension(result.Body) is not { } extension)
                {
                    App.Trace($"web import {photo.Url}: {result.Failure}");
                    failed++;
                    continue;
                }
                var name = WebSiteClient.FileNameFor(photo.Url, extension, names[i]);
                if (have.Contains((name.ToLowerInvariant(), result.Body!.LongLength)))
                {
                    already++;
                    continue;
                }
                try
                {
                    var path = await Task.Run(() => WebSiteClient.Save(result.Body!, photo.Url, folder, names[i]));
                    imported.Add(path);
                    // Where it came from goes into the photo, so it is known later.
                    if (_metadata is not null && Core.SupportedFormats.IsSupported(path))
                    {
                        try
                        {
                            await _metadata.WriteAsync(path, new MetadataChanges { SourceUrl = photo.Url.AbsoluteUri }, ownReplacement: true);
                        }
                        catch (Exception ex) when (ex is ExifToolException or NotSupportedException)
                        {
                            App.Trace($"web import source for {path}: {ex.Message}");
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    App.Trace($"web import {photo.Url}: {ex.Message}");
                    failed++;
                }
            }
        }
        finally
        {
            IsBusy = false;
            StatusText = _countText;
        }

        if (imported.Count > 0)
        {
            // The new photos join the library: a folder outside it is added to it.
            if (!_settings.Folders.Any(f => IsUnder(folder, f)))
                await AddFolderAsync(folder);
            else
                await IndexImportedAsync(imported);
            if (tags.Count > 0)
                await TagImportedAsync(imported, tags);
            if (_settings.AutoTagging.Automatic != AutoTagSettings.AutomaticOff)
                await Task.Run(() => _index.QueueAutoTag(imported));
            StartBackgroundJobs();
            RefreshFolderTree();
            SelectedFolder = new FolderNodeViewModel(folder, isRoot: false);
        }

        // One banner for the whole download.
        var message = imported.Count > 0
            ? Tr("Downloaded {0} into {1}.", PhotoCount(imported.Count), folder)
            : Tr("No photos were downloaded.");
        if (already > 0)
            message += " " + Tr("{0:N0} were in the library already and were left out.", already);
        if (failed > 0)
            message += " " + Tr("{0:N0} could not be downloaded.", failed);
        if (imported.Count > 0 && failed == 0)
            Inform(message);
        else
            Warn(message);
    }
}
