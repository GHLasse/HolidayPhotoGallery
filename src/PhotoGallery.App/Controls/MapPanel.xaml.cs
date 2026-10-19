using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
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
            Web.Source = new Uri($"https://{HostName}/map.html");
        }
        catch (Exception ex)
        {
            PinText.Text = Tr("The map could not start: {0} It needs the Microsoft Edge WebView2 Runtime, which Windows 11 includes.", ex.Message);
        }
    }

    /// <summary>Shows a marker for every photo in the list that has a GPS position.</summary>
    public void ShowPhotos(IEnumerable<PhotoItemViewModel> photos)
    {
        var list = new JsonArray();
        foreach (var photo in photos)
        {
            if (photo.Photo.Location is { } at)
                list.Add(new JsonObject { ["path"] = photo.Path, ["name"] = photo.FileName, ["lat"] = at.Latitude, ["lon"] = at.Longitude });
        }
        Post(new JsonObject { ["type"] = "photos", ["photos"] = list });
    }

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
            SetPin(own, tellMap: true);
        UpdateButtons();
    }

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
                foreach (var json in _pending)
                    Web.CoreWebView2.PostWebMessageAsJson(json);
                _pending.Clear();
                break;
            case "pin":
                var position = new GeoPosition(message["lat"]!.GetValue<double>(), message["lon"]!.GetValue<double>());
                SetPin(position.IsValid ? position : Wrapped(position), tellMap: false);
                break;
            case "select":
                PhotoClicked?.Invoke(message["path"]!.GetValue<string>(), message["add"]?.GetValue<bool>() == true);
                break;
        }
    }

    // Leaflet lets the world repeat sideways, so a click can be at e.g. longitude 200; bring it back into range.
    private static GeoPosition Wrapped(GeoPosition p) =>
        new(Math.Clamp(p.Latitude, -90, 90), ((p.Longitude + 180) % 360 + 360) % 360 - 180);

    private async void Assign_Click(object sender, RoutedEventArgs e)
    {
        if (_pin is { } pin && _selection.Count > 0 && AssignRequested is { } handler)
            await handler(_selection, pin);
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (_selection.Count > 0 && RemoveRequested is { } handler)
            await handler(_selection);
    }
}
