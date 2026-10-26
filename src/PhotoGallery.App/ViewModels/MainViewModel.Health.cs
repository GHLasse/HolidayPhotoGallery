using PhotoGallery.Core.Library;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.App.ViewModels;

/// <summary>
/// The library health check: every photo's file is looked at once in the background (again after it
/// changes), and damaged ones are kept in the index for the Damaged filter.
/// </summary>
public sealed partial class MainViewModel
{
    private bool _checkingFiles;
    private bool _damagedOnly;
    private bool _misnamedOnly;

    /// <summary>Shows only photos whose file was found damaged.</summary>
    public bool DamagedOnly
    {
        get => _damagedOnly;
        set
        {
            if (SetProperty(ref _damagedOnly, value))
                _ = SearchAsync();
        }
    }

    /// <summary>Shows only photos whose name claims the wrong format, such as a JPEG called .png.</summary>
    public bool MisnamedOnly
    {
        get => _misnamedOnly;
        set
        {
            if (SetProperty(ref _misnamedOnly, value))
                _ = SearchAsync();
        }
    }

    /// <summary>
    /// Gives every misnamed photo the extension of its real format (IMG_1.PNG that is a JPEG becomes
    /// IMG_1.JPG), with its backups and sidecar files, and Undo. Then details can be saved into it again.
    /// </summary>
    public async Task FixMisnamedAsync()
    {
        var plan = new List<(IndexedPhoto Photo, string NewName)>();
        foreach (var photo in await Task.Run(_index.GetMisnamed))
        {
            try
            {
                if (await Task.Run(() => FileCheck.CorrectName(photo.Path)) is { } name)
                    plan.Add((photo, name));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Gone or locked: the next check finds it again.
            }
        }
        if (plan.Count == 0)
        {
            Inform(Tr("No photo has the wrong file extension."));
            return;
        }
        await RenamePhotosAsync(plan);
        // The renamed photos are checked again, so they leave the Misnamed filter.
        _ = CheckFilesInBackgroundAsync();
    }

    /// <summary>
    /// Checks every photo not checked yet, quietly and pausing while the app is busy with something the
    /// user asked for. Reading each file once takes a while in a big library, so it runs after the folders
    /// were checked, like the face search.
    /// </summary>
    public async Task CheckFilesInBackgroundAsync()
    {
        if (_checkingFiles)
            return;
        _checkingFiles = true;
        var unreadable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var damagedBefore = await Task.Run(_index.CountDamaged);
        var misnamedBefore = await Task.Run(_index.CountMisnamed);
        try
        {
            while (true)
            {
                const int Batch = 100;
                var todo = (await Task.Run(() => _index.GetUnchecked(Batch + unreadable.Count)))
                    .Where(p => !unreadable.Contains(p.Path)).Take(Batch).ToList();
                if (todo.Count == 0)
                    break;
                foreach (var (path, modified) in todo)
                {
                    while (IsBusy)
                        await Task.Delay(2000);
                    try
                    {
                        var health = await Task.Run(() => FileCheck.Check(path));
                        await Task.Run(() => _index.SaveCheck(path, health, modified));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        unreadable.Add(path); // gone or locked: tried again next time
                    }
                }
            }
        }
        finally
        {
            _checkingFiles = false;
        }

        var damaged = await Task.Run(_index.CountDamaged);
        if (damaged > damagedBefore)
            Warn(damaged == 1
                ? Tr("1 photo file is damaged. Choose Missing › Damaged files to see it, and Repair in the viewer to rescue it.")
                : Tr("{0} photo files are damaged. Choose Missing › Damaged files to see them, and Repair in the viewer to rescue them.", damaged));

        var misnamed = await Task.Run(_index.CountMisnamed);
        if (misnamed > misnamedBefore)
            Warn(misnamed == 1
                    ? Tr("1 photo has the wrong file extension, such as a JPEG called .png, so its details can’t be saved. Choose Missing › Wrong file extension to see it.")
                    : Tr("{0} photos have the wrong file extension, such as a JPEG called .png, so their details can’t be saved. Choose Missing › Wrong file extension to see them.", misnamed),
                [new NoticeAction(Tr("Fix the extensions"), FixMisnamedAsync)]);
    }
}
