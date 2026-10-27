using System.Collections.ObjectModel;
using PhotoGallery.App.Imaging;
using PhotoGallery.Core.Editing;
using PhotoGallery.Core.ExifTool;
using PhotoGallery.Core.Faces;
using PhotoGallery.Core.Library;
using PhotoGallery.Core.Metadata;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.App.ViewModels;

/// <summary>Unnamed faces that look like the same person, and who they may be.</summary>
public sealed class FaceGroup(IReadOnlyList<FoundFace> faces, Func<NameSuggestion?> suggest)
{
    private readonly Lazy<NameSuggestion?> _suggestion = new(suggest);

    public IReadOnlyList<FoundFace> Faces { get; } = faces;

    /// <summary>Worked out when the group is shown: for thousands of groups at once it took many seconds.</summary>
    public NameSuggestion? Suggestion => _suggestion.Value;
}

/// <summary>Finding, recognising and naming the people in the photos.</summary>
public sealed partial class MainViewModel
{
    private static readonly string FaceModelFolder = FaceModel.DefaultFolder(DataFolder);
    private const uint FaceThumbnailSize = 96;

    private FaceAnalyzer? _faceAnalyzer;
    private FaceMatcher? _matcher;
    private readonly SemaphoreSlim _faceFinding = new(1, 1);
    private readonly HashSet<string> _facesNotFound = new(StringComparer.OrdinalIgnoreCase);
    private bool _findingNewFaces;
    private IReadOnlyList<string> _selectedPeople = [];
    private bool _isUpdatingPeople;

    /// <summary>Raised after faces were found or named, so views showing them can refresh.</summary>
    public event Action? FacesChanged;

    public static bool IsFaceModelInstalled => FaceModel.IsInstalled(FaceModelFolder);

    /// <summary>Everyone named in the library, with on how many photos. Ticking a name filters the grid.</summary>
    public ObservableCollection<TagFilterViewModel> People { get; } = [];

    public bool HasPeople => People.Count > 0;

    /// <summary>Everyone named, by name, for the "More" list in the filter bar.</summary>
    public IReadOnlyList<TagFilterViewModel> PeopleByName =>
        People.OrderBy(p => p.Tag, StringComparer.CurrentCultureIgnoreCase).ToList();

    /// <summary>Photos must show all of these people.</summary>
    public IReadOnlyList<string> SelectedPeople
    {
        get => _selectedPeople;
        private set
        {
            _selectedPeople = value;
            _ = SearchAsync();
        }
    }

    /// <summary>The People view's order (X-1): 0 most photos first, 1 by name; kept in the settings.</summary>
    public int PeopleSortIndex
    {
        get => _settings.PeopleSort == "name" ? 1 : 0;
        set
        {
            var key = value == 1 ? "name" : "count";
            if (key == _settings.PeopleSort)
                return;
            _settings.PeopleSort = key;
            _settings.Save(SettingsPath);
            OnPropertyChanged();
        }
    }

    /// <summary>Everyone named, in the People view's order.</summary>
    public IReadOnlyList<TagFilterViewModel> PeopleInViewOrder => PeopleSortIndex == 1 ? PeopleByName : People.ToList();

    /// <summary>The names already used in the library, most photos first, to pick from when naming a face.</summary>
    public IReadOnlyList<string> KnownNames => People.Select(p => p.Tag).ToList();

    private void UpdatePeople(IReadOnlyList<(string Name, int Count)> people)
    {
        if (people.Count == People.Count && people.Zip(People).All(p => p.First.Name == p.Second.Tag && p.First.Count == p.Second.Count))
            return;

        var ticked = new HashSet<string>(People.Where(t => t.IsSelected).Select(t => t.Tag), StringComparer.CurrentCultureIgnoreCase);
        _isUpdatingPeople = true;
        People.Clear();
        foreach (var (name, count) in people)
        {
            var item = new TagFilterViewModel(name, count, OnPersonSelectionChanged);
            if (ticked.Contains(name))
                item.IsSelected = true;
            People.Add(item);
        }
        _isUpdatingPeople = false;
        OnPropertyChanged(nameof(HasPeople));
        OnPropertyChanged(nameof(PeopleByName));

        var stillTicked = People.Where(t => t.IsSelected).Select(t => t.Tag).ToList();
        if (stillTicked.Count != _selectedPeople.Count)
            SelectedPeople = stillTicked;
    }

