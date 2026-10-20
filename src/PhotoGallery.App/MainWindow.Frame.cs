using System.Numerics;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using PhotoGallery.App.ViewModels;
using Windows.System;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.App;

/// <summary>
/// The window's frame: the app's own title bar, the side panes (the folder pane folds into a strip of
/// icons, the info pane slides in while photos are selected, Tab hides both and Shift+Tab the top bar
/// too, as in Lightroom), the selection bar, and small motion.
/// </summary>
public sealed partial class MainWindow
{
    // Narrower windows fold the folder pane into its icons, so the photos keep their room.
    private const double FoldNavBelowWidth = 1100;
    private const double RailWidth = 52;
    private const double SelectionBarLabelsWidth = 900;

    private static readonly bool s_animations = new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;
    private bool _panesHidden, _chromeHidden;
    private bool _navFolded, _narrow;
    // Set with I; null lets the info pane follow the selection.
    private bool? _infoForced;
    private bool _hadSelection;

    private void SetUpFrame()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppTitleBar.IconSource = new ImageIconSource
        {
            ImageSource = new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon-40.png"))),
        };

        // Selected here rather than in XAML, where it would switch workspaces before the window is built.
        _syncingWorkspace = true;
        ModeBar.SelectedItem = LibraryTab;
        _syncingWorkspace = false;

        NavSplitter.Attach(NavColumn, paneBefore: true, min: 180, max: 520, MainViewModel.DefaultNavPaneWidth, _ => SavePaneWidths());
        InfoSplitter.Attach(InfoColumn, paneBefore: false, min: 260, max: 600, MainViewModel.DefaultInfoPaneWidth, _ => SavePaneWidths());
        MapSplitter.Attach(InfoColumn, paneBefore: false, min: 280, max: 1600, MainViewModel.DefaultMapPaneWidth, ViewModel.SaveMapPaneWidth);
        _navFolded = ViewModel.NavPaneFolded;
        ApplyChrome(animate: false);

        Root.SizeChanged += (_, e) => OnWindowWidthChanged(e.NewSize.Width);
        Root.PreviewKeyDown += Root_PreviewKeyDown;

