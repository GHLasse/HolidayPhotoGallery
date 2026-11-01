using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PhotoGallery.App.Imaging;
using PhotoGallery.App.ViewModels;
using Windows.Foundation;
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
        AddKeyAccelerators();
    }

    // The keys also come in as keyboard accelerators. Those reach a visible compare view wherever the focus
    // is; PreviewKeyDown only does while the focus is inside it, and on Lasse's PC the focus kept ending up
    // elsewhere, so the number keys and Tab did nothing. A key PreviewKeyDown already handled is skipped.
    private VirtualKey? _keyHandledByPreview;

    private void AddKeyAccelerators()
    {
        KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        var keys = new List<(VirtualKey Key, VirtualKeyModifiers Modifiers)>
        {
            (VirtualKey.Escape, VirtualKeyModifiers.None), (VirtualKey.G, VirtualKeyModifiers.None),
            (VirtualKey.P, VirtualKeyModifiers.None), (VirtualKey.X, VirtualKeyModifiers.None),
            (VirtualKey.Tab, VirtualKeyModifiers.None), (VirtualKey.Tab, VirtualKeyModifiers.Shift),
            (VirtualKey.Left, VirtualKeyModifiers.None), (VirtualKey.Right, VirtualKeyModifiers.None),
            (VirtualKey.Up, VirtualKeyModifiers.None), (VirtualKey.Down, VirtualKeyModifiers.None),
            (VirtualKey.PageUp, VirtualKeyModifiers.None), (VirtualKey.PageDown, VirtualKeyModifiers.None),
        };
        for (var i = 0; i <= 5; i++)
        {
            keys.Add((VirtualKey.Number0 + i, VirtualKeyModifiers.None));
            keys.Add((VirtualKey.NumberPad0 + i, VirtualKeyModifiers.None));
        }
        foreach (var (key, modifiers) in keys)
        {
            var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
            accelerator.Invoked += async (_, args) =>
            {
                if (Visibility != Visibility.Visible || _panes.Count == 0)
                    return;
                if (_keyHandledByPreview == key)
                {
                    args.Handled = true;
                    return;
                }
                args.Handled = HandlesKey(key);
                if (args.Handled)
                    await HandleKeyAsync(key, modifiers.HasFlag(VirtualKeyModifiers.Shift));
            };
            KeyboardAccelerators.Add(accelerator);
        }
    }

    private bool HandlesKey(VirtualKey key) => key switch
    {
        VirtualKey.Escape or VirtualKey.G or VirtualKey.P or VirtualKey.X or VirtualKey.Tab => true,
        VirtualKey.Left or VirtualKey.Right => true,
        VirtualKey.Up or VirtualKey.Down => _panes.Count == 4,
        VirtualKey.PageUp or VirtualKey.PageDown => _inGroups,
        _ => Stars(key) >= 0,
    };

    private static int Stars(VirtualKey key) => (int)key switch
    {
        var k and >= (int)VirtualKey.Number0 and <= (int)VirtualKey.Number5 => k - (int)VirtualKey.Number0,
        var k and >= (int)VirtualKey.NumberPad0 and <= (int)VirtualKey.NumberPad5 => k - (int)VirtualKey.NumberPad0,
        _ => -1,
    };

    /// <summary>Raised when the user rates a photo; the handler writes it to the file.</summary>
    public event Func<PhotoItemViewModel, int, Task>? RatingChanged;

    /// <summary>Raised when the user presses P to flag or unflag the photo; the handler writes it to the file.</summary>
    public event Func<PhotoItemViewModel, Task>? FlagRequested;

    public event Func<PhotoItemViewModel, Task>? RejectRequested;

    /// <summary>Raised with -1 or +1 when a single photo is shown and the user presses Left or Right.</summary>
    public event Action<int>? NavigateRequested;

    public event Action? CloseRequested;

    /// <summary>A photo was double-clicked, to see it on its own in the viewer.</summary>
    public event Action<PhotoItemViewModel>? OpenRequested;

    public IReadOnlyList<PhotoItemViewModel> Photos => _panes.Select(p => p.Item).ToList();

    /// <summary>The quality hint to show under a photo (C-4), or null for none.</summary>
    public Func<PhotoItemViewModel, string?>? HintFor { get; set; }

    /// <summary>"Ignore" was clicked on a photo's hint: it should never show again for that photo.</summary>
    public event Func<PhotoItemViewModel, Task>? IgnoreHintRequested;

    /// <summary>Raised with -1 or +1 to go to the previous or next group of similar shots (Page Up, Page Down).</summary>
    public event Action<int>? GroupStepRequested;

    /// <summary>The right-click menu for a photo (changes to its stack of similar shots); none when empty.</summary>
    public Func<PhotoItemViewModel, IReadOnlyList<MenuFlyoutItem>>? MenuItemsFor { get; set; }

    private bool _inGroups;

    /// <summary>
    /// Going through groups of similar shots: <paramref name="text"/> ("Similar shots 2 of 9") shows above the
    /// photos with buttons to the previous and next group; null ends it.
    /// </summary>
    public void SetGroup(string? text, bool hasPrevious = false, bool hasNext = false)
    {
        _inGroups = text is not null;
        GroupBar.Visibility = _inGroups ? Visibility.Visible : Visibility.Collapsed;
        GroupText.Text = text ?? "";
        PreviousGroupButton.IsEnabled = hasPrevious;
        NextGroupButton.IsEnabled = hasNext;
    }

    private void PreviousGroup_Click(object sender, RoutedEventArgs e) => GroupStepRequested?.Invoke(-1);

    private void NextGroup_Click(object sender, RoutedEventArgs e) => GroupStepRequested?.Invoke(1);

    /// <summary>Shows the photos, with the one at <paramref name="highlight"/> highlighted (the sharpest of a group).</summary>
    public void Show(IReadOnlyList<PhotoItemViewModel> photos, int highlight = 0)
    {
        _activeIndex = 0;
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
        _activeIndex = Math.Clamp(highlight, 0, Math.Max(0, items.Count - 1));
        SetActive(_activeIndex, FocusState.Programmatic);
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
            // Hidden, not Auto: bars that appear on zooming in shrank the viewport, the photo shrank with it
            // and left a white area in the pane. Hidden bars still scroll.
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollMode = ScrollMode.Auto,
            VerticalScrollMode = ScrollMode.Auto,
            Content = image,
        };
        // At zoom 1 the photo fits the pane exactly; zooming in then shows real detail.
        scroller.SizeChanged += (_, e) =>
        {
            image.Width = e.NewSize.Width;
            image.Height = e.NewSize.Height;
        };
        AddDragToPan(scroller);

        var rating = new RatingControl
        {
            IsClearEnabled = true,
            Value = item.Photo.Rating > 0 ? item.Photo.Rating : -1,
            VerticalAlignment = VerticalAlignment.Center,
        };
        MainWindow.Tint(rating);
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

        // A quality hint (C-4) as a quiet line under the name, never on the photo itself.
        var hintText = new TextBlock
        {
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = ThemeBrush("TextFillColorSecondaryBrush", Colors.Gray),
        };
        var ignore = new HyperlinkButton { Content = Tr("Ignore"), Padding = new Thickness(4, 0, 4, 0), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        ToolTipService.SetToolTip(ignore, Tr("Don’t point this out for this photo again"));
        var hint = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Children = { new FontIcon { Glyph = "\uE7BA", FontSize = 12, Foreground = ThemeBrush("TextFillColorSecondaryBrush", Colors.Gray) }, hintText, ignore },
        };
        var nameAndHint = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { name, hint } };
        var footer = new Grid { ColumnSpacing = 8, Padding = new Thickness(8, 4, 8, 4) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.Children.Add(nameAndHint);
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

        var pane = new Pane(item, host, root, scroller, image, rating, status, flag) { Hint = hint, HintText = hintText };
        ShowHint(pane);
        ignore.Click += async (_, _) =>
        {
            if (IgnoreHintRequested is { } handler)
                await handler(item);
            hint.Visibility = Visibility.Collapsed;
            FocusHighlighted();
        };
        // handledEventsToo: the scroll viewer and rating control mark clicks as handled,
        // which used to stop the click from highlighting the photo.
        // A pane cannot take focus before it is on screen, so the first one grabs it once loaded.
        host.Loaded += (_, _) =>
        {
            if (index == _activeIndex)
                host.Focus(FocusState.Programmatic);
        };
        host.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => SetActive(index, FocusState.Pointer)), handledEventsToo: true);
        host.ContextRequested += (_, e) =>
        {
            if (MenuItemsFor?.Invoke(item) is not { Count: > 0 } items)
                return;
            e.Handled = true;
            var menu = new MenuFlyout();
            foreach (var menuItem in items)
                menu.Items.Add(menuItem);
            if (e.TryGetPosition(host, out var point))
                menu.ShowAt(host, point);
            else
                menu.ShowAt(host);
        };
        host.DoubleTapped += (_, e) =>
        {
            e.Handled = true;
            OpenRequested?.Invoke(item);
        };
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

    // A zoomed photo pans with the mouse held down; the others follow it, as they do when scrolling.
    private void AddDragToPan(ScrollViewer scroller)
    {
        Point? start = null;
        double startH = 0, startV = 0;
        scroller.AddHandler(PointerPressedEvent, new PointerEventHandler((_, e) =>
        {
            var point = e.GetCurrentPoint(scroller);
            if (scroller.ZoomFactor <= 1.01 || !point.Properties.IsLeftButtonPressed)
                return;
            start = point.Position;
            (startH, startV) = (scroller.HorizontalOffset, scroller.VerticalOffset);
            scroller.CapturePointer(e.Pointer);
        }), handledEventsToo: true);
        scroller.AddHandler(PointerMovedEvent, new PointerEventHandler((_, e) =>
        {
            if (start is not { } from)
                return;
            var p = e.GetCurrentPoint(scroller).Position;
            scroller.ChangeView(startH - (p.X - from.X), startV - (p.Y - from.Y), null, disableAnimation: true);
            e.Handled = true;
        }), handledEventsToo: true);
        void End(object sender, PointerRoutedEventArgs e)
        {
            if (start is null)
                return;
            start = null;
            scroller.ReleasePointerCapture(e.Pointer);
        }
        scroller.AddHandler(PointerReleasedEvent, new PointerEventHandler(End), handledEventsToo: true);
        scroller.PointerCaptureLost += (_, _) => start = null;
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
                // A video is compared by the frame Windows shows for it.
                var source = pane.Item.IsVideo
                    ? await ImageLoader.LoadShellThumbnailAsync(pane.Item.Path, Math.Min(maxSize, 1024))
                    : await ImageLoader.DecodeAsync(pane.Item.Path, maxSize);
                if (_panes.Contains(pane))
                    pane.Image.Source = source;
            }
            catch (Exception ex)
            {
                pane.Status.Text = Tr("This photo cannot be shown. {0}", ex.Message);
            }
        }
    }

    /// <summary>Gives the highlighted photo the keyboard focus again, e.g. when coming back from the viewer.</summary>
    public void FocusHighlighted()
    {
        if (_activeIndex < _panes.Count)
            _panes[_activeIndex].Host.Focus(FocusState.Programmatic);
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
        ShowHint(pane);
    }

    /// <summary>Shows each photo's hint again, e.g. once the hints are loaded.</summary>
    public void RefreshHints()
    {
        foreach (var pane in _panes)
            ShowHint(pane);
    }

    // A rated photo has been judged, so its hint goes (a flag or reject leaves it).
    private void ShowHint(Pane pane)
    {
        if (pane.Hint is null || pane.HintText is null)
            return;
        var text = HintFor?.Invoke(pane.Item);
        pane.HintText.Text = text ?? "";
        pane.Hint.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    // PreviewKeyDown runs before the focused control sees the key, so the scroll viewer and
    // rating control can't swallow Tab, the arrows or the number keys.
    private void Root_PreviewKeyDown(object sender, KeyRoutedEventArgs e) => PreviewKey(e);

    /// <summary>
    /// Takes the compare keys before anything else sees them; the window passes on every key while compare is
    /// open, so Tab moves the highlight wherever the focus is instead of moving the focus.
    /// </summary>
    /// <param name="asKey">The key to act on instead, such as Left for A (W, A, S and D work like the arrows).</param>
    public async void PreviewKey(KeyRoutedEventArgs e, VirtualKey? asKey = null)
    {
        var key = asKey ?? e.Key;
        if (Visibility != Visibility.Visible || _panes.Count == 0 || e.Handled || !HandlesKey(key))
            return;
        if (key == VirtualKey.Tab)
            _panes[_activeIndex].Host.Focus(FocusState.Keyboard);
        e.Handled = true;
        _keyHandledByPreview = e.Key;
        DispatcherQueue.TryEnqueue(() => _keyHandledByPreview = null);
        var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        await HandleKeyAsync(key, shift);
    }

    private async Task HandleKeyAsync(VirtualKey key, bool shift)
    {
        switch (key)
        {
            // G goes back to the grid, like Lightroom.
            case VirtualKey.Escape or VirtualKey.G:
                CloseRequested?.Invoke();
                return;
            case VirtualKey.P:
                var flagged = _panes[_activeIndex];
                if (FlagRequested is { } flag)
                    await flag(flagged.Item);
                ShowMark(flagged.Flag, flagged.Item);
                ShowHint(flagged);
                return;
            case VirtualKey.X:
                var rejected = _panes[_activeIndex];
                if (RejectRequested is { } reject)
                    await reject(rejected.Item);
                ShowMark(rejected.Flag, rejected.Item);
                ShowHint(rejected);
                return;
            case VirtualKey.PageUp or VirtualKey.PageDown:
                GroupStepRequested?.Invoke(key == VirtualKey.PageUp ? -1 : 1);
                return;
            case VirtualKey.Tab:
                SetActive((_activeIndex + (shift ? _panes.Count - 1 : 1)) % _panes.Count, FocusState.Keyboard);
                return;
            // With one photo the arrows go to the next or previous one; with more they move the highlight,
            // Up and Down by a row of two.
            case VirtualKey.Left or VirtualKey.Right when _panes.Count == 1:
                NavigateRequested?.Invoke(key == VirtualKey.Left ? -1 : 1);
                return;
            case VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down:
                var step = key switch { VirtualKey.Left => -1, VirtualKey.Right => 1, VirtualKey.Up => -2, _ => 2 };
                SetActive(Math.Clamp(_activeIndex + step, 0, _panes.Count - 1), FocusState.Keyboard);
                return;
        }

        var stars = Stars(key);
        if (stars < 0)
            return;
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
        public StackPanel? Hint { get; init; }
        public TextBlock? HintText { get; init; }
    }
}
