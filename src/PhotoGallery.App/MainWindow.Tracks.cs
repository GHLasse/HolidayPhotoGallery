using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhotoGallery.App.Controls;
using PhotoGallery.App.ViewModels;
using PhotoGallery.Core.Tracks;
using Windows.Storage.Pickers;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.App;

/// <summary>
/// GPS tracks (G-4): Settings › GPS tracks (also Library › GPS tracks…), where tracks are added, renamed and
/// deleted, the map's Tracks switch, and the thin line the map draws under the photos around the selected one. Placing photos from a
/// track goes through the map's estimate: photos without GPS get red dots where the track was at their time,
/// and nothing is written until they are assigned.
/// </summary>
public sealed partial class MainWindow
{
    private int _trackLineRun;

    /// <summary>
    /// The lines under the map's dots. With a photo selected: the tracks (or the route between photos) for 4 hours
    /// around it. With none: the tracks between the first and last photo shown, the folder's or album's trip.
    /// The map's Tracks switch turns them off; the leg a red dot was estimated on stays, it explains the dot.
    /// </summary>
    private async Task ShowTrackLineAsync()
    {
        var run = ++_trackLineRun;
        var current = SelectedPhotos is [var first, ..] ? first : null;
        try
        {
            IReadOnlyList<IReadOnlyList<PhotoGallery.Core.Metadata.GeoPosition>> lines;
            var isRoute = false;
            PhotoGallery.Core.Metadata.LocationGuess? leg = null;
            if (current is not null)
                (lines, isRoute, leg) = await ViewModel.TrackLineAsync(current);
            else if (MapView.TracksOn)
                lines = await ViewModel.TrackLinesForPhotosAsync(_workspace == Workspace.Map ? _mapPhotos : ViewModel.Photos.Select(p => p.Photo).ToList());
            else
                lines = [];
            if (!MapView.TracksOn && leg is null)
                lines = [];
            if (run == _trackLineRun && IsMapOpen)
                MapView.ShowTrack(lines, isRoute, label: leg is null ? null : LegText(leg));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            App.Trace($"Track line: {ex.Message}");
        }
    }

    /// <summary>The legend for the leg an estimate lies on: how far and how long apart its two ends are.</summary>
    private static string LegText(PhotoGallery.Core.Metadata.LocationGuess leg)
    {
        var gap = leg.Gap;
        var time = gap.TotalMinutes < 90 ? Tr("{0:N0} minutes", Math.Max(1, Math.Round(gap.TotalMinutes)))
            : gap.TotalHours < 48 ? Tr("{0:N1} hours", gap.TotalHours)
            : Tr("{0:N0} days", gap.TotalDays);
        var km = leg.Kilometres;
        return Tr("From the photo before to the photo after: {0} km, {1} apart", km < 10 ? km.ToString("N1") : km.ToString("N0"), time);
    }

    /// <summary>
    /// Settings › GPS tracks: the tracks (added, renamed and deleted at once, as files) and how photos are matched
    /// to them, read back with <paramref name="chosen"/> when Save is clicked. The map shows them with its Tracks
    /// switch (a "Show on the map" button here jumped to the Map behind an open photo, Lasse 2026-11-06).
    /// </summary>
    private FrameworkElement TracksPage(out Func<(TimeZoneInfo? Zone, int ClockErrorMinutes, int MaxGapMinutes)> chosen)
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

        var onMap = new TextBlock
        {
            Text = Tr("To see them: turn on Tracks with the layers button at the top right of the map. It shows the tracks around the selected photo, or with none selected those between the first and last photo shown."),
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
        // Problems are shown in the dialog itself: a banner would sit behind it, unreadable.
        var problem = new InfoBar { Severity = InfoBarSeverity.Error, IsClosable = true, IsOpen = false };
        void Problem(string text)
        {
            problem.Title = text;
            problem.Message = "";
            problem.IsOpen = true;
        }

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
            var renamed = false;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(name, Tr("Track name"));
            name.LostFocus += (_, _) => Rename();
            name.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) Rename(); };
            void Rename()
            {
                // Enter renames and rebuilds the list, and the text box then loses its focus: only once.
                if (renamed || name.Text.Trim() == track.Name || name.Text.Trim().Length == 0)
                    return;
                renamed = true;
                try
                {
                    ViewModel.TrackFiles.Rename(track.Path, name.Text);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    Problem(Tr("Could not rename the track: {0}", ex.Message));
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
            var delete = IconButton("\uE74D", Tr("Delete the track"), () =>
            {
                try
                {
                    ViewModel.TrackFiles.Delete(track.Path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Problem(Tr("Could not delete the track: {0}", ex.Message));
                }
                Fill();
                return Task.CompletedTask;
            });
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel { Spacing = 2, Children = { name, what } };
            Grid.SetColumn(delete, 1);
            row.Children.Add(text);
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
            problem.IsOpen = false;
            if (failed.Count > 0)
            {
                Problem(Tr("Some tracks could not be added."));
                problem.Message = string.Join("\n", failed);
            }
            Fill();
        };

        Fill();
        chosen = () => (zone.SelectedItem is TimeZoneChoice { Zone: { } picked } ? picked : null,
                        double.IsNaN(clock.Value) ? 0 : (int)Math.Round(clock.Value),
                        double.IsNaN(gap.Value) ? 30 : (int)Math.Round(gap.Value));
        return new ScrollViewer
        {
            MaxHeight = 480,
            Padding = new Thickness(0, 0, 12, 0),
            Visibility = Visibility.Collapsed,
            Content = new StackPanel
            {
                Spacing = 12,
                Children = { hint, how, onMap, add, problem, list, new Border { Height = 1, Background = ThemeBrush("DividerStrokeColorDefaultBrush") }, zone, clock, gap },
            },
        };
    }

    /// <summary>Saves Settings › GPS tracks; a map that is open places the selected photos again with them.</summary>
    private void SaveTrackSettings((TimeZoneInfo? Zone, int ClockErrorMinutes, int MaxGapMinutes) chosen)
    {
        ViewModel.SaveTrackSettings(chosen.Zone, chosen.ClockErrorMinutes, chosen.MaxGapMinutes);
        if (IsMapOpen)
            MapView.ShowSelection(SelectedPhotos, fit: false); // estimates again with the new settings
    }
}