        // The classic Windows Live Photo Gallery shortcuts: Ctrl+, and Ctrl+. (no XAML name for these keys).
        // Hidden: WinUI would otherwise make a tooltip naming the keys, and it has no name for , and . (it crashed).
        Root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        AddAccelerator((VirtualKey)188, VirtualKeyModifiers.Control, () => RotateLeft_Click(this, new RoutedEventArgs()));
        AddAccelerator((VirtualKey)190, VirtualKeyModifiers.Control, () => RotateRight_Click(this, new RoutedEventArgs()));
        SetUpFilterFolding();
        SetUpViewerMotion();
    }

    private void AddAccelerator(VirtualKey key, VirtualKeyModifiers modifiers, Action action)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += (_, args) =>
        {
            if (IsViewerOpen || CompareLayer.Visibility == Visibility.Visible)
                return;
            args.Handled = true;
            action();
        };
        Root.KeyboardAccelerators.Add(accelerator);
    }

    private void OnWindowWidthChanged(double width)
    {
        SelectionCommands.DefaultLabelPosition = width >= SelectionBarLabelsWidth
            ? CommandBarDefaultLabelPosition.Right
            : CommandBarDefaultLabelPosition.Collapsed;
        App.Trace($"window: {width:0} px wide");
        var narrow = width < FoldNavBelowWidth;
        if (narrow == _narrow)
            return;
        _narrow = narrow;
        // Folded while narrow; widening brings back what the user chose last.
        _navFolded = narrow || ViewModel.NavPaneFolded;
        ApplyChrome();
    }

    // A folded pane's width is the strip's, not the one to keep.
    private void SavePaneWidths() => ViewModel.SavePaneWidths(
        _navFolded || NavPane.Visibility != Visibility.Visible ? ViewModel.NavPaneWidth : NavColumn.Width.Value,
        InfoPane.Visibility != Visibility.Visible ? ViewModel.InfoPaneWidth : InfoColumn.Width.Value);

    // The map takes the info pane's place: there is room for one or the other (I still shows it).
    // The details only belong to the photo grid: not beside the People or Albums pages.
    private bool InfoShown => _workspace is Workspace.Library or Workspace.Map &&
                              (_infoForced ?? (PhotoGrid.SelectedItems.Count > 0 && !IsMapShown));

    private bool MapBeside => _workspace == Workspace.Library && MapToggle.IsChecked == true;

    private bool IsMapShown => _workspace == Workspace.Map || (_workspace == Workspace.Library && MapToggle.IsChecked == true);

    /// <summary>
    /// Shows, folds or hides the side panes and the top bar. A pane that goes away gives its width to the
    /// photos, so nothing ever lies over them; the widths slide rather than jump.
    /// </summary>
    private void ApplyChrome(bool animate = true)
    {
        var panes = !_panesHidden && !_chromeHidden;
        var nav = panes && !_navFolded;
        var rail = panes && _navFolded;
        var info = panes && InfoShown;
        App.Trace($"chrome: nav {nav}, rail {rail}, info {info}, animate {animate}");

        NavPane.Visibility = NavSplitter.Visibility = nav ? Visibility.Visible : Visibility.Collapsed;
        NavRail.Visibility = rail ? Visibility.Visible : Visibility.Collapsed;
        AnimateColumn(NavColumn, nav ? ViewModel.NavPaneWidth : rail ? RailWidth : 0, animate);

        // The map beside the grid and the info pane share the right-hand column: one or the other.
        var map = MapBeside && !_chromeHidden;
        var full = _workspace == Workspace.Map;
        info &= !map;
        InfoSplitter.Visibility = info ? Visibility.Visible : Visibility.Collapsed;
        MapSplitter.Visibility = map ? Visibility.Visible : Visibility.Collapsed;
        if (info)
            InfoPane.Visibility = Visibility.Visible;
        else if (map)
            InfoPane.Visibility = Visibility.Collapsed;
        MapView.Visibility = map || full ? Visibility.Visible : Visibility.Collapsed;
        var width = map ? Math.Min(ViewModel.MapPaneWidth, Math.Max(280, (PhotoArea.ActualWidth + InfoColumn.ActualWidth) * 0.7))
                  : info ? ViewModel.InfoPaneWidth : 0;
        AnimateColumn(InfoColumn, width, animate, done: () =>
        {
            // Collapsed only once it has slid out, so its content doesn't vanish mid-slide.
            if (!(!_panesHidden && !_chromeHidden && InfoShown && !MapBeside))
                InfoPane.Visibility = Visibility.Collapsed;
        });
        TopBar.Visibility = _chromeHidden || _workspace == Workspace.People ? Visibility.Collapsed : Visibility.Visible;
    }

    // Tab and Shift+Tab hide the panes, like Lightroom, wherever the focus is; only a text box keeps Tab.
    private void Root_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Tab || IsViewerOpen || CompareLayer.Visibility == Visibility.Visible ||
            IsDown(VirtualKey.Control) || IsDown(VirtualKey.Menu) ||
            FocusManager.GetFocusedElement(Root.XamlRoot) is TextBox or PasswordBox or RichEditBox or AutoSuggestBox)
        {
            return;
        }
        e.Handled = true;
        if (IsDown(VirtualKey.Shift))
            _chromeHidden = !_chromeHidden;
        else if (_chromeHidden)
            _chromeHidden = _panesHidden = false;
        else
            _panesHidden = !_panesHidden;
        ApplyChrome();
    }

    private void SearchAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        SearchBox.Focus(FocusState.Keyboard);
    }

    // ---- Folder pane and its strip of icons ----

    private void ToggleNavPane_Click(object sender, RoutedEventArgs e) => SetNavFolded(!_navFolded);

    private void SetNavFolded(bool folded)
    {
        _navFolded = folded;
        _panesHidden = _chromeHidden = false;
        if (!_narrow)
            ViewModel.SaveNavPaneFolded(folded);
        ApplyChrome();
    }

    // Each icon opens the pane at its part.
    private void RailFolders_Click(object sender, RoutedEventArgs e)
    {
        SetNavFolded(false);
        OpenNavSection(FoldersHeading, true);
        FolderTree.Focus(FocusState.Programmatic);
    }

    private void RailDates_Click(object sender, RoutedEventArgs e)
    {
        SetNavFolded(false);
        OpenNavSection(DatesHeading, true);
    }

    private void RailPlaces_Click(object sender, RoutedEventArgs e)
    {
        SetNavFolded(false);
        OpenNavSection(PlacesHeading, true);
    }

    // A heading opens or closes its section; Folders starts open, Dates and Places closed.
    private void NavSection_Click(object sender, RoutedEventArgs e)
    {
        var (content, _) = NavSection(sender);
        OpenNavSection(sender, content.Visibility != Visibility.Visible);
    }

    private void OpenNavSection(object heading, bool open)
    {
        var (content, chevron) = NavSection(heading);
        content.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        chevron.Glyph = open ? "\uE70E" : "\uE70D";
        if (heading == FoldersHeading)
            FoldersRow.Height = open ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
    }

    private (FrameworkElement Content, FontIcon Chevron) NavSection(object heading) =>
        heading == FoldersHeading ? (FoldersContent, FoldersChevron)
        : heading == DatesHeading ? (DateTree, DatesChevron)
        : (PlaceTree, PlacesChevron);

    // ---- Selection: the info pane and the selection bar follow it ----

    private void OnSelectionChromeChanged()
    {
        var count = PhotoGrid.SelectedItems.Count;
        App.Trace($"selection: {count}");
        var has = count > 0;
        if (has != _hadSelection)
        {
            _hadSelection = has;
            _infoForced = null; // I only holds until the selection comes or goes
            ApplyChrome();
        }
        SelectionCountText.Text = Tr("{0} selected", count);
        ShowSelectionBar(has);
        // Room under the last row, so the bar never covers a photo that is scrolled to the end.
        ApplyGridPadding(has ? 80 : 8);
    }

    private bool _selectionBarShown, _selectionBarAllowed = true;
    private int _selectionBarVersion;

    // Rises from the bottom while photos are selected, and sinks away when none are.
    private void ShowSelectionBar(bool show)
    {
        show &= _selectionBarAllowed;
        if (show == _selectionBarShown)
            return;
        _selectionBarShown = show;
        var version = ++_selectionBarVersion;
        if (!s_animations)
        {
            SelectionBar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            return;
        }
        SelectionBar.Visibility = Visibility.Visible;
        var storyboard = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        AnimateDouble(storyboard, SelectionBar, "Opacity", show ? 0 : 1, show ? 1 : 0, show ? SlideMs : 167);
        AnimateDouble(storyboard, SelectionBarShift, "TranslateY", show ? 24 : 0, show ? 0 : 24, show ? SlideMs : 167);
        storyboard.Completed += (_, _) =>
        {
            if (version == _selectionBarVersion && !_selectionBarShown)
                SelectionBar.Visibility = Visibility.Collapsed;
        };
        storyboard.Begin();
    }

    // ---- Quick actions on a tile ----

    private async void TileStar_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PhotoItemViewModel item, Tag: string tag } || !int.TryParse(tag, out var stars))
            return;
        // The same star again clears the rating.
        await ViewModel.SetRatingAsync(item, item.Rating == stars ? 0 : stars);
        if (!ViewModel.Editor.IsDirty)
            ViewModel.Editor.Load(SelectedPhotos);
    }

    private async void TileFlag_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PhotoItemViewModel item)
            await ViewModel.ToggleFlagAsync([item]);
    }

    // A click on the one selected photo deselects it, so the info pane can go away again (Esc does too).
    private PhotoItemViewModel? _pressedSelected;

    private void SetUpClickToDeselect()
    {
        // handledEventsToo: the grid's items take these events for their own selection handling.
        PhotoGrid.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) =>
        {
            _pressedSelected = null;
            var point = e.GetCurrentPoint(PhotoGrid);
            if (!point.Properties.IsLeftButtonPressed || e.KeyModifiers != VirtualKeyModifiers.None)
                return;
            if (ClickedPhoto(e.OriginalSource) is { } item && PhotoGrid.SelectedItems.Count == 1 && ReferenceEquals(PhotoGrid.SelectedItems[0], item))
                _pressedSelected = item;
        }), true);
        PhotoGrid.AddHandler(UIElement.TappedEvent, new TappedEventHandler((_, e) =>
        {
            var item = _pressedSelected;
            _pressedSelected = null;
            if (item is not null && ReferenceEquals(ClickedPhoto(e.OriginalSource), item) &&
                PhotoGrid.SelectedItems.Count == 1 && ReferenceEquals(PhotoGrid.SelectedItems[0], item))
            {
                App.Trace("selection: click on the selected photo clears it");
                PhotoGrid.SelectedItems.Clear();
            }
        }), true);
    }

    // The photo whose tile was clicked, unless the click was on one of the tile's own buttons (stars, flag, tick).
    private PhotoItemViewModel? ClickedPhoto(object source)
    {
        for (var element = source as DependencyObject; element is not null && element != PhotoGrid;
             element = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(element))
        {
            if (element is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase)
                return null;
            if (element is GridViewItem { Content: PhotoItemViewModel item })
                return item;
        }
        return null;
    }

    // Adds the photo to the selection without Ctrl, or takes it out.
    private void TileCheck_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PhotoItemViewModel item)
            return;
        if (PhotoGrid.SelectedItems.Contains(item))
            PhotoGrid.SelectedItems.Remove(item);
        else
            PhotoGrid.SelectedItems.Add(item);
    }

    private void ToggleInfoPane()
    {
        _infoForced = !InfoShown;
        _panesHidden = _chromeHidden = false;
        ApplyChrome();
    }

    // ---- Motion ----

    // A tile rises a little under the pointer (Fluent: 167 ms, decelerating) and shows its quick actions.
    private void Tile_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        Lift((FrameworkElement)sender, up: true);
        ShowQuickActions(sender, true);
    }

    private void Tile_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        Lift((FrameworkElement)sender, up: false);
        ShowQuickActions(sender, false);
    }

    private static void ShowQuickActions(object tile, bool show)
    {
        if ((tile as FrameworkElement)?.FindName("QuickActions") is UIElement actions)
        {
            actions.Opacity = show ? 1 : 0;
            actions.IsHitTestVisible = show;
        }
    }

    private static void Lift(FrameworkElement element, bool up)
    {
        if (!s_animations)
            return;
        element.ScaleTransition ??= new Vector3Transition { Duration = TimeSpan.FromMilliseconds(167) };
        element.TranslationTransition ??= new Vector3Transition { Duration = TimeSpan.FromMilliseconds(167) };
        element.CenterPoint = new Vector3((float)element.ActualWidth / 2, (float)element.ActualHeight / 2, 0);
        element.Scale = up ? new Vector3(1.03f, 1.03f, 1) : Vector3.One;
        element.Translation = up ? new Vector3(0, -2, 0) : Vector3.Zero;
    }
}
