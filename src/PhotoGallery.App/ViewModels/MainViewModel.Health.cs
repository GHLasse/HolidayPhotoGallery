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
    }
}
