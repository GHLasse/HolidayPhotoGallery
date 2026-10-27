namespace PhotoGallery.App.ViewModels;

/// <summary>
/// What is waiting in the whole library, for the Library menu and the dot on its button: groups of duplicates
/// (MainViewModel.Duplicates.cs), faces without a name and damaged files.
/// </summary>
public sealed partial class MainViewModel
{
    private int _unnamedFaceCount;
    private int _damagedCount;

    public int UnnamedFaceCount
    {
        get => _unnamedFaceCount;
        private set
        {
            if (SetProperty(ref _unnamedFaceCount, value))
                OnPropertyChanged(nameof(HasLibraryWork));
        }
    }

    public int DamagedCount
    {
        get => _damagedCount;
        private set
        {
            if (SetProperty(ref _damagedCount, value))
                OnPropertyChanged(nameof(HasLibraryWork));
        }
    }

    /// <summary>Something in the Library menu to go through: duplicates, unnamed faces or damaged files.</summary>
    public bool HasLibraryWork => HasDuplicates || _unnamedFaceCount > 0 || _damagedCount > 0;

    /// <summary>Counts the unnamed faces and damaged files again; quick, from the index only.</summary>
    public async Task RefreshLibraryCountsAsync()
    {
        try
        {
            var (unnamed, damaged) = await Task.Run(() => (_index.CountUnnamedFaces(), _index.CountDamaged()));
            UnnamedFaceCount = unnamed;
            DamagedCount = damaged;
        }
        catch (Exception ex) when (ex is System.Data.Common.DbException or InvalidOperationException)
        {
            App.Trace($"Library counts: {ex.Message}");
        }
    }

    /// <summary>Shows only the damaged files, to go through them with Repair.</summary>
    public void ShowDamagedFiles() => DamagedOnly = true;
}
