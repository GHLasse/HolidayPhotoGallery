using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using PhotoGallery.App.Imaging;
using PhotoGallery.App.ViewModels;
using PhotoGallery.Core.Library;
using PhotoGallery.Core.Metadata;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.App.Controls;

/// <summary>
/// An OpenStreetMap map (Leaflet in WebView2, from Assets\map.html) that shows where the photos were
/// taken. Clicking the map or finding a place sets a pin, and nothing is written until the user assigns
/// the pin to the selected photos, as in Geosetter.
/// </summary>
public sealed partial class MapPanel : UserControl
{
    // Served from the app folder under this made-up https address, so OpenStreetMap sees a proper
    // referrer (it refuses map tiles to pages without one).
    private const string HostName = "map.holidayphotogallery.example";

    private readonly List<string> _pending = [];
    private Task? _starting;
    private bool _ready;
    private GeoPosition? _pin;
    private IReadOnlyList<PhotoItemViewModel> _selection = [];

    public MapPanel()
    {
        InitializeComponent();
    }

    /// <summary>Raised when the user assigns the pin to the selected photos.</summary>
    public event Func<IReadOnlyList<PhotoItemViewModel>, GeoPosition, Task>? AssignRequested;

    /// <summary>Raised when the user assigns the estimated positions, each photo its own (G-3).</summary>
    public event Func<IReadOnlyList<(PhotoItemViewModel Item, GeoPosition Position)>, Task>? AssignEachRequested;

    /// <summary>Estimates where the photos were taken from the geotagged photos before and after them; null: no guess.</summary>
    public Func<IReadOnlyList<PhotoItemViewModel>, Task<IReadOnlyList<(PhotoItemViewModel Item, LocationGuess? Guess)>>>? Estimator { get; set; }

    // The estimate shown for the selection (one or more photos), until the user sets a pin of their own.
    private List<(PhotoItemViewModel Item, GeoPosition Position)>? _estimates;
    private bool _pinIsEstimate;
    private int _estimateRun;

    /// <summary>Raised when the user removes the location from the selected photos.</summary>
    public event Func<IReadOnlyList<PhotoItemViewModel>, Task>? RemoveRequested;

    /// <summary>Raised when a photo's marker is clicked; true means Ctrl was held to add it to the selection.</summary>
    public event Action<string, bool>? PhotoClicked;

    /// <summary>Loads the map the first time it is shown; WebView2 is only started when needed.</summary>
    public Task StartAsync() => _starting ??= StartCoreAsync();

