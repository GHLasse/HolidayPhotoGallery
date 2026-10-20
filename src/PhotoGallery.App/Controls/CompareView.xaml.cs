using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PhotoGallery.App.Imaging;
using PhotoGallery.App.ViewModels;
using Windows.System;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.App.Controls;

/// <summary>
/// Shows one to four photos side by side, as large as possible, to pick the best ones.
/// Zoom and pan are shared between the photos; keys 1-5 rate the highlighted photo, 0 clears it.
/// </summary>
public sealed partial class CompareView : UserControl
{
    public const int MaxPhotos = 4;

    private readonly List<Pane> _panes = [];
    private int _activeIndex;
    private Pane? _driver;

    public CompareView()
    {
        InitializeComponent();
    }

    /// <summary>Raised when the user rates a photo; the handler writes it to the file.</summary>
    public event Func<PhotoItemViewModel, int, Task>? RatingChanged;

    /// <summary>Raised when the user presses P to flag or unflag the photo; the handler writes it to the file.</summary>
    public event Func<PhotoItemViewModel, Task>? FlagRequested;

    public event Func<PhotoItemViewModel, Task>? RejectRequested;

    /// <summary>Raised with -1 or +1 when a single photo is shown and the user presses Left or Right.</summary>
    public event Action<int>? NavigateRequested;

    public event Action? CloseRequested;

    public IReadOnlyList<PhotoItemViewModel> Photos => _panes.Select(p => p.Item).ToList();

    public void Show(IReadOnlyList<PhotoItemViewModel> photos)
    {
        PaneGrid.Children.Clear();
        PaneGrid.RowDefinitions.Clear();
        PaneGrid.ColumnDefinitions.Clear();
        _panes.Clear();

        var items = photos.Take(MaxPhotos).ToList();
        // One row for up to three photos, a 2 x 2 grid for four.
        var columns = items.Count == 4 ? 2 : Math.Max(1, items.Count);
        var rows = items.Count == 4 ? 2 : 1;
        for (var c = 0; c < columns; c++)
            PaneGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var r = 0; r < rows; r++)
            PaneGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        for (var i = 0; i < items.Count; i++)
        {
            var pane = CreatePane(items[i], i);
            Grid.SetColumn(pane.Host, i % columns);
            Grid.SetRow(pane.Host, i / columns);
            PaneGrid.Children.Add(pane.Host);
            _panes.Add(pane);
        }

