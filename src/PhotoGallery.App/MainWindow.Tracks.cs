using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhotoGallery.App.Controls;
using PhotoGallery.App.ViewModels;
using PhotoGallery.Core.Tracks;
using Windows.Storage.Pickers;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.App;

/// <summary>
/// GPS tracks (G-4): the track list (Library › GPS tracks…), where tracks are added, renamed, deleted and shown
/// on the map, and the thin line the map draws under the photos around the selected one. Placing photos from a
/// track goes through the map's estimate: photos without GPS get red dots where the track was at their time,
/// and nothing is written until they are assigned.
/// </summary>
public sealed partial class MainWindow
{
    private int _trackLineRun;

    /// <summary>The track (or the route between photos) for 4 hours around the selected photo, under the map's dots.</summary>
    private async Task ShowTrackLineAsync()
    {
        var run = ++_trackLineRun;
        var current = SelectedPhotos is [var first, ..] ? first : null;
        try
        {
            var (line, isRoute) = await ViewModel.TrackLineAsync(current);
            if (run == _trackLineRun && IsMapOpen)
                MapView.ShowTrack(line, isRoute);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            App.Trace($"Track line: {ex.Message}");
        }
    }

    private async Task ShowTracksAsync()
    {
        var list = new StackPanel { Spacing = 6 };
        var hint = new TextBlock
        {
            Text = Tr("Tracks from your phone, watch or GPS logger: GPX, KML or KMZ, Google Maps Timeline (on the phone: Settings › Location › Location services › Timeline › Export timeline data) or the older Google Takeout location history, and Garmin FIT. They are copied into the app's data folder and never leave this PC."),
            TextWrapping = TextWrapping.Wrap,
        };
        var how = new TextBlock
        {
            Text = Tr("To place photos: select photos without a location and open the map. Where a track covers their time they get red dots on the track; Assign writes them into the files. Photos that already have a location keep it."),
            TextWrapping = TextWrapping.Wrap,
            Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
        };

        // How the camera's clock relates to the track's (which is always UTC).
        var zones = TimeZoneChoice.List(null);
        var zone = new ComboBox { Header = Tr("Time zone the camera's clock was set to (for photos that don't say)"), ItemsSource = zones, HorizontalAlignment = HorizontalAlignment.Stretch };
        var current = ViewModel.TrackTimeZone;
        zone.SelectedIndex = Math.Max(0, zones.FindIndex(z => z.Zone?.Id == current.Id));
        TimeZoneTyping.Attach(zone);
        var clock = new NumberBox
        {
            Header = Tr("Camera clock ahead by (minutes, negative if behind)"),
            Value = ViewModel.TrackClockErrorMinutes, Minimum = -24 * 60, Maximum = 24 * 60, SmallChange = 1,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        };
        var gap = new NumberBox
        {
            Header = Tr("Leave a photo blank when the track has no point for (minutes)"),
            Value = ViewModel.TrackMaxGapMinutes, Minimum = 1, Maximum = 24 * 60, SmallChange = 5,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        };

        var add = new Button { Content = Tr("Add tracks…"), Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        ContentDialog? dialog = null;
        string? showOnMap = null;

        void Fill()
        {
            list.Children.Clear();
            var tracks = ViewModel.TrackFiles.List();
            if (tracks.Count == 0)
                list.Children.Add(new TextBlock { Text = Tr("No tracks yet."), Foreground = ThemeBrush("TextFillColorSecondaryBrush") });
            foreach (var track in tracks)
                list.Children.Add(TrackRow(track));
        }

        FrameworkElement TrackRow(TrackInfo track)
        {
            var name = new TextBox { Text = track.Name, MinWidth = 200 };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(name, Tr("Track name"));
            name.LostFocus += (_, _) => Rename();
            name.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) Rename(); };
            void Rename()
            {
                if (name.Text.Trim() == track.Name || name.Text.Trim().Length == 0)
                    return;
                try
                {
                    ViewModel.TrackFiles.Rename(track.Path, name.Text);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    ViewModel.ErrorMessage = Tr("Could not rename the track: {0}", ex.Message);
                }
                Fill();
            }
            var what = new TextBlock
            {
                Text = track.Error is { } error ? Tr("Can't be read: {0}", error)
                    : track.StartUtc is { } start && track.EndUtc is { } end
                        ? Tr("{0} to {1} · {2:N0} points", start.ToLocalTime().ToString("g"), end.ToLocalTime().ToString("g"), track.PointCount)
                        : "",
                FontSize = 12,
                Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
                TextWrapping = TextWrapping.Wrap,
            };
            var show = IconButton("\uE707", Tr("Show on the map"), () =>
            {
                showOnMap = track.Path;
                dialog?.Hide();
                return Task.CompletedTask;
            });
            show.IsEnabled = track.Error is null;
            var delete = IconButton("\uE74D", Tr("Delete the track"), () =>
            {
                try
                {
                    ViewModel.TrackFiles.Delete(track.Path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    ViewModel.ErrorMessage = Tr("Could not delete the track: {0}", ex.Message);
                }
                Fill();
                return Task.CompletedTask;
            });
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel { Spacing = 2, Children = { name, what } };
            Grid.SetColumn(show, 1);
            Grid.SetColumn(delete, 2);
            row.Children.Add(text);
            row.Children.Add(show);
            row.Children.Add(delete);
            return row;
        }

        add.Click += async (_, _) =>
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.Downloads };
            foreach (var type in TrackReader.Extensions)
                picker.FileTypeFilter.Add(type);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);
            var files = await picker.PickMultipleFilesAsync();
            var failed = new List<string>();
            foreach (var file in files)
            {
                try
                {
                    await Task.Run(() => ViewModel.TrackFiles.Add(file.Path));
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
                {
                    failed.Add($"{file.Name}: {ex.Message}");
                }
            }
            if (failed.Count > 0)
                ViewModel.ErrorMessage = Tr("Some tracks could not be added.") + " " + string.Join(" ", failed);
            Fill();
        };

        Fill();
        dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = Tr("GPS tracks"),
            Content = new ScrollViewer
            {
                MaxHeight = 560,
                Padding = new Thickness(0, 0, 12, 0),
                Content = new StackPanel
                {
                    Spacing = 12,
                    MinWidth = 480,
                    Children = { hint, add, list, new Border { Height = 1, Background = ThemeBrush("DividerStrokeColorDefaultBrush") }, zone, clock, gap, how },
                },
            },
            CloseButtonText = Tr("Close"),
            DefaultButton = ContentDialogButton.Close,
        };
        await dialog.ShowThemedAsync();

        ViewModel.SaveTrackSettings(zone.SelectedItem is TimeZoneChoice { Zone: { } picked } ? picked : null,
                                    double.IsNaN(clock.Value) ? 0 : (int)Math.Round(clock.Value),
                                    double.IsNaN(gap.Value) ? 30 : (int)Math.Round(gap.Value));
        if (showOnMap is { } path)
        {
            ShowWorkspace(Workspace.Map);
            await MapView.StartAsync();
            MapView.ShowTrack(await ViewModel.TrackPointsAsync(path), isRoute: false, fit: true);
        }
        else if (IsMapOpen)
        {
            MapView.ShowSelection(SelectedPhotos, fit: false); // estimates again with the new settings
            _ = ShowTrackLineAsync();
        }
    }
}
