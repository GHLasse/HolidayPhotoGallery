using System.Collections.ObjectModel;
using PhotoGallery.App.Imaging;
using PhotoGallery.Core.Editing;
using PhotoGallery.Core.ExifTool;
using PhotoGallery.Core.Faces;
using PhotoGallery.Core.Library;
using PhotoGallery.Core.Metadata;

namespace PhotoGallery.App.ViewModels;

/// <summary>Unnamed faces that look like the same person, and who they may be.</summary>
public sealed record FaceGroup(IReadOnlyList<FoundFace> Faces, NameSuggestion? Suggestion);

/// <summary>Finding, recognising and naming the people in the photos.</summary>
public sealed partial class MainViewModel
{
    private static readonly string FaceModelFolder = FaceModel.DefaultFolder(DataFolder);
    private const uint FaceThumbnailSize = 96;

    private FaceAnalyzer? _faceAnalyzer;
    private FaceMatcher? _matcher;
    private IReadOnlyList<string> _selectedPeople = [];
    private bool _isUpdatingPeople;

    /// <summary>Raised after faces were found or named, so views showing them can refresh.</summary>
    public event Action? FacesChanged;

    public static bool IsFaceModelInstalled => FaceModel.IsInstalled(FaceModelFolder);

    /// <summary>Everyone named in the library, with on how many photos. Ticking a name filters the grid.</summary>
    public ObservableCollection<TagFilterViewModel> People { get; } = [];

    public bool HasPeople => People.Count > 0;

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

    public void ClearPeopleFilter()
    {
        _isUpdatingPeople = true;
        foreach (var person in People)
            person.IsSelected = false;
        _isUpdatingPeople = false;
        if (_selectedPeople.Count > 0)
            SelectedPeople = [];
    }

    /// <summary>Downloads the face models once (about 39 MB), showing progress in the status bar.</summary>
    public async Task<bool> DownloadFaceModelAsync()
    {
        IsBusy = true;
        try
        {
            var progress = new Progress<double>(p => StatusText = $"Downloading the face models… {p:P0}");
            await FaceModel.DownloadAsync(FaceModelFolder, progress);
            StatusText = "The face models are ready.";
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException)
        {
            ErrorMessage = $"The face models could not be downloaded ({ex.Message}). Check the internet connection and try again.";
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
            ErrorMessage = "Select the photos to find people in.";
            return 0;
        }

        var failures = new List<string>();
        var found = 0;
        var looked = 0;
        IsBusy = true;
        try
        {
            var todo = items;
            if (!again)
            {
                var done = await Task.Run(() => _index.GetFaceScanned(items.Select(i => i.Path)));
                todo = items.Where(i => !done.Contains(i.Path)).ToList();
            }

            if (todo.Count > 0 && _faceAnalyzer is null)
            {
                StatusText = "Loading the face models…";
                _faceAnalyzer = await Task.Run(() => FaceAnalyzer.Load(FaceModelFolder));
            }

            for (var i = 0; i < todo.Count; i++)
            {
                var item = todo[i];
                StatusText = $"Looking for people in {i + 1} of {todo.Count}…";
                try
                {
                    var image = await ImageLoader.DecodePixelsAsync(item.Path, FaceAnalyzer.ImageSize);
                    var analyzer = _faceAnalyzer!;
                    var faces = await Task.Run(() => analyzer.Analyze(image.Pixels, image.Width, image.Height));
                    var newFaces = new List<NewFace>();
                    foreach (var face in faces)
                    {
                        var thumbnail = await ImageEncoder.EncodeJpegAsync(FaceCrop(image, face.Box), FaceThumbnailSize, FaceThumbnailSize);
                        // Kept on the stored pixels, like the people tags in the file.
                        newFaces.Add(new NewFace(face.Box.FromUpright(item.Photo.Orientation), face.Score, face.Embedding, thumbnail));
                    }
                    await Task.Run(() => _index.SaveFaces(item.Path, newFaces));
                    found += newFaces.Count;
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
            failures.Add($"The face models could not be loaded ({ex.Message}).");
        }
        finally
        {
            IsBusy = false;
        }

        ForgetMatcher();
        var unnamed = await Task.Run(() => _index.GetAllFaces().Count(f => f.Person is null));
        ErrorMessage = failures.Count == 0 ? null : "Some photos could not be looked at. " + string.Join(" ", failures);
        StatusText = looked == 0 && failures.Count == 0
            ? "These photos were already looked at."
            : $"Found {found} face(s) in {looked} photo(s). {unnamed} face(s) in the library have no name yet.";
        FacesChanged?.Invoke();
        return unnamed;
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

    /// <summary>
    /// The unnamed faces of the library in groups that look like the same person, biggest group first,
    /// each with a suggested name when it looks like someone already named.
    /// </summary>
    public async Task<IReadOnlyList<FaceGroup>> GroupUnnamedFacesAsync()
    {
        var matcher = await GetMatcherAsync();
        return await Task.Run(() =>
        {
            var unnamed = _index.GetAllFaces(withThumbnails: true).Where(f => f.Person is null).ToList();
            var groups = new List<FaceGroup>();
            foreach (var members in FaceMatcher.Group(unnamed.Select(f => f.Embedding).ToList()))
            {
                var faces = members.Select(i => unnamed[i]).ToList();
                // The name most of the group's faces point to.
                var suggestion = faces
                    .Select(f => matcher.Suggest(f.Embedding, f.Rejected))
                    .OfType<NameSuggestion>()
                    .GroupBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
                    .OrderByDescending(g => g.Count()).ThenByDescending(g => g.Max(s => s.Similarity))
                    .Select(g => g.OrderByDescending(s => s.Similarity).First())
                    .FirstOrDefault();
                groups.Add(new FaceGroup(faces, suggestion));
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
        return await ChangePeopleAsync(faces, (people, face) => FaceLinks.Name(people, face.Box, name), $"Naming {name} in");
    }

    /// <summary>Takes the name off a face, in the file too.</summary>
    public Task<int> UnnameFaceAsync(FoundFace face) =>
        ChangePeopleAsync([face], (people, f) => FaceLinks.Unname(people, f.Box), "Removing the name from");

    /// <summary>Removes a person named in the file of a photo, e.g. one tagged without a found face.</summary>
    public async Task RemovePersonAsync(PhotoItemViewModel item, PersonTag person)
    {
        if (_metadata is null || _indexer is null)
            return;
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
            ErrorMessage = $"Could not change {item.FileName}: {ex.Message}";
        }
        await AfterPeopleChangedAsync();
    }

    private async Task<int> ChangePeopleAsync(IReadOnlyList<FoundFace> faces, Func<IReadOnlyList<PersonTag>, FoundFace, IReadOnlyList<PersonTag>> change, string doing)
    {
        if (_metadata is null || _indexer is null || faces.Count == 0)
            return 0;

        var failures = new List<string>();
        var photos = faces.GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
        var changed = 0;
        IsBusy = true;
        try
        {
            for (var i = 0; i < photos.Count; i++)
            {
                var path = photos[i].Key;
                StatusText = $"{doing} {i + 1} of {photos.Count} photo(s)…";
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
        }

        ErrorMessage = failures.Count == 0 ? null : "Some photos could not be changed. " + string.Join(" ", failures);
        StatusText = $"Changed the people in {changed} photo(s)";
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