    private async Task StartCoreAsync()
    {
        try
        {
            // Keep the browser's data with the app's other data, not next to the .exe, where it would
            // lock the app folder.
            var dataFolder = Path.Combine(LibrarySettings.DefaultDataFolder, "WebView2");
            var environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, dataFolder, new CoreWebView2EnvironmentOptions());
            await Web.EnsureCoreWebView2Async(environment);
            Web.CoreWebView2.SetVirtualHostNameToFolderMapping(
                HostName, Path.Combine(AppContext.BaseDirectory, "Assets"), CoreWebView2HostResourceAccessKind.Allow);
            Web.CoreWebView2.Settings.AreDevToolsEnabled = false;
            Web.CoreWebView2.Settings.IsStatusBarEnabled = false;
            Web.WebMessageReceived += Web_WebMessageReceived;
            // The map’s labels follow the app’s language (see map.html). Strings.Language in full: a bare "Language"
            // here is the control’s own XAML language, which is Windows’ language.
            // The satellite view is sharp with the app's Esri key and falls back to Sentinel-2 cloudless without it.
            var esri = MainViewModel.SatelliteKey is { } key ? "&esri=" + Uri.EscapeDataString(key) : "";
            Web.Source = new Uri($"https://{HostName}/map.html?lang={Uri.EscapeDataString(PhotoGallery.Core.Localization.Strings.Language)}{esri}");
        }
        catch (Exception ex)
        {
            PinText.Text = Tr("The map could not start: {0} It needs the Microsoft Edge WebView2 Runtime, which Windows 11 includes.", ex.Message);
        }
    }

    /// <summary>Shows a marker for every photo in the list that has a GPS position.</summary>
    public void ShowPhotos(IEnumerable<PhotoItemViewModel> photos) => ShowPhotos(photos.Select(p => p.Photo), fitAll: false);

    /// <summary>Shows a marker for every photo with a GPS position; <paramref name="fitAll"/> zooms out to show them all.</summary>
    public void ShowPhotos(IEnumerable<IndexedPhoto> photos, bool fitAll)
    {
        var list = new JsonArray();
        foreach (var photo in photos)
        {
            if (photo.Location is { } at)
                list.Add(new JsonObject { ["path"] = photo.Path, ["name"] = Path.GetFileName(photo.Path), ["lat"] = at.Latitude, ["lon"] = at.Longitude });
        }
        // The same markers again (a filter that changed nothing on the map) aren't redrawn: with tens of
        // thousands of photos that takes the map a moment.
        var markers = list.ToJsonString();
        if (markers == _shownMarkers && !fitAll)
            return;
        _shownMarkers = markers;
        Post(new JsonObject { ["type"] = "photos", ["photos"] = list, ["fitAll"] = fitAll });
    }

    private string? _shownMarkers;

    /// <summary>Highlights the selected photos, and with <paramref name="fit"/> moves the map to them.</summary>
    public void ShowSelection(IReadOnlyList<PhotoItemViewModel> selection, bool fit)
    {
        _selection = selection;
        var paths = new JsonArray();
        foreach (var photo in selection)
            paths.Add(photo.Path);
        Post(new JsonObject { ["type"] = "selection", ["paths"] = paths, ["fit"] = fit });

        // With one photo selected, its own position becomes the pin, ready to be dragged.
        if (selection is [{ Photo.Location: { } own }])
        {
            SetPin(own, tellMap: true);
            _pinIsEstimate = false;
        }
        // Photos without GPS and no pin of the user's: suggest where they were (G-3).
        if (CanPlacePhotos && selection.Count > 0 && selection.All(p => p.Photo.Location is null) && (_pin is null || _pinIsEstimate))
            _ = EstimateAsync(auto: true);
        else
            ClearEstimates();
        UpdateButtons();
    }

    /// <summary>Raised when the picture or Open in a photo's bubble on the whole-library map is clicked.</summary>
    public event Action<string>? PhotoOpened;

    /// <summary>Raised by "Show in library" in a photo's bubble.</summary>
    public event Action<string>? PhotoShown;

    // The picture shown above a clicked dot: Windows' thumbnail, sent to the map as a small JPEG.
    private async Task SendPreviewAsync(string path)
    {
        try
        {
            var jpeg = await ImageLoader.LoadShellThumbnailJpegAsync(path, 256);
            if (jpeg is not null)
                Post(new JsonObject { ["type"] = "preview", ["path"] = path, ["src"] = "data:image/jpeg;base64," + Convert.ToBase64String(jpeg) });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            // Without a picture the bubble keeps the name.
        }
    }

    /// <summary>The places found for a search (<see cref="FindPlace"/>), best first; the map went to the first.</summary>
    public event Action<string, IReadOnlyList<string>>? PlacesFound;

    /// <summary>Looks the place up on OpenStreetMap and goes there; <see cref="PlacesFound"/> tells what was found.</summary>
    public void FindPlace(string query) => Post(new JsonObject { ["type"] = "find", ["q"] = query });

    /// <summary>Goes to another of the places the last search found.</summary>
    public void GoToPlace(int index) => Post(new JsonObject { ["type"] = "goto", ["index"] = index });

    private void SetPin(GeoPosition? position, bool tellMap)
    {
        _pin = position;
        if (tellMap)
        {
            Post(position is { } p
                ? new JsonObject { ["type"] = "pin", ["lat"] = p.Latitude, ["lon"] = p.Longitude }
                : new JsonObject { ["type"] = "pin", ["lat"] = null, ["lon"] = null });
        }
        PinText.Text = position is { } at
            ? Tr("Pin: {0:0.00000}, {1:0.00000}", at.Latitude, at.Longitude)
            : Tr("Click the map or search for a place to set a pin.");
        UpdateButtons();
    }

    /// <summary>Whether the pin and the Assign and Remove buttons are offered; not while browsing the whole map.</summary>
    public bool CanPlacePhotos
    {
        get => AssignRow.Visibility == Visibility.Visible;
        set
        {
            AssignRow.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            Post(new JsonObject { ["type"] = "placing", ["on"] = value });
            if (!value)
                SetPin(null, tellMap: false);
        }
    }

    private void UpdateButtons()
    {
        var count = _selection.Count;
        AssignButton.IsEnabled = _pin is not null && count > 0;
        AssignText.Text = count switch
        {
            0 => Tr("Assign to selected"),
            1 => Tr("Assign to 1 photo"),
            _ => Tr("Assign to {0:N0} photos", count),
        };
        if (_estimates is { Count: > 0 } each && _pin is null)
        {
            AssignButton.IsEnabled = true;
            AssignText.Text = Tr("Assign estimates to {0:N0} photos", each.Count);
        }
        EstimateButton.IsEnabled = _selection.Any(p => p.Photo.Location is null && p.Photo.DateTaken is not null) && Estimator is not null;
        RemoveButton.IsEnabled = _selection.Any(p => p.Photo.Location is not null);
    }

    private void Post(JsonObject message)
    {
        var json = message.ToJsonString();
        if (_ready)
            Web.CoreWebView2.PostWebMessageAsJson(json);
        else
        {
            // Only the latest message of each kind matters until the page is ready.
            var type = message["type"]!.GetValue<string>();
            _pending.RemoveAll(m => JsonNode.Parse(m)!["type"]!.GetValue<string>() == type);
            _pending.Add(json);
        }
    }

    private void Web_WebMessageReceived(WebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        JsonNode? message;
        try
        {
            message = JsonNode.Parse(args.WebMessageAsJson);
        }
        catch (JsonException)
        {
            return;
        }

        switch (message?["type"]?.GetValue<string>())
        {
            case "ready":
                _ready = true;
                Web.CoreWebView2.PostWebMessageAsJson(new JsonObject
                {
                    ["type"] = "texts",
                    ["open"] = Tr("Open"),
                    ["show"] = Tr("Show in library"),
                    ["of"] = Tr("{0} of {1}"),
                    ["estimate"] = Tr("Estimate from photos before and after"),
                    ["map"] = Tr("Map"),
                    ["satellite"] = Tr("Satellite"),
                    ["labels"] = Tr("Labels"),
                }.ToJsonString());
                foreach (var json in _pending)
                    Web.CoreWebView2.PostWebMessageAsJson(json);
                _pending.Clear();
                break;
            case "pin":
                var position = new GeoPosition(message["lat"]!.GetValue<double>(), message["lon"]!.GetValue<double>());
                // A pin of the user's own replaces the estimate.
                ClearEstimates();
                SetPin(position.IsValid ? position : Wrapped(position), tellMap: false);
                break;
            case "estimate":
                _ = EstimateAsync(auto: false);
                break;
            case "select":
                var path = message["path"]!.GetValue<string>();
                PhotoClicked?.Invoke(path, message["add"]?.GetValue<bool>() == true);
                if (!CanPlacePhotos)
                    _ = SendPreviewAsync(path);
                break;
            case "open":
                if (message["path"]?.GetValue<string>() is { } open)
                    PhotoOpened?.Invoke(open);
                break;
            case "show":
                if (message["path"]?.GetValue<string>() is { } shown)
                    PhotoShown?.Invoke(shown);
                break;
            case "places":
                PlacesFound?.Invoke(message["q"]?.GetValue<string>() ?? "",
                    message["names"]?.AsArray().Select(n => n?.GetValue<string>() ?? "").ToList() ?? []);
                break;
        }
    }

    // Leaflet lets the world repeat sideways, so a click can be at e.g. longitude 200; bring it back into range.
    private static GeoPosition Wrapped(GeoPosition p) =>
        new(Math.Clamp(p.Latitude, -90, 90), ((p.Longitude + 180) % 360 + 360) % 360 - 180);

    private async void Assign_Click(object sender, RoutedEventArgs e)
    {
        if (_estimates is { Count: > 0 } each && _pin is null && AssignEachRequested is { } assignEach)
        {
            ClearEstimates();
            await assignEach(each);
            return;
        }
        ClearEstimates();
        if (_pin is { } pin && _selection.Count > 0 && AssignRequested is { } handler)
            await handler(_selection, pin);
    }

    private void Estimate_Click(object sender, RoutedEventArgs e) => _ = EstimateAsync(auto: false);

    /// <summary>
    /// Suggests where the selected photos without GPS were taken: one photo gets the pin there, ready to drag and
    /// assign; several get a red dot each, assigned together, each to its own position.
    /// </summary>
    private async Task EstimateAsync(bool auto)
    {
        var run = ++_estimateRun;
        var photos = _selection.Where(p => p.Photo.Location is null).ToList();
        if (Estimator is null || photos.Count == 0)
            return;
        IReadOnlyList<(PhotoItemViewModel Item, LocationGuess? Guess)> guesses;
        try
        {
            guesses = await Estimator(photos);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            return;
        }
        if (run != _estimateRun)
            return; // the selection changed meanwhile
        var found = guesses.Where(g => g.Guess is not null).Select(g => (g.Item, g.Guess!)).ToList();
        if (found.Count == 0)
        {
            ClearEstimates();
            if (_pinIsEstimate)
                SetPin(null, tellMap: true);
            _pinIsEstimate = false;
            if (!auto || _pin is null)
                PinText.Text = photos.All(p => p.Photo.DateTaken is null)
                    ? Tr("No estimate: the photos have no date taken.")
                    : Tr("No estimate: there is no geotagged photo taken before and after. Click the map to set a pin.");
            return;
        }

        _estimates = found.Select(f => (f.Item, f.Item2.Position)).ToList();
        PostEstimates();
        if (found.Count == 1 && _selection.Count == 1)
        {
            SetPin(found[0].Item2.Position, tellMap: true);
            _pinIsEstimate = true;
        }
        else if (_pinIsEstimate || !auto)
        {
            SetPin(null, tellMap: true);
            _pinIsEstimate = true;
        }
        var rough = found.Max(f => f.Item2.Gap);
        var text = found.Count == 1 && _selection.Count == 1
            ? Tr("Estimated from the photos before and after. Drag the pin to correct it, then assign it.")
            : Tr("Estimated {0} of {1} photos from the photos before and after (red dots).", found.Count, _selection.Count);
        if (rough > LocationEstimate.RoughAfter)
            text += " " + Tr("The photos around them are {0:N0} hours apart, so it may be rough.", rough.TotalHours);
        PinText.Text = text;
        UpdateButtons();
    }

    private void PostEstimates()
    {
        var points = new JsonArray();
        foreach (var (item, at) in _estimates ?? [])
            points.Add(new JsonObject { ["lat"] = at.Latitude, ["lon"] = at.Longitude, ["name"] = item.FileName });
        Post(new JsonObject { ["type"] = "estimates", ["points"] = points });
    }

    private void ClearEstimates()
    {
        _estimateRun++;
        if (_estimates is null)
            return;
        _estimates = null;
        _pinIsEstimate = false;
        PostEstimates();
        UpdateButtons();
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (_selection.Count > 0 && RemoveRequested is { } handler)
            await handler(_selection);
    }
}