    private void OnPersonSelectionChanged()
    {
        if (_isUpdatingPeople)
            return;
        SelectedPeople = People.Where(t => t.IsSelected).Select(t => t.Tag).ToList();
    }

    /// <summary>Shows only the photos of one person (from the People workspace).</summary>
    public void ShowOnlyPerson(TagFilterViewModel person)
    {
        _isUpdatingPeople = true;
        foreach (var p in People)
            p.IsSelected = ReferenceEquals(p, person);
        _isUpdatingPeople = false;
        SelectedPeople = [person.Tag];
    }

    public void ClearPeopleFilter()
    {
        _isUpdatingPeople = true;
        foreach (var person in People)
            person.IsSelected = false;
        _isUpdatingPeople = false;
        if (_selectedPeople.Count > 0)
            SelectedPeople = [];
    }

    /// <summary>Downloads the face models once (about 39 MB), showing progress in the filter row.</summary>
    public async Task<bool> DownloadFaceModelAsync()
    {
        IsBusy = true;
        try
        {
            var progress = new Progress<double>(p => StatusText = Tr("Downloading the face models… {0:P0}", p));
            await FaceModel.DownloadAsync(FaceModelFolder, progress);
            Inform(Tr("The face models are ready."));
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException)
        {
            ErrorMessage = Tr("The face models could not be downloaded ({0}). Check the internet connection and try again.", ex.Message);
            StatusText = "";
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Looks for faces in the photos that were not looked at yet (or in all of them with <paramref name="again"/>)
    /// and keeps them in the index with a small picture of each. Returns how many unnamed faces there are now.
    /// </summary>
    public async Task<int> FindFacesAsync(IReadOnlyList<PhotoItemViewModel> items, bool again = false)
    {
        if (items.Count == 0)
        {
            Warn(Tr("Select the photos to find people in."));
            return 0;
        }

        var failures = new List<string>();
        var found = 0;
        var looked = 0;
        IsBusy = true;
        try
        {
            // Faces are only looked for on photos, not in videos.
            var todo = items.Where(i => !i.IsVideo).ToList();
            if (!again)
            {
                var done = await Task.Run(() => _index.GetFaceScanned(items.Select(i => i.Path)));
                todo = todo.Where(i => !done.Contains(i.Path)).ToList();
            }

            if (todo.Count > 0 && _faceAnalyzer is null)
            {
                StatusText = Tr("Loading the face models…");
                await LoadFaceAnalyzerAsync();
            }

            for (var i = 0; i < todo.Count; i++)
            {
                if (_stopRequested)
                    break; // Stop in the progress details: what is done stays done
                var item = todo[i];
                StatusText = Tr("Looking for people in {0} of {1}…", i + 1, todo.Count);
                try
                {
                    found += await LookForFacesAsync(item.Path, item.Photo.Orientation);
                    looked++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
                {
                    failures.Add($"{item.FileName}: {ex.Message}");
                }
            }
        }
        catch (Exception ex) when (ex is Microsoft.ML.OnnxRuntime.OnnxRuntimeException or IOException or InvalidDataException)
        {
            _faceAnalyzer?.Dispose();
            _faceAnalyzer = null;
            failures.Add(Tr("The face models could not be loaded ({0}).", ex.Message));
        }
        finally
        {
            IsBusy = false;
        }

        ForgetMatcher();
        var unnamed = await Task.Run(_index.CountUnnamedFaces);
        UnnamedFaceCount = unnamed;
        ErrorMessage = failures.Count == 0 ? null : Tr("Some photos could not be looked at.") + " " + string.Join(" ", failures);
        Inform(looked == 0 && failures.Count == 0
            ? Tr("These photos were already looked at.")
            : Tr("Found {0} face(s) in {1} photo(s). {2} face(s) in the library have no name yet.", found, looked, unnamed));
        FacesChanged?.Invoke();
        return unnamed;
    }

    /// <summary>
    /// Finds the faces on one photo and keeps them in the index; returns how many. One photo at a time,
    /// whether the user asked or the background search for new photos did.
    /// </summary>
    private async Task<int> LookForFacesAsync(string path, int orientation)
    {
        await _faceFinding.WaitAsync();
        try
        {
            var analyzer = await LoadFaceAnalyzerAsync();
            var image = await ImageLoader.DecodePixelsAsync(path, FaceAnalyzer.ImageSize);
            var faces = await Task.Run(() => analyzer.Analyze(image.Pixels, image.Width, image.Height));
            var newFaces = new List<NewFace>();
            foreach (var face in faces)
            {
                var thumbnail = await ImageEncoder.EncodeJpegAsync(FaceCrop(image, face.Box), FaceThumbnailSize, FaceThumbnailSize);
                // Kept on the stored pixels, like the people tags in the file.
                newFaces.Add(new NewFace(face.Box.FromUpright(orientation), face.Score, face.Embedding, thumbnail));
            }
            if (!await Task.Run(() => _index.SaveFaces(path, newFaces)))
                throw new IOException(Tr("The photo is no longer in the library."));
            return newFaces.Count;
        }
        finally
        {
            _faceFinding.Release();
        }
    }

    private async Task<FaceAnalyzer> LoadFaceAnalyzerAsync() =>
        _faceAnalyzer ??= await Task.Run(() => FaceAnalyzer.Load(FaceModelFolder));

    /// <summary>Whether new photos are looked at for faces without being asked (Settings › App).</summary>
    public bool FindPeopleAutomatically => _settings.FindPeopleAutomatically;

    public void SaveFindPeopleAutomatically(bool on)
    {
        if (on != _settings.FindPeopleAutomatically)
        {
            _settings.FindPeopleAutomatically = on;
            _settings.Save(SettingsPath);
        }
        // Also when it was on already but the face models only came just now.
        if (on)
            _ = FindFacesInNewPhotosAsync();
    }

    /// <summary>
    /// Looks for faces in every photo not looked at yet, one at a time and quietly, pausing while the app
    /// is busy with something the user asked for. Runs after each check of the folders, so new photos get
    /// their faces without anyone pressing Find people. Needs the face models, downloaded by Find people or
    /// when this is turned on in Settings.
    /// </summary>
    public async Task FindFacesInNewPhotosAsync()
    {
        if (_findingNewFaces || !_settings.FindPeopleAutomatically || !IsFaceModelInstalled)
            return;
        _findingNewFaces = true;
        try
        {
            await LoadFaceAnalyzerAsync();
            while (true)
            {
                const int Batch = 50;
                var todo = (await Task.Run(() => _index.GetNotFaceScanned(Batch + _facesNotFound.Count)))
                    .Where(p => !_facesNotFound.Contains(p.Path)).Take(Batch).ToList();
                if (todo.Count == 0)
                    return;
                var found = 0;
                foreach (var (path, orientation) in todo)
                {
                    // What the user asked for goes first.
                    while (IsBusy)
                        await Task.Delay(2000);
                    if (!_settings.FindPeopleAutomatically)
                        return;
                    try
                    {
                        found += await LookForFacesAsync(path, orientation);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or ArgumentException)
                    {
                        _facesNotFound.Add(path); // tried again next time the app starts
                    }
                }
                if (found > 0)
                {
                    ForgetMatcher();
                    FacesChanged?.Invoke();
                }
            }
        }
        catch (Exception ex) when (ex is Microsoft.ML.OnnxRuntime.OnnxRuntimeException or IOException or InvalidDataException)
        {
            // The models could not be loaded; Find people says why when the user asks.
            _faceAnalyzer?.Dispose();
            _faceAnalyzer = null;
        }
        finally
        {
            _findingNewFaces = false;
        }
    }

    /// <summary>A square around the face with some room, as the face's thumbnail.</summary>
    private static DecodedImage FaceCrop(DecodedImage image, FaceBox box)
    {
        var size = Math.Max(box.Width * image.Width, box.Height * image.Height) * 1.6;
        var side = (int)Math.Clamp(size, 8, Math.Min(image.Width, image.Height));
        var x = (int)Math.Clamp(box.CenterX * image.Width - side / 2.0, 0, image.Width - side);
        var y = (int)Math.Clamp(box.CenterY * image.Height - side / 2.0, 0, image.Height - side);
        return new DecodedImage(ImageMath.Crop(image.Pixels, image.Width, image.Height, new PixelRect(x, y, side, side)), side, side);
    }

    private readonly Dictionary<string, byte[]?> _personPictures = [];
    private readonly SemaphoreSlim _peopleWriting = new(1, 1);

    /// <summary>
    /// A picture of a person's face cut from the photo at the box in the file, for people named in another
    /// program (e.g. Windows Live Photo Gallery) whose face has not been found here. Null without a real box.
    /// </summary>
    public Task<byte[]?> GetPersonPictureAsync(PhotoItemViewModel item, PersonTag person) =>
        CutFaceAsync(item.Path, item.Photo.FileModifiedUtc, item.Photo.Orientation, person);

    /// <summary>The person's face on their newest photo, for the filter bar. Null when no photo marks their face.</summary>
    public async Task<byte[]?> GetLatestFaceAsync(string name)
    {
        var face = await Task.Run(() => _index.GetLatestFace(name));
        if (face is null)
            return null;
        return face.Thumbnail ?? await CutFaceAsync(face.Path, face.FileModifiedUtc, face.Orientation, face.Person);
    }

    private async Task<byte[]?> CutFaceAsync(string path, DateTime fileModifiedUtc, int orientation, PersonTag person)
    {
        if (person.Box.Area > 0.9)
            return null;
        var key = $"{path}|{fileModifiedUtc.Ticks}|{person.Box}";
        if (_personPictures.TryGetValue(key, out var cached))
            return cached;
        byte[]? picture = null;
        try
        {
            // The decoded pixels are upright, the box in the file is on the stored pixels.
            var image = await ImageLoader.DecodePixelsAsync(path, FaceAnalyzer.ImageSize);
            picture = await ImageEncoder.EncodeJpegAsync(FaceCrop(image, person.Box.ToUpright(orientation)), FaceThumbnailSize, FaceThumbnailSize);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
        }
        if (_personPictures.Count > 200)
            _personPictures.Clear();
        _personPictures[key] = picture;
        return picture;
    }

    public Task<IReadOnlyList<FoundFace>> GetFacesAsync(string path) => Task.Run(() => _index.GetFaces(path));

    public Task<bool> IsFaceScannedAsync(string path) => Task.Run(() => _index.GetFaceScanned([path]).Count > 0);

    /// <summary>Who an unnamed face probably is, from the faces already named.</summary>
    public async Task<NameSuggestion?> SuggestAsync(FoundFace face)
    {
        var matcher = await GetMatcherAsync();
        return matcher.Suggest(face.Embedding, face.Rejected);
    }

    private async Task<FaceMatcher> GetMatcherAsync() =>
        _matcher ??= await Task.Run(() => new FaceMatcher(_index.GetAllFaces()
            .Where(f => f.Person is not null)
            .Select(f => (f.Person!, f.Embedding))));

    private void ForgetMatcher() => _matcher = null;

    private static IEnumerable<T> Sample<T>(IReadOnlyList<T> items, int count) =>
        items.Count <= count ? items : Enumerable.Range(0, count).Select(i => items[i * items.Count / count]);

    /// <summary>
    /// The unnamed faces of the library in groups that look like the same person, biggest group first,
    /// each with a suggested name when it looks like someone already named.
    /// </summary>
    public async Task<IReadOnlyList<FaceGroup>> GroupUnnamedFacesAsync()
    {
        IsBusy = true;
        StatusText = Tr("Grouping the faces that have no name…");
        try
        {
            return await GroupUnnamedFacesCoreAsync();
        }
        finally
        {
            IsBusy = false;
            StatusText = _countText;
        }
    }

    private async Task<IReadOnlyList<FaceGroup>> GroupUnnamedFacesCoreAsync()
    {
        var matcher = await GetMatcherAsync();
        return await Task.Run(() =>
        {
            var unnamed = _index.GetAllFaces(withThumbnails: true).Where(f => f.Person is null).ToList();
            var groups = new List<FaceGroup>();
            foreach (var members in FaceMatcher.Group(unnamed.Select(f => f.Embedding).ToList()))
            {
                var faces = members.Select(i => unnamed[i]).ToList();
                // The name most of the group's faces point to; a sample of the group is enough.
                groups.Add(new FaceGroup(faces, () => Sample(faces, 12)
                    .Select(f => matcher.Suggest(f.Embedding, f.Rejected))
                    .OfType<NameSuggestion>()
                    .GroupBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
                    .OrderByDescending(g => g.Count()).ThenByDescending(g => g.Max(s => s.Similarity))
                    .Select(g => g.OrderByDescending(s => s.Similarity).First())
                    .FirstOrDefault()));
            }
            return (IReadOnlyList<FaceGroup>)groups;
        });
    }

    /// <summary>Names faces and writes the names into their photos. Returns how many photos were changed.</summary>
    public async Task<int> NameFacesAsync(IReadOnlyList<FoundFace> faces, string name)
    {
        name = name.Trim();
        if (string.IsNullOrEmpty(name))
            return 0;
        return await ChangePeopleAsync(faces, (people, face) => FaceLinks.Name(people, face.Box, name),
            (n, total) => Tr("Naming {0} in {1} of {2} photo(s)…", name, n, total));
    }

    /// <summary>Takes the name off a face, in the file too.</summary>
    public Task<int> UnnameFaceAsync(FoundFace face) =>
        ChangePeopleAsync([face], (people, f) => FaceLinks.Unname(people, f.Box),
            (n, total) => Tr("Removing the name from {0} of {1} photo(s)…", n, total));

    /// <summary>Removes a person named in the file of a photo, e.g. one tagged without a found face.</summary>
    public async Task RemovePersonAsync(PhotoItemViewModel item, PersonTag person)
    {
        if (_metadata is null || _indexer is null)
            return;
        await _peopleWriting.WaitAsync();
        try
        {
            var current = await _metadata.ReadAsync(item.Path);
            var people = current.People.Where(p => !(p.Name == person.Name && p.Box == person.Box)).ToList();
            await _metadata.WriteAsync(item.Path, new MetadataChanges { People = people });
            if (await _indexer.RefreshFileAsync(item.Path) is { } updated)
                item.Photo = updated;
        }
        catch (Exception ex) when (ex is ExifToolException or IOException or UnauthorizedAccessException)
        {
            ErrorMessage = Tr("Could not change {0}: {1}", item.FileName, ex.Message);
        }
        finally
        {
            _peopleWriting.Release();
        }
        await AfterPeopleChangedAsync();
    }

    /// <summary>Takes a person off all of <paramref name="items"/> (the info pane with several photos selected).</summary>
    public async Task RemovePersonFromAsync(IReadOnlyList<PhotoItemViewModel> items, string name)
    {
        if (_metadata is null || _indexer is null)
            return;
        var named = items.Where(i => i.Photo.People.Any(p => string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase))).ToList();
        if (named.Count == 0)
            return;
        await _peopleWriting.WaitAsync();
        var failures = new List<string>();
        var changed = 0;
        IsBusy = true;
        try
        {
            for (var i = 0; i < named.Count; i++)
            {
                if (_stopRequested)
                    break;
                var item = named[i];
                StatusText = Tr("Removing {0} from {1} of {2} photo(s)…", name, i + 1, named.Count);
                try
                {
                    var current = await _metadata.ReadAsync(item.Path);
                    var people = current.People.Where(p => !string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase)).ToList();
                    await _metadata.WriteAsync(item.Path, new MetadataChanges { People = people });
                    if (await _indexer.RefreshFileAsync(item.Path) is { } updated)
                        item.Photo = updated;
                    changed++;
                }
                catch (Exception ex) when (ex is ExifToolException or IOException or UnauthorizedAccessException)
                {
                    failures.Add($"{item.FileName}: {ex.Message}");
                }
            }
        }
        finally
        {
            IsBusy = false;
            _peopleWriting.Release();
        }
        ErrorMessage = failures.Count == 0 ? null : Tr("Some photos could not be changed.") + " " + string.Join(" ", failures);
        Inform(Tr("Removed {0} from {1}", name, PhotoCount(changed)));
        await AfterPeopleChangedAsync();
    }

    private async Task<int> ChangePeopleAsync(IReadOnlyList<FoundFace> faces, Func<IReadOnlyList<PersonTag>, FoundFace, IReadOnlyList<PersonTag>> change, Func<int, int, string> doing)
    {
        if (_metadata is null || _indexer is null || faces.Count == 0)
            return 0;

        // Naming runs in the background while the user browses, so changes wait their turn instead of
        // writing the same file at once.
        await _peopleWriting.WaitAsync();
        var failures = new List<string>();
        var photos = faces.GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
        var changed = 0;
        IsBusy = true;
        try
        {
            for (var i = 0; i < photos.Count; i++)
            {
                if (_stopRequested)
                    break; // Stop in the progress details: what is done stays done
                var path = photos[i].Key;
                StatusText = doing(i + 1, photos.Count);
                try
                {
                    // The file is the source of truth, so its people are read fresh before changing them.
                    var current = await _metadata.ReadAsync(path);
                    var people = current.People;
                    foreach (var face in photos[i])
                        people = change(people, face);
                    await _metadata.WriteAsync(path, new MetadataChanges { People = people });
                    var updated = await _indexer.RefreshFileAsync(path);
                    if (updated is not null && Photos.FirstOrDefault(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase)) is { } item)
                        item.Photo = updated;
                    changed++;
                }
                catch (Exception ex) when (ex is ExifToolException or IOException or UnauthorizedAccessException)
                {
                    failures.Add($"{Path.GetFileName(path)}: {ex.Message}");
                }
            }
        }
        finally
        {
            IsBusy = false;
            _peopleWriting.Release();
        }

        ErrorMessage = failures.Count == 0 ? null : Tr("Some photos could not be changed.") + " " + string.Join(" ", failures);
        Inform(Tr("Changed the people in {0}", PhotoCount(changed)));
        await AfterPeopleChangedAsync();
        return changed;
    }

