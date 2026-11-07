using System.Numerics;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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
    private const double SelectionBarLabelsWidth = 1150;

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

        AppTitleBar.Loaded += (_, _) => StyleTitleBar();
        foreach (var tab in ModeBar.Items)
            tab.SizeChanged += (_, _) => WidenTabMark(tab);
        ModeBar.SelectionChanged += (_, _) => RecolourTabMarks();
        foreach (var tree in new[] { FolderTree, DateTree, PlaceTree })
            tree.SelectionChanged += (_, _) => DispatcherQueue.TryEnqueue(() => RecolourTreeMark(tree));

        // Selected here rather than in XAML, where it would switch workspaces before the window is built.
        _syncingWorkspace = true;
        ModeBar.SelectedItem = LibraryTab;
        _syncingWorkspace = false;

        NavSplitter.Attach(NavColumn, paneBefore: true, min: 180, max: 520, MainViewModel.DefaultNavPaneWidth, _ => SavePaneWidths());
        // Details, map and Edit share one width, so switching between them doesn't jump (Lasse, 2026-11-06).
        InfoSplitter.Attach(InfoColumn, paneBefore: false, min: 260, max: 900, MainViewModel.DefaultInfoPaneWidth, _ => SavePaneWidths());
        MapSplitter.Attach(InfoColumn, paneBefore: false, min: 260, max: 900, MainViewModel.DefaultInfoPaneWidth, _ => SavePaneWidths());
        _navFolded = ViewModel.NavPaneFolded;
        ApplyChrome(animate: false);

        Root.SizeChanged += (_, e) => OnWindowWidthChanged(e.NewSize.Width);
        NavPane.SizeChanged += (_, _) => LimitNavTrees();
        WatchNavTrees();
        Root.PreviewKeyDown += Root_PreviewKeyDown;

        // The classic Windows Live Photo Gallery shortcuts: Ctrl+, and Ctrl+. (no XAML name for these keys).
        // Hidden: WinUI would otherwise make a tooltip naming the keys, and it has no name for , and . (it crashed).
        Root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        AddAccelerator((VirtualKey)188, VirtualKeyModifiers.Control, () => RotateLeft_Click(this, new RoutedEventArgs()));
        AddAccelerator((VirtualKey)190, VirtualKeyModifiers.Control, () => RotateRight_Click(this, new RoutedEventArgs()));
        AddTabAccelerators();
        SetUpFilterFolding();
        SetUpViewerMotion();
        SetUpEditPane();
        WatchForCards();
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
        RightSide() is RightPane.Info or RightPane.Map or RightPane.Edit ? InfoColumn.Width.Value : ViewModel.InfoPaneWidth);

    /// <summary>What the right-hand column shows: nothing, the strip of icons, the details, the map or the Edit pane.</summary>
    private enum RightPane { None, Rail, Info, Map, Edit }

    // The details and the map share the right-hand column, and fold to a strip of icons like the left pane.
    // The details come with a selection and go with it (the strip stays); I shows them anyway until it changes.
    // They only belong to the photos: not beside the People or Albums pages, nor over the whole-library map
    // unless I asks for them. The viewer has a choice of its own (MainWindow.Viewer.cs).
    private RightPane RightSide()
    {
        if (IsViewerOpen)
            return _viewerFolded ? RightPane.Rail : ShownPane(_viewerPane) switch
            {
                SidePane.Map => RightPane.Map,
                SidePane.Edit => RightPane.Edit,
                _ => RightPane.Info,
            };
        if (_panesHidden || _chromeHidden || _workspace is not (Workspace.Library or Workspace.Map))
            return RightPane.None;
        if (_workspace == Workspace.Map)
            return _infoForced == true && PaneUsable(SidePane.Info) ? RightPane.Info : RightPane.None;
        if (_sideFolded)
            return RightPane.Rail;
        if (_sidePane == SidePane.Map)
            return RightPane.Map;
        // With nothing selected there are no details to show (Lasse 2026-11-07), so the pane folds to its strip.
        return PaneUsable(SidePane.Info) ? RightPane.Info : RightPane.Rail;
    }

    /// <summary>
    /// Whether the pane can show anything useful now (Lasse 2026-11-07: only offer what can be chosen). Beside the
    /// grid the details and Edit need a selected photo; beside the viewer there is always a photo, but a video
    /// can't be edited, and on the Map page the map is already there.
    /// </summary>
    private bool PaneUsable(SidePane pane) => pane switch
    {
        SidePane.Info => IsViewerOpen || PhotoGrid.SelectedItems.Count > 0,
        SidePane.Map => !(IsViewerOpen && _workspace == Workspace.Map),
        _ => IsViewerOpen ? !_viewerShowsVideo : PhotoGrid.SelectedItems.OfType<PhotoItemViewModel>().FirstOrDefault() is { IsVideo: false },
    };

    // The pane the viewer shows: its choice, or the details while that choice can't be used (a video while editing,
    // the map on the Map page). The choice is kept, so the next photo opens in Edit again.
    private SidePane ShownPane(SidePane chosen) => PaneUsable(chosen) ? chosen : SidePane.Info;

    private bool MapBeside => _workspace == Workspace.Library && _sidePane == SidePane.Map && !_sideFolded;

    private bool IsMapShown => _workspace == Workspace.Map || MapBeside;

    /// <summary>
    /// Shows, folds or hides the side panes and the top bar. A pane that goes away gives its width to the
    /// photos, so nothing ever lies over them; the widths slide rather than jump.
    /// </summary>
    private void ApplyChrome(bool animate = true)
    {
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, PlaceNotices);
        var panes = !_panesHidden && !_chromeHidden;
        var nav = panes && !_navFolded;
        var rail = panes && _navFolded;
        var side = RightSide();
        App.Trace($"chrome: nav {nav}, rail {rail}, right {side}, animate {animate}");

        // A pane going away keeps its content until it has slid out.
        void ShowNav()
        {
            NavPane.Visibility = NavSplitter.Visibility = nav ? Visibility.Visible : Visibility.Collapsed;
            NavRail.Visibility = rail ? Visibility.Visible : Visibility.Collapsed;
            SettingsButton.Visibility = nav || rail ? Visibility.Visible : Visibility.Collapsed;
            SettingsLabel.Visibility = nav ? Visibility.Visible : Visibility.Collapsed;
            SettingsButton.HorizontalAlignment = nav ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
            SettingsButton.Width = nav ? double.NaN : 36;
        }
        var navGoes = !nav && !rail;
        // Folding, the pane slides out first and the strip comes after it (AnimateColumn).
        var navFolds = rail && NavPane.Visibility == Visibility.Visible && animate && s_animations;
        if (navFolds)
            NavRail.Visibility = Visibility.Visible;
        else if (!navGoes)
            ShowNav();
        NavSplitter.Visibility = nav ? Visibility.Visible : Visibility.Collapsed;
        AnimateColumn(NavColumn, nav ? ViewModel.NavPaneWidth : rail ? RailWidth : 0, animate, fromLeft: true,
                      done: navGoes || navFolds ? () => { if (_panesHidden || _chromeHidden || _navFolded) ShowNav(); } : null,
                      incoming: nav ? [NavPane, SettingsButton] : rail ? [NavRail, SettingsButton] : [],
                      content: [NavPane, NavRail, SettingsButton]);

        // The map beside the photos and the details share the right-hand column: one or the other, or the strip.
        var viewer = IsViewerOpen;
        var full = _workspace == Workspace.Map;
        var info = side == RightPane.Info;
        var map = side == RightPane.Map;
        var sideRail = side == RightPane.Rail;
        var edit = side == RightPane.Edit;
        PlaceInfoBesideViewer(viewer && side != RightPane.None);
        InfoSplitter.Visibility = info || edit ? Visibility.Visible : Visibility.Collapsed;
        MapSplitter.Visibility = map ? Visibility.Visible : Visibility.Collapsed;
        var width = map || info || edit ? Math.Min(ViewModel.InfoPaneWidth, Math.Max(260, (PhotoArea.ActualWidth + InfoColumn.ActualWidth) * 0.7))
                  : sideRail ? RailWidth : 0;
        // Folding or closing, the pane and its switch slide out first and go once they have (AnimateColumn's done).
        var rightWas = InfoColumn.Width.IsAbsolute ? InfoColumn.Width.Value : InfoColumn.ActualWidth;
        var rightShrinks = animate && s_animations && width < rightWas - 1;
        if (info)
            InfoPane.Visibility = Visibility.Visible;
        else if (map || edit || (sideRail && !rightShrinks))
            InfoPane.Visibility = Visibility.Collapsed;
        if (edit)
            EditPane.Visibility = Visibility.Visible;
        else if (info || map || (sideRail && !rightShrinks))
            EditPane.Visibility = Visibility.Collapsed;
        SideRail.Visibility = sideRail ? Visibility.Visible : Visibility.Collapsed;
        // The switch sits above the pane; over the whole-library map there is nothing to switch to.
        var header = (info || map || edit) && (viewer || !full);
        // The switch goes with the pane.
        if (header || !rightShrinks)
        {
            SidePaneHeader.Visibility = header ? Visibility.Visible : Visibility.Collapsed;
            InfoPane.Margin = new Thickness(0, header ? SidePaneHeader.Height : 0, 0, 0);
            EditPane.Margin = InfoPane.Margin;
        }
        // Beside the grid or the photo the map fills the pane right up to its edges (Lasse 2026-11-07): under the
        // splitter on the left and under the switch at the top, which floats over it on cards like the map's own controls.
        // (The Map page's map reaches the window's edges too.)
        // The map beside the photos slides in and out with its pane, like the details do.
        var mapBesideMoves = !full && !_mapOnPage && !_mapShrinking && animate && s_animations;
        var mapGoes = mapBesideMoves && rightShrinks && !map && MapView.Visibility == Visibility.Visible;
        var mapComes = mapBesideMoves && map && MapView.Visibility != Visibility.Visible && width > rightWas + 1;
        if (!full && !_mapOnPage && !mapGoes)
        {
            Grid.SetColumn(MapView, map ? 3 : 4);
            Grid.SetColumnSpan(MapView, map ? 2 : 1);
        }
        MapView.TopInset = map && header ? SidePaneHeader.Height : 0;
        var overMap = map && header;
        SideSwitchCard.Visibility = overMap ? Visibility.Visible : Visibility.Collapsed;
        FoldCard.Visibility = SideSwitchCard.Visibility;
        FoldSideButton.CornerRadius = new CornerRadius(overMap ? 18 : 4);
        // Under the viewer the whole-library map would lie over the pane beside it, so it waits till the viewer closes.
        if (_mapBesideSlid && !mapGoes)
        {
            // A slide that was turned round: back in place.
            PutMapBesideBack();
        }
        if (mapGoes)
            SlideMapBeside((float)MapView.ActualWidth);
        else
            MapView.Visibility = map || (full && !viewer) ? Visibility.Visible : Visibility.Collapsed;
        if (mapComes)
            SlideMapBeside(0, from: (float)width);
        SyncSideSwitch();
        AnimateColumn(InfoColumn, width, animate, lifted: mapGoes ? () => LiftOut(MapView, left: false) : null, done: () =>
        {
            // Collapsed only once it has slid out, so its content doesn't vanish mid-slide.
            var now = RightSide();
            if (now != RightPane.Info)
                InfoPane.Visibility = Visibility.Collapsed;
            if (now != RightPane.Edit)
                EditPane.Visibility = Visibility.Collapsed;
            if (now is RightPane.Rail or RightPane.None)
                SidePaneHeader.Visibility = Visibility.Collapsed;
            if (mapGoes && now != RightPane.Map)
                PutMapBesideAway();
        },
        incoming: info ? [InfoPane, SidePaneHeader] : edit ? [EditPane, SidePaneHeader] : map ? [SidePaneHeader] : sideRail ? [SideRail] : [],
        content: [InfoPane, EditPane, SidePaneHeader, SideRail]);
        // The filters work on the grid; the People page and the whole-library map have none.
        TopBar.Visibility = _chromeHidden || viewer || _workspace is Workspace.People or Workspace.Map ? Visibility.Collapsed : Visibility.Visible;
    }

    // Tab and Shift+Tab hide the panes, like Lightroom, wherever the focus is; only a text box keeps Tab.
    // Tab also comes in as a keyboard accelerator, which fires wherever the focus is; on Lasse's PC
    // PreviewKeyDown didn't see it and Tab kept moving the focus. A press already handled is skipped.
    private bool _tabHandledByPreview;

    private void AddTabAccelerators()
    {
        foreach (var modifiers in new[] { VirtualKeyModifiers.None, VirtualKeyModifiers.Shift })
        {
            var accelerator = new KeyboardAccelerator { Key = VirtualKey.Tab, Modifiers = modifiers };
            accelerator.Invoked += (_, args) =>
            {
                if (_tabHandledByPreview)
                {
                    args.Handled = true;
                    return;
                }
                if (!TabHidesPanes())
                    return;
                args.Handled = true;
                TogglePanes(modifiers == VirtualKeyModifiers.Shift);
            };
            Root.KeyboardAccelerators.Add(accelerator);
        }
    }

    private bool TabHidesPanes() =>
        !IsViewerOpen && CompareLayer.Visibility != Visibility.Visible &&
        !IsDown(VirtualKey.Control) && !IsDown(VirtualKey.Menu) &&
        FocusManager.GetFocusedElement(Root.XamlRoot) is not (TextBox or PasswordBox or RichEditBox or AutoSuggestBox);

    private void Root_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        _lastKeyWasTab = e.Key == VirtualKey.Tab;
        if (CompareLayer.Visibility == Visibility.Visible)
        {
            CompareLayer.PreviewKey(e, WasdArrow(e.Key));
            return;
        }
        if (HandleWasd(e) || HandleCopyPaste(e) || HandleCommandKeys(e))
            return;
        if (IsViewerOpen && GridHasFocus() && CompareLayer.Visibility != Visibility.Visible)
        {
            // The grid behind the viewer must not take keys: it moved its own selection, so the details pane
            // showed another photo than the viewer and a rating went to that photo.
            if (e.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down or
                VirtualKey.PageUp or VirtualKey.PageDown or VirtualKey.Home or VirtualKey.End or VirtualKey.Space or VirtualKey.Enter)
            {
                e.Handled = true;
                ViewerCloseButton.Focus(FocusState.Programmatic);
                if (e.Key == VirtualKey.Left && !IsEditing)
                    ViewerPrevious_Click(this, new RoutedEventArgs());
                else if (e.Key == VirtualKey.Right && !IsEditing)
                    ViewerNext_Click(this, new RoutedEventArgs());
                return;
            }
            ViewerCloseButton.Focus(FocusState.Programmatic);
        }
        if (e.Key != VirtualKey.Tab || !TabHidesPanes())
            return;
        e.Handled = true;
        _tabHandledByPreview = true;
        DispatcherQueue.TryEnqueue(() => _tabHandledByPreview = false);
        TogglePanes(IsDown(VirtualKey.Shift));
    }

    // Banners float over the top of the photos; in the viewer they go below its bar, which they covered
    // (Rotate and Crop could not be clicked while one showed).
    private void PlaceNotices()
    {
        var top = 8.0;
        if (IsViewerOpen && ViewerTopBar.ActualHeight > 0)
        {
            var barBottom = ViewerTopBar.TransformToVisual(Root).TransformPoint(new Windows.Foundation.Point(0, ViewerTopBar.ActualHeight)).Y;
            var panelTop = NoticePanel.TransformToVisual(Root).TransformPoint(new Windows.Foundation.Point(0, 0)).Y - NoticePanel.Margin.Top;
            top = Math.Max(8, barBottom - panelTop + 8);
        }
        NoticePanel.Margin = new Thickness(12, top, 12, 0);
    }

    private bool GridHasFocus()
    {
        for (DependencyObject? element = FocusManager.GetFocusedElement(Root.XamlRoot) as UIElement; element is not null;
             element = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(element))
        {
            if (element == PhotoGrid)
                return true;
        }
        return false;
    }

    private void TogglePanes(bool shift)
    {
        if (shift)
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

    // One tree open at a time, like an accordion; the headings all stay at the top.
    private void OpenNavSection(object heading, bool open)
    {
        foreach (var other in new object[] { FoldersHeading, DatesHeading, PlacesHeading })
        {
            var (content, chevron) = NavSection(other);
            var show = other == heading ? open : open ? false : content.Visibility == Visibility.Visible;
            content.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            chevron.Glyph = show ? "\uE70E" : "\uE70D";
        }
        LimitNavTrees();
    }

    // The open tree gets the height the headings leave free, and scrolls within it. Measured, not counted:
    // a fixed sum let a long tree run on under the Settings button. The room comes from the window's row,
    // not from the pane: a pane whose content is taller than its row reports the content's height.
    private void LimitNavTrees()
    {
        var trees = new FrameworkElement[] { FoldersContent, DateTree, PlaceTree };
        var height = NavRowSizer.ActualHeight;
        if (height <= 0)
            return;
        double used = 0;
        var rows = 0;
        foreach (var child in NavPane.Children.OfType<FrameworkElement>())
        {
            if (child.Visibility != Visibility.Visible || child == NavFade || trees.Contains(child))
                continue;
            used += child.ActualHeight > 0 ? child.ActualHeight : 36;
            rows++;
        }
        var free = height - NavPane.Padding.Top - NavPane.Padding.Bottom - used - NavPane.RowSpacing * rows;
        var room = Math.Max(72, free);
        foreach (var tree in trees)
            tree.MaxHeight = room;

        // Whatever still doesn't fit (a very low window) is cut off above Settings, never drawn over it.
        var bottom = Math.Max(0, height - NavPane.Padding.Bottom);
        NavPane.Clip = new Microsoft.UI.Xaml.Media.RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, Math.Max(0, NavPane.ActualWidth), bottom) };
        ShowNavFade();
    }

    // The fade sits over the foot of the open tree while part of it is scrolled out of sight below.
    private void ShowNavFade()
    {
        var open = new FrameworkElement[] { FoldersContent, DateTree, PlaceTree }.FirstOrDefault(t => t.Visibility == Visibility.Visible);
        var more = open is not null && Descendants(open).OfType<ScrollViewer>().FirstOrDefault() is { } viewer
                   && viewer.VerticalOffset < viewer.ScrollableHeight - 1;
        if (more)
        {
            Grid.SetRow(NavFade, Grid.GetRow(open!));
            var background = NavBackground();
            NavFade.Background = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(0, 1),
                GradientStops =
                {
                    new GradientStop { Color = Windows.UI.Color.FromArgb(0, background.R, background.G, background.B), Offset = 0 },
                    new GradientStop { Color = background, Offset = 1 },
                },
            };
        }
        NavFade.Visibility = more ? Visibility.Visible : Visibility.Collapsed;
    }

    // The colour the pane is drawn on: the first solid background from the pane up (the palette tints it), else
    // the window's in the pane's own light or dark mode. The window's root brush isn't always it: in light
    // mode it could be a dark tint, which made the fade a dark band (Lasse 2026-11-01).
    private Windows.UI.Color NavBackground()
    {
        var color = NavPane.ActualTheme == ElementTheme.Dark ? Windows.UI.Color.FromArgb(255, 32, 32, 32) : Windows.UI.Color.FromArgb(255, 243, 243, 243);
        // The see-through washes from the window down to the pane, laid over the plain window colour in turn.
        var layers = new List<Windows.UI.Color>();
        for (DependencyObject? at = NavPane; at is not null; at = VisualTreeHelper.GetParent(at))
        {
            var brush = at switch
            {
                Panel panel => panel.Background,
                Border border => border.Background,
                Control control => control.Background,
                _ => null,
            };
            if (brush is not SolidColorBrush solid)
                continue;
            var alpha = solid.Color.A * Math.Clamp(brush.Opacity, 0, 1);
            if (alpha <= 0)
                continue;
            layers.Add(Windows.UI.Color.FromArgb((byte)Math.Round(alpha), solid.Color.R, solid.Color.G, solid.Color.B));
            if (layers[^1].A == 255)
                break; // nothing below shows through
        }
        for (var i = layers.Count - 1; i >= 0; i--)
        {
            var top = layers[i];
            var a = top.A / 255.0;
            color = Windows.UI.Color.FromArgb(255, (byte)Math.Round(top.R * a + color.R * (1 - a)),
                (byte)Math.Round(top.G * a + color.G * (1 - a)), (byte)Math.Round(top.B * a + color.B * (1 - a)));
        }
        return color;
    }

    // Opening a branch or scrolling changes what lies below; the fade follows.
    private void WatchNavTrees()
    {
        // The row also shrinks when the bars above it grow, without the window changing size; the pane itself
        // doesn't notice when its content is taller than the row, so a filler of the row tells.
        NavRowSizer.SizeChanged += (_, _) => LimitNavTrees();
        foreach (var tree in new FrameworkElement[] { FolderTree, DateTree, PlaceTree })
            tree.Loaded += (_, _) =>
            {
                if (Descendants(tree).OfType<ScrollViewer>().FirstOrDefault() is { } viewer)
                {
                    viewer.RegisterPropertyChangedCallback(ScrollViewer.ScrollableHeightProperty, (_, _) => ShowNavFade());
                    viewer.ViewChanged += (_, _) => ShowNavFade();
                }
                ShowNavFade();
            };
    }

    private (FrameworkElement Content, FontIcon Chevron) NavSection(object heading) =>
        ReferenceEquals(heading, FoldersHeading) ? (FoldersContent, FoldersChevron)
        : ReferenceEquals(heading, DatesHeading) ? (DateTree, DatesChevron)
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
            var version = ++_paneSlideVersion;
            if (!IsViewerOpen)
            {
                // The details pane waits until a double-click could have happened: sliding in or out at once
                // moved the tiles, so the second click of a double-click opened another photo (U-8).
                _ = Task.Delay((int)GetDoubleClickTime()).ContinueWith(_ => DispatcherQueue.TryEnqueue(() =>
                {
                    if (version == _paneSlideVersion)
                        ApplyChrome();
                }));
            }
            else
                ApplyChrome();
        }
        else
            SyncSideSwitch(); // a video selected instead of a photo can't be edited
        SelectionCountText.Text = Tr("{0} selected", count);
        _ = ShowRepairAsync();
        ShowSelectionBar(has);
        // Room under the last row, so the bar never covers a photo that is scrolled to the end.
        ApplyGridPadding(has ? 80 : 8);
    }

    private int _paneSlideVersion;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

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

    private async void TileShots_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PhotoItemViewModel item)
            await GoThroughSimilarShotsAsync(item);
    }

    // A quality hint's icon offers to ignore the hints for this photo (C-4); the tooltip says what it means.
    private void TileQualityHint_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PhotoItemViewModel item } button)
            return;
        var ignore = new MenuFlyoutItem
        {
            Text = Tr("Ignore quality hints for this photo"),
            Icon = new FontIcon { Glyph = "\uE7BA" },
        };
        ignore.Click += async (_, _) => await ViewModel.IgnoreQualityHintsAsync([item]);
        var menu = new MenuFlyout();
        menu.Items.Add(ignore);
        menu.ShowAt(button);
    }

    private async void TileFlag_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PhotoItemViewModel item)
            await ViewModel.ToggleFlagAsync([item]);
    }

    // A click on the one selected photo deselects it, so the info pane can go away again (Esc does too).
    private PhotoItemViewModel? _pressedSelected;

    // Flag and Reject in the info pane show their words while they fit beside the stars, else only icons
    // (the German words are long). Measured with the words, so they come back when the pane widens.
    // The title bar's own name is caption-sized (12 px); like Outlook's, the app's name reads larger.
    private void StyleTitleBar()
    {
        foreach (var text in Descendants(AppTitleBar).OfType<TextBlock>().Where(t => t.Text == AppTitleBar.Title))
        {
            text.FontSize = 14;
            text.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        }
    }

    // The line under the chosen workspace is a short 16 px dash by default; like Outlook's it runs under
    // most of the word. The template's dash is its only thin rectangle.
    // It is also filled with the design colour here: the selector bar ignored the palette's brush.
    private void WidenTabMark(SelectorBarItem tab)
    {
        foreach (var mark in Descendants(tab).OfType<Microsoft.UI.Xaml.Shapes.Rectangle>().Where(r => (double.IsNaN(r.Height) ? r.ActualHeight : r.Height) is > 0 and <= 4))
        {
            mark.Width = Math.Max(16, tab.ActualWidth - 28);
            mark.Fill = ThemeBrush("AccentFillColorDefaultBrush") ?? mark.Fill;
        }
    }

    private void RecolourTabMarks()
    {
        foreach (var tab in ModeBar.Items)
            WidenTabMark(tab);
    }

    // The line beside the chosen folder, date or place: painted here too, in case the tree's template
    // kept the Windows accent like the selector bar did.
    private void RecolourTreeMark(TreeView tree)
    {
        var fill = ThemeBrush("AccentFillColorDefaultBrush");
        if (fill is null)
            return;
        foreach (var mark in Descendants(tree).OfType<FrameworkElement>().Where(e => e.Name == "SelectionIndicator"))
        {
            if (mark is Microsoft.UI.Xaml.Shapes.Shape shape)
                shape.Fill = fill;
            else if (mark is Border border)
                border.Background = fill;
        }
    }

    // Flag and Reject keep only their icons when the pane is too narrow for their words (long in German);
    // the row wraps as a last resort. Checked against the pane itself, after layout, whenever it resizes.
    private void SetUpRatingRow()
    {
        void Fit()
        {
            FlagButtonLabel.Visibility = RejectButtonLabel.Visibility = Visibility.Visible;
            RatingRow.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            var room = InfoPane.ActualWidth - InfoPane.Padding.Left - InfoPane.Padding.Right;
            if (room > 0 && RatingRow.DesiredSize.Width > room)
                FlagButtonLabel.Visibility = RejectButtonLabel.Visibility = Visibility.Collapsed;
        }
        InfoPane.SizeChanged += (_, _) => DispatcherQueue.TryEnqueue(Fit);
        RatingRow.Loaded += (_, _) => DispatcherQueue.TryEnqueue(Fit);
    }

    private void SetUpClickToDeselect()
    {
        // handledEventsToo: the grid's items take these events for their own selection handling.
        PhotoGrid.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) =>
        {
            var point = e.GetCurrentPoint(PhotoGrid);
            if (point.Properties.IsRightButtonPressed)
            {
                // Before the menu opens (on release): right-clicking outside the selection selects that photo.
                SelectForMenu(e.OriginalSource);
                return;
            }
            if (!point.Properties.IsLeftButtonPressed)
                return;
            // A double-click opens the photo of its first click, even if the tiles moved in between (U-8).
            if (ClickedPhoto(e.OriginalSource) is { } item &&
                (_pressedSelected is null || Environment.TickCount64 - _pressedAt > GetDoubleClickTime()))
            {
                _pressedSelected = item;
                _pressedAt = Environment.TickCount64;
            }
        }), true);
        // A plain click on the only selected photo keeps it selected (Ctrl+click takes it out): clearing it
        // folded the details pane away between the two clicks of a double-click.
    }

    private long _pressedAt;

    /// <summary>The photo the first click of a double-click went to, when that was just now.</summary>
    private PhotoItemViewModel? FirstClickedPhoto() =>
        Environment.TickCount64 - _pressedAt <= GetDoubleClickTime() * 2 ? _pressedSelected : null;

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

    // I shows the details, even with no photo selected, or folds them away.
    private void ToggleInfoPane()
    {
        _panesHidden = _chromeHidden = false;
        if (RightSide() == RightPane.Info)
        {
            _sideFolded = true;
            _infoForced = null;
            ApplyChrome();
        }
        else if (PaneUsable(SidePane.Info))
            _ = SetSidePaneAsync(SidePane.Info);
    }

    /// <summary>
    /// The things the right-hand pane can show, as chosen with the switch above it. Edit belongs to the viewer:
    /// chosen beside the grid, it opens the selected photo (MainWindow.EditPane.cs).
    /// </summary>
    private enum SidePane { Info, Map, Edit }

    // The details come and go with the selection; the map stays. Folded, the pane is a strip of icons.
    private SidePane _sidePane = SidePane.Info;
    private bool _sideFolded;

    // The switch and the strip serve the viewer too while it is open.
    private void SideInfo_Click(object sender, RoutedEventArgs e) => ShowRightPane(SidePane.Info);

    private void SideMap_Click(object sender, RoutedEventArgs e) => ShowRightPane(SidePane.Map);

    private void SideRailInfo_Click(object sender, RoutedEventArgs e) => ShowRightPane(SidePane.Info);

    private void SideRailMap_Click(object sender, RoutedEventArgs e) => ShowRightPane(SidePane.Map);

    private void SideEdit_Click(object sender, RoutedEventArgs e) => ShowRightPane(SidePane.Edit);

    private void SideRailEdit_Click(object sender, RoutedEventArgs e) => ShowRightPane(SidePane.Edit);

    private void UnfoldSidePane_Click(object sender, RoutedEventArgs e) =>
        ShowRightPane(IsViewerOpen ? _viewerPane : _sidePane);

    private void FoldSidePane_Click(object sender, RoutedEventArgs e)
    {
        if (IsViewerOpen)
            _viewerFolded = true;
        else
        {
            _sideFolded = true;
            _infoForced = null;
        }
        ApplyChrome();
    }

    private void ShowRightPane(SidePane pane)
    {
        // A click that lands just as the selection empties, before the switch has caught up (the pane waits for
        // a possible double-click), does nothing rather than open an empty pane.
        if (!PaneUsable(pane))
        {
            SyncSideSwitch();
            return;
        }
        if (pane == SidePane.Edit && !IsViewerOpen)
        {
            EditSelectedPhoto();
            SyncSideSwitch();
        }
        else if (IsViewerOpen)
            _ = SetViewerPaneAsync(pane);
        else
            _ = SetSidePaneAsync(pane);
    }

    // Choosing one unfolds the pane; the details then show even with nothing selected.
    private async Task SetSidePaneAsync(SidePane pane)
    {
        var mapBefore = MapBeside;
        _sidePane = pane;
        _sideFolded = false;
        _infoForced = pane == SidePane.Info ? true : null;
        if (MapBeside != mapBefore)
            await ApplyMapLayoutAsync();
        else
            ApplyChrome();
    }

    // The thumb slides under the chosen icon, which turns the colour of text on the accent.
    // Only the panes that can be used now have an icon, on the switch and on the folded strip; the switch
    // narrows to them.
    private void SyncSideSwitch()
    {
        var pane = IsViewerOpen ? ShownPane(_viewerPane) : _sidePane;
        var buttons = new (SidePane Pane, FrameworkElement Switch, FrameworkElement Rail)[]
        {
            (SidePane.Info, SideInfoButton, SideRailInfoButton),
            (SidePane.Map, SideMapButton, SideRailMapButton),
            (SidePane.Edit, SideEditButton, SideRailEditButton),
        };
        var slot = 0;
        var thumbSlot = -1;
        foreach (var (each, onSwitch, onRail) in buttons)
        {
            var usable = PaneUsable(each);
            onSwitch.Visibility = onRail.Visibility = usable ? Visibility.Visible : Visibility.Collapsed;
            if (!usable)
                continue;
            if (each == pane)
                thumbSlot = slot;
            slot++;
        }
        SideSwitch.Width = SideMapButton.Width * slot + 6;
        SideSwitchThumb.Visibility = thumbSlot < 0 ? Visibility.Collapsed : Visibility.Visible;
        SideSwitchThumb.Translation = new System.Numerics.Vector3((float)SideMapButton.Width * Math.Max(thumbSlot, 0), 0, 0);
        // Unfolding beside the grid brings back the pane last chosen, so it shows only while that one can be used.
        SideRailUnfoldButton.Visibility = IsViewerOpen || PaneUsable(_sidePane) ? Visibility.Visible : Visibility.Collapsed;
        SideInfoIconOn.Opacity = pane == SidePane.Info ? 1 : 0;
        SideInfoIconOff.Opacity = pane == SidePane.Info ? 0 : 1;
        SideMapIconOn.Opacity = pane == SidePane.Map ? 1 : 0;
        SideMapIconOff.Opacity = pane == SidePane.Map ? 0 : 1;
        SideEditIconOn.Opacity = pane == SidePane.Edit ? 1 : 0;
        SideEditIconOff.Opacity = pane == SidePane.Edit ? 0 : 1;
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
        // The clickable stars take the place of the plain ones under the photo.
        if ((tile as FrameworkElement)?.FindName("StarLine") is UIElement stars)
            stars.Opacity = show ? 0 : 1;
    }

    private static void Lift(FrameworkElement element, bool up)
    {
        if (!s_animations)
            return;
        try
        {
            element.ScaleTransition ??= new Vector3Transition { Duration = TimeSpan.FromMilliseconds(167) };
            element.TranslationTransition ??= new Vector3Transition { Duration = TimeSpan.FromMilliseconds(167) };
            element.CenterPoint = new Vector3((float)element.ActualWidth / 2, (float)element.ActualHeight / 2, 0);
            element.Scale = up ? new Vector3(1.03f, 1.03f, 1) : Vector3.One;
            element.Translation = up ? new Vector3(0, -2, 0) : Vector3.Zero;
        }
        catch (Exception e)
        {
            // WinUI refuses these while something else moves the tile (e.g. it glides with a pane): it just doesn't rise.
            App.Trace($"tile lift skipped: {e.Message}");
        }
    }
}