        HintText.Text = items.Count == 1
            ? Tr("Keys 1–5 rate this photo, 0 clears the rating. Left and Right go to the previous or next photo. Ctrl + mouse wheel zooms.")
            : Tr("Click a photo to highlight it. Keys 1–5 rate it, 0 clears the rating, Tab moves to the next photo. Ctrl + mouse wheel zooms.");
        SetActive(0, FocusState.Programmatic);
        _ = LoadImagesAsync();
    }

    private Pane CreatePane(PhotoItemViewModel item, int index)
    {
        var image = new Image { Stretch = Stretch.Uniform };
        var scroller = new ScrollViewer
        {
            ZoomMode = ZoomMode.Enabled,
            MinZoomFactor = 1,
            MaxZoomFactor = 8,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Auto,
            VerticalScrollMode = ScrollMode.Auto,
            Content = image,
        };
        // At zoom 1 the photo fits the pane exactly; zooming in then shows real detail.
        scroller.SizeChanged += (_, _) =>
        {
            image.Width = scroller.ViewportWidth;
            image.Height = scroller.ViewportHeight;
        };

        var rating = new RatingControl
        {
            IsClearEnabled = true,
            Value = item.Photo.Rating > 0 ? item.Photo.Rating : -1,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var name = new TextBlock
        {
            Text = item.FileName,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var status = new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 360,
        };
        var flag = new FontIcon { FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
        ShowMark(flag, item);
        var footer = new Grid { ColumnSpacing = 8, Padding = new Thickness(8, 4, 8, 4) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.Children.Add(name);
        Grid.SetColumn(flag, 1);
        footer.Children.Add(flag);
        Grid.SetColumn(rating, 2);
        footer.Children.Add(rating);

        var body = new Grid();
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.Children.Add(scroller);
        body.Children.Add(status);
        Grid.SetRow(footer, 1);
        body.Children.Add(footer);

        var root = new Border
        {
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(6),
            Background = ThemeBrush("CardBackgroundFillColorDefaultBrush", Colors.Transparent),
            Child = body,
        };

        // The pane itself can take keyboard focus, so the rating keys keep working after a click.
        // Without this, clicking a photo moved focus out of the compare view and the keys went nowhere.
        var host = new ContentControl
        {
            Content = root,
            IsTabStop = true,
            UseSystemFocusVisuals = false,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
        };

        var pane = new Pane(item, host, root, scroller, image, rating, status, flag);
        // handledEventsToo: the scroll viewer and rating control mark clicks as handled,
        // which used to stop the click from highlighting the photo.
        // A pane cannot take focus before it is on screen, so the first one grabs it once loaded.
        host.Loaded += (_, _) =>
        {
            if (index == _activeIndex)
                host.Focus(FocusState.Programmatic);
        };
        host.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => SetActive(index, FocusState.Pointer)), handledEventsToo: true);
        // Only the photo under the mouse leads; the others follow it. Without this, the
        // programmatic scrolling of the followers would echo back and fight the leader.
        scroller.PointerEntered += (_, _) => _driver = pane;
        scroller.ViewChanged += (_, _) => SyncFrom(pane);
        rating.ValueChanged += async (_, _) =>
        {
            if (!pane.SettingRating)
                await RaiseRatingAsync(pane, rating.Value < 1 ? 0 : (int)Math.Round(rating.Value));
        };
        return pane;
    }

    private async Task LoadImagesAsync()
    {
        var scale = XamlRoot?.RasterizationScale ?? 1.0;
        foreach (var pane in _panes.ToList())
        {
            // Decode at twice the pane size (capped) so zooming in stays sharp.
            var longEdge = Math.Max(ActualWidth / Math.Max(1, PaneGrid.ColumnDefinitions.Count), ActualHeight / Math.Max(1, PaneGrid.RowDefinitions.Count));
            var maxSize = (uint)Math.Clamp(longEdge * scale * 2, 1200, 4096);
            try
            {
                var source = await ImageLoader.DecodeAsync(pane.Item.Path, maxSize);
                if (_panes.Contains(pane))
                    pane.Image.Source = source;
            }
            catch (Exception ex)
            {
                pane.Status.Text = Tr("This photo cannot be shown. {0}", ex.Message);
            }
        }
    }

    private void SetActive(int index, FocusState focus)
    {
        if (_panes.Count == 0)
            return;
        _activeIndex = Math.Clamp(index, 0, _panes.Count - 1);
        _panes[_activeIndex].Host.Focus(focus);
        var accent = ThemeBrush("AccentFillColorDefaultBrush", Colors.DodgerBlue);
        for (var i = 0; i < _panes.Count; i++)
        {
            // With one photo there is nothing to choose between, so no highlight.
            _panes[i].Root.BorderBrush = i == _activeIndex && _panes.Count > 1 ? accent : new SolidColorBrush(Colors.Transparent);
        }
    }

    private void SyncFrom(Pane source)
    {
        if (source != _driver || SyncToggle.IsChecked != true)
            return;

        var sv = source.Scroller;
        var zoom = sv.ZoomFactor;
        // Positions are matched as fractions of the scrollable range, so photos of slightly
        // different sizes still show the same part of the scene.
        var relX = sv.ScrollableWidth > 0 ? sv.HorizontalOffset / sv.ScrollableWidth : 0;
        var relY = sv.ScrollableHeight > 0 ? sv.VerticalOffset / sv.ScrollableHeight : 0;
        foreach (var pane in _panes.Where(p => p != source))
        {
            var target = pane.Scroller;
            var unzoomedWidth = target.ExtentWidth / Math.Max(target.ZoomFactor, 0.01f);
            var unzoomedHeight = target.ExtentHeight / Math.Max(target.ZoomFactor, 0.01f);
            var scrollableWidth = Math.Max(0, unzoomedWidth * zoom - target.ViewportWidth);
            var scrollableHeight = Math.Max(0, unzoomedHeight * zoom - target.ViewportHeight);
            target.ChangeView(relX * scrollableWidth, relY * scrollableHeight, zoom, disableAnimation: true);
        }
    }

    private static Brush ThemeBrush(string key, Windows.UI.Color fallback) =>
        Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush ? brush : new SolidColorBrush(fallback);

    private async Task RaiseRatingAsync(Pane pane, int stars)
    {
        if (RatingChanged is { } handler)
            await handler(pane.Item, stars);
    }

    // PreviewKeyDown runs before the focused control sees the key, so the scroll viewer and
    // rating control can't swallow Tab, the arrows or the number keys.
    private async void Root_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_panes.Count == 0)
            return;

        switch (e.Key)
        {
            // G goes back to the grid, like Lightroom.
            case VirtualKey.Escape or VirtualKey.G:
                e.Handled = true;
                CloseRequested?.Invoke();
                return;
            case VirtualKey.P:
                e.Handled = true;
                var flagged = _panes[_activeIndex];
                if (FlagRequested is { } flag)
                    await flag(flagged.Item);
                ShowMark(flagged.Flag, flagged.Item);
                return;
            case VirtualKey.X:
                e.Handled = true;
                var rejected = _panes[_activeIndex];
                if (RejectRequested is { } reject)
                    await reject(rejected.Item);
                ShowMark(rejected.Flag, rejected.Item);
                return;
            case VirtualKey.Tab:
                e.Handled = true;
                var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
                    .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
                SetActive((_activeIndex + (shift ? _panes.Count - 1 : 1)) % _panes.Count, FocusState.Keyboard);
                return;
            case VirtualKey.Left or VirtualKey.Right when _panes.Count == 1:
                e.Handled = true;
                NavigateRequested?.Invoke(e.Key == VirtualKey.Left ? -1 : 1);
                return;
        }

        var key = (int)e.Key;
        var stars = key switch
        {
            >= (int)VirtualKey.Number0 and <= (int)VirtualKey.Number5 => key - (int)VirtualKey.Number0,
            >= (int)VirtualKey.NumberPad0 and <= (int)VirtualKey.NumberPad5 => key - (int)VirtualKey.NumberPad0,
            _ => -1,
        };
        if (stars < 0)
            return;

        e.Handled = true;
        var pane = _panes[_activeIndex];
        pane.SettingRating = true;
        pane.Rating.Value = stars == 0 ? -1 : stars;
        pane.SettingRating = false;
        await RaiseRatingAsync(pane, stars);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();

    // The flag (P) or the reject mark (X) next to the file name; a photo has one or neither.
    private static void ShowMark(FontIcon mark, PhotoItemViewModel item)
    {
        mark.Visibility = item.Photo.Flagged || item.Photo.Rejected ? Visibility.Visible : Visibility.Collapsed;
        mark.Glyph = item.Photo.Rejected ? "\uE711" : "\uE7C1";
        mark.Foreground = item.Photo.Rejected
            ? ThemeBrush("SystemFillColorCriticalBrush", Colors.IndianRed)
            : ThemeBrush("AccentTextFillColorPrimaryBrush", Colors.DodgerBlue);
        ToolTipService.SetToolTip(mark, item.Photo.Rejected ? Tr("Rejected (X)") : Tr("Flagged (P)"));
    }

    private sealed class Pane(PhotoItemViewModel item, ContentControl host, Border root, ScrollViewer scroller, Image image, RatingControl rating, TextBlock status, FontIcon flag)
    {
        public FontIcon Flag { get; } = flag;
        public PhotoItemViewModel Item { get; } = item;
        public ContentControl Host { get; } = host;
        public Border Root { get; } = root;
        public ScrollViewer Scroller { get; } = scroller;
        public Image Image { get; } = image;
        public RatingControl Rating { get; } = rating;
        public TextBlock Status { get; } = status;
        public bool SettingRating { get; set; }
    }
}