    /// <summary>
    /// Tags a person on photos without marking a face, e.g. someone seen from behind. Like Windows Live Photo
    /// Gallery, the tag covers the whole photo. Photos that already name the person are left as they are.
    /// </summary>
    public async Task<int> AddPersonAsync(IReadOnlyList<PhotoItemViewModel> items, string name)
    {
        name = name.Trim();
        if (_metadata is null || _indexer is null || items.Count == 0 || name.Length == 0)
            return 0;

        await _peopleWriting.WaitAsync();
        var failures = new List<string>();
        var changed = 0;
        IsBusy = true;
        try
        {
            for (var i = 0; i < items.Count; i++)
            {
                if (_stopRequested)
                    break; // Stop in the progress details: what is done stays done
                var item = items[i];
                StatusText = Tr("Adding {0} to {1} of {2} photo(s)…", name, i + 1, items.Count);
                try
                {
                    var current = await _metadata.ReadAsync(item.Path);
                    if (current.People.Any(p => p.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase)))
                        continue;
                    await _metadata.WriteAsync(item.Path, new MetadataChanges { People = [.. current.People, new PersonTag(name, new FaceBox(0, 0, 1, 1))] });
                    if (await _indexer.RefreshFileAsync(item.Path) is { } updated)
                        item.Photo = updated;
                    changed++;
                }
                catch (Exception ex) when (ex is ExifToolException or IOException or UnauthorizedAccessException)
                {
                    failures.Add($"{item.FileName}: {ex.Message}");
                }
            }
        }
        finally
        {
            IsBusy = false;
            _peopleWriting.Release();
        }

        ErrorMessage = failures.Count == 0 ? null : Tr("Some photos could not be changed.") + " " + string.Join(" ", failures);
        Inform(Tr("Added {0} to {1}", name, PhotoCount(changed)));
        await AfterPeopleChangedAsync();
        return changed;
    }

    /// <summary>Remembers that the face is not this person, so they are not suggested for it again.</summary>
    public async Task RejectSuggestionAsync(FoundFace face, string name)
    {
        await Task.Run(() => _index.RejectName(face.Id, name));
        FacesChanged?.Invoke();
    }

    /// <summary>Hides faces that are not worth naming; they stay out of suggestions and groups.</summary>
    public async Task IgnoreFacesAsync(IEnumerable<FoundFace> faces)
    {
        var ids = faces.Select(f => f.Id).ToList();
        await Task.Run(() =>
        {
            foreach (var id in ids)
                _index.IgnoreFace(id);
        });
        ForgetMatcher();
        FacesChanged?.Invoke();
    }

    private async Task AfterPeopleChangedAsync()
    {
        ForgetMatcher();
        UpdatePeople(await Task.Run(_index.GetPeopleCounts));
        FacesChanged?.Invoke();
    }
}
