using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PhotoGallery.App.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.App;

/// <summary>
/// Every action on photos in one list: its name, icon, key and group. The selection bar, the right-click menu
/// and the viewer's "…" menu are all made from it, so an action has the same name, icon and key everywhere,
/// and the key shows in every menu and tooltip. The groups are the same on all three: Mark, Look, Describe,
/// Send and Organise (navigation proposal agreed with Lasse, 2026-10-27). Topics may share an icon, actions never do.
/// </summary>
public sealed partial class MainWindow
{
    private enum CommandGroup { Mark, Look, Describe, Send, Organise }

    private sealed class PhotoCommand
    {
        public required string Label { get; init; }

        /// <summary>One Segoe Fluent Icons character, or path data from <see cref="AppIcons"/>.</summary>
        public required string Glyph { get; init; }

        public required CommandGroup Group { get; init; }

        public required Action Run { get; init; }

        /// <summary>The key as shown in menus and tooltips, e.g. "Ctrl+D".</summary>
        public string? Keys { get; init; }

        public string? Tip { get; init; }

        /// <summary>More words the command box (Ctrl+K) finds it by, e.g. "delete" for the Recycle Bin.</summary>
        public string? Also { get; init; }

        /// <summary>The rotate-left icon is the rotate-right one mirrored.</summary>
        public bool Mirrored { get; init; }

        /// <summary>Waits while a long job runs (<see cref="Busy"/>).</summary>
        public bool Gated { get; init; }

        /// <summary>Given the photos it would act on; null means always.</summary>
        public Func<IReadOnlyList<PhotoItemViewModel>, bool>? Enabled { get; init; }

        /// <summary>A label that says more for the photos at hand ("Upload 3 photos to OneDrive").</summary>
        public Func<IReadOnlyList<PhotoItemViewModel>, string>? LabelFor { get; init; }

        /// <summary>False leaves the command out of the viewer's menu (it has its own button there, or makes no sense).</summary>
        public bool InViewer { get; init; } = true;
    }

    // The , and . keys have no names in VirtualKey.
    private const VirtualKey CommaKey = (VirtualKey)188;
    private const VirtualKey PeriodKey = (VirtualKey)190;

    private List<PhotoCommand>? _commands;
    private readonly List<(PhotoCommand Command, AppBarButton Button)> _barOverflow = [];

    /// <summary>What a command acts on: the photo in the viewer, else the grid's selection.</summary>
    private List<PhotoItemViewModel> CommandTargets() => IsViewerOpen && ViewerItem is { } shown ? [shown] : SelectedPhotos;

    private static string WithKeys(string text, string? keys) => keys is null ? text : $"{text} ({keys})";

    private List<PhotoCommand> Commands => _commands ??= MakeCommands();

    private PhotoCommand Command(string label) => Commands.First(c => c.Label == label);

    private List<PhotoCommand> MakeCommands()
    {
        static bool Any(IReadOnlyList<PhotoItemViewModel> p) => p.Count > 0;
        static bool One(IReadOnlyList<PhotoItemViewModel> p) => p.Count == 1;
        void Do(RoutedEventHandler handler) => handler(this, new RoutedEventArgs());

        return
        [
            // Mark
            new() { Label = Tr("Flag"), Glyph = "\uE7C1", Group = CommandGroup.Mark, Keys = "P", Enabled = Any,
                    Tip = Tr("Flag or unflag the selected photos"), Run = () => _ = MarkAsync(ViewModel.ToggleFlagAsync) },
            new() { Label = Tr("Reject"), Glyph = "\uE711", Group = CommandGroup.Mark, Keys = "X", Enabled = Any,
                    Tip = Tr("Reject the selected photos, or take the mark off. Nothing is deleted."), Run = () => _ = MarkAsync(ViewModel.ToggleRejectAsync) },

            // Look
            new() { Label = Tr("Open large"), Also = Tr("view enlarge"), Glyph = "\uE740", Group = CommandGroup.Look, Keys = "E", Enabled = Any, InViewer = false,
                    Run = () => { if (SelectedPhotos is [var first, ..]) OpenViewer(first); } },
            new() { Label = Tr("Compare"), Glyph = "\uE8A9", Group = CommandGroup.Look, Keys = "C", Enabled = Any, InViewer = false,
                    Tip = Tr("Show 1 to 4 selected photos side by side to rate them"), Run = () => OpenCompare(SelectedPhotos) },
            new() { Label = Tr("Find similar photos"), Also = Tr("alike look-alikes same"), Glyph = AppIcons.FindSimilar, Group = CommandGroup.Look, Enabled = One, Gated = true,
                    Tip = Tr("Show the photos that look like this one, most alike first"), Run = () => _ = FindSimilarAsync() },
            new() { Label = Tr("Rotate left"), Also = Tr("turn"), Glyph = "\uE7AD", Mirrored = true, Group = CommandGroup.Look, Keys = "Ctrl+,", Enabled = Any,
                    Run = () => Do(IsViewerOpen ? (RoutedEventHandler)ViewerRotateLeft_Click : RotateLeft_Click) },
            new() { Label = Tr("Rotate right"), Also = Tr("turn"), Glyph = "\uE7AD", Group = CommandGroup.Look, Keys = "Ctrl+.", Enabled = Any,
                    Run = () => Do(IsViewerOpen ? (RoutedEventHandler)ViewerRotateRight_Click : RotateRight_Click) },

            // Describe
            new() { Label = Tr("Look up place"), Also = Tr("location address city country"), Glyph = AppIcons.PlaceText, Group = CommandGroup.Describe, Enabled = Any, Gated = true,
                    Tip = Tr("Look up country, region, city and district from the GPS position of the selected photos (OpenStreetMap) and save them in the files"),
                    Run = () => Do(AddPlaceNames_Click) },
            new() { Label = Tr("Auto-tag"), Also = Tr("keywords AI recognise"), Glyph = AppIcons.TagSparkle, Group = CommandGroup.Describe, Enabled = Any, Gated = true,
                    Tip = Tr("Recognise what is in the selected photos on this PC and add tags from your tag list"), Run = () => Do(AutoTag_Click) },
            new() { Label = Tr("Remove auto tags"), Glyph = AppIcons.RemoveAutoTags, Group = CommandGroup.Describe, Enabled = Any, Gated = true,
                    Tip = Tr("Remove the tags auto-tagging added to the selected photos; your own tags stay"), Run = () => Do(RemoveAutoTags_Click) },
            new() { Label = Tr("Find people"), Glyph = AppIcons.FindPeople, Group = CommandGroup.Describe, Enabled = p => p.Any(i => !i.IsVideo), Gated = true,
                    Tip = Tr("Find people in the selected photos"), Run = () => Do(FindPeople_Click) },
            new() { Label = Tr("Add a person…"), Glyph = "\uE8FA", Group = CommandGroup.Describe, Enabled = Any,
                    Tip = Tr("Add a person to the selected photos, without marking a face"), Run = () => Do(AddPerson_Click) },
            new() { Label = Tr("Adjust date and time…"), Also = Tr("time zone shift timestamp"), Glyph = "\uEC92", Group = CommandGroup.Describe, Keys = "Ctrl+Shift+D", Enabled = Any,
                    Tip = Tr("Correct the date and time of the selected photos"), Run = () => Do(ShiftDates_Click) },
            new() { Label = Tr("Copy details"), Glyph = "\uE779", Group = CommandGroup.Describe, Keys = "Ctrl+Shift+C", Enabled = One,
                    Tip = Tr("Copy the details of the photo (location, date, camera, tags …)"), Run = () => Do(CopyDetails_Click) },
            new() { Label = Tr("Paste details…"), Glyph = "\uECC8", Group = CommandGroup.Describe, Keys = "Ctrl+Shift+V", Gated = true,
                    Enabled = p => p.Count > 0 && ViewModel.CopiedDetails is not null,
                    LabelFor = _ => ViewModel.CopiedDetails is null ? Tr("Paste details…") : Tr("Paste details of {0}…", ViewModel.CopiedFrom),
                    Tip = Tr("Paste details onto the selected photos"), Run = () => Do(PasteDetails_Click) },

            // Send (the favourite apps are added to this group where it is shown)
            new() { Label = Tr("Upload to Google Photos"), Glyph = "\uE898", Group = CommandGroup.Send, Enabled = Any,
                    LabelFor = p => p.Count > 1 ? Tr("Upload {0} photos to Google Photos", p.Count) : Tr("Upload to Google Photos"),
                    Run = () => Do(UploadPhotosToGoogle_Click) },
            new() { Label = Tr("Upload to OneDrive"), Glyph = "\uE753", Group = CommandGroup.Send, Enabled = Any,
                    LabelFor = p => p.Count > 1 ? Tr("Upload {0} photos to OneDrive", p.Count) : Tr("Upload to OneDrive"),
                    Run = () => Do(UploadPhotosToOneDrive_Click) },
            new() { Label = Tr("Share…"), Also = Tr("send email"), Glyph = "\uE72D", Group = CommandGroup.Send, Enabled = Any,
                    Tip = Tr("Send the photos with another app or to a nearby PC, as Share does in File Explorer"), Run = () => Do(Share_Click) },
            new() { Label = Tr("Open with…"), Glyph = "\uE7AC", Group = CommandGroup.Send, Enabled = One,
                    Tip = Tr("Open this photo in another app, e.g. to edit it there. A copy is kept in .originals first, so Back to original still works."),
                    Run = () => Do(IsViewerOpen ? (RoutedEventHandler)ViewerOpenWith_Click : OpenWith_Click) },

            // Organise
            new() { Label = Tr("Rename…"), Glyph = "\uE8AC", Group = CommandGroup.Organise, Keys = "F2", Enabled = Any, Gated = true, InViewer = false,
                    Tip = Tr("Rename the selected photos with a name pattern such as date and counter"), Run = () => Do(RenamePhotos_Click) },
            new() { Label = Tr("Duplicate"), Also = Tr("copy"), Glyph = AppIcons.Duplicate, Group = CommandGroup.Organise, Keys = "Ctrl+D", Enabled = Any, Gated = true,
                    Tip = Tr("Make a copy of each photo next to it, with all its details"), Run = () => _ = DuplicateAsync() },
            new() { Label = Tr("Copy files"), Glyph = "\uE8C8", Group = CommandGroup.Organise, Keys = "Ctrl+C", Enabled = Any,
                    Tip = Tr("Copy the photo files, to paste them in File Explorer, another app or another folder here"), Run = () => _ = CopyFilesAsync() },
            new() { Label = Tr("Paste files"), Glyph = "\uE77F", Group = CommandGroup.Organise, Keys = "Ctrl+V", Gated = true, InViewer = false,
                    Enabled = _ => ViewModel.SelectedFolder is not null && ClipboardHasFiles(),
                    Tip = Tr("Copy the files from the clipboard into the folder shown"), Run = () => _ = PasteFilesAsync() },
            new() { Label = Tr("Move to folder…"), Glyph = "\uE8DE", Group = CommandGroup.Organise, Enabled = Any, Gated = true,
                    Tip = Tr("Move the selected photos into another folder of your library"), Run = () => _ = MoveToFolderAsync() },
            new() { Label = Tr("Make private"), Also = Tr("hide"), Glyph = "\uE72E", Group = CommandGroup.Organise, Enabled = Any,
                    LabelFor = p => SelectionIsPrivate(p) ? Tr("Not private any more") : Tr("Make private"),
                    Tip = Tr("Hide the photos until private mode is on"), Run = () => Do(PhotosPrivate_Click) },
            new() { Label = Tr("Use as album cover"), Glyph = AppIcons.AlbumCover, Group = CommandGroup.Organise,
                    Enabled = p => p.Count == 1 && ViewModel.FindAlbumFor(p[0].Path) is not null,
                    LabelFor = p => p.Count == 1 && ViewModel.FindAlbumFor(p[0].Path) is { } album
                        ? Tr("Use as cover of “{0}”", ViewModel.CurrentAlbumName(album))
                        : Tr("Use as album cover (select one photo in an album)"),
                    Run = () => Do(UseAsAlbumCover_Click) },
            new() { Label = Tr("Move to the Recycle Bin…"), Also = Tr("delete remove trash"), Glyph = "\uE74D", Group = CommandGroup.Organise, Keys = "Del", Enabled = Any, Gated = true,
                    Tip = Tr("Move the selected photos to the Recycle Bin, from where they can be restored"), Run = () => _ = RecycleSelectedAsync() },
            new() { Label = Tr("Show in folder"), Glyph = "\uE838", Group = CommandGroup.Organise, Enabled = One,
                    Tip = Tr("Open the folder in File Explorer with the photo selected"), Run = () => Do(ShowInFolderMenu_Click) },
        ];
    }

    // A mark from a menu works like its key: in the viewer on the photo shown (and moves on with auto-advance).
    private async Task MarkAsync(Func<IReadOnlyList<PhotoItemViewModel>, Task> mark)
    {
        if (IsViewerOpen)
        {
            if (ViewerItem is { } item && !IsEditing)
            {
                await mark([item]);
                AfterMarkInViewer();
            }
            return;
        }
        await mark(SelectedPhotos);
    }

    private async Task RateAsync(int stars)
    {
        await MarkAsync(photos => ViewModel.SetRatingAsync(photos, stars));
        if (!IsViewerOpen && !ViewModel.Editor.IsDirty)
            ViewModel.Editor.Load(SelectedPhotos);
    }

    private static IconElement Icon(PhotoCommand command)
    {
        var icon = AppIcons.Make(command.Glyph);
        if (command.Mirrored)
        {
            icon.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
            icon.RenderTransform = new ScaleTransform { ScaleX = -1 };
        }
        return icon;
    }

    private MenuFlyoutItem MenuItem(PhotoCommand command, IReadOnlyList<PhotoItemViewModel> targets)
    {
        var item = new MenuFlyoutItem
        {
            Text = command.LabelFor?.Invoke(targets) ?? command.Label,
            Icon = Icon(command),
            IsEnabled = command.Enabled?.Invoke(targets) ?? true,
        };
        if (command.Keys is { } keys)
            item.KeyboardAcceleratorTextOverride = keys;
        if (command.Tip is { } tip)
            ToolTipService.SetToolTip(item, tip);
        // Menu items are made anew each time, so they are not gated (Busy keeps its elements); the work
        // they start waits by itself and says why in a banner.
        item.Click += (_, _) =>
        {
            if (command.Keys is { } keys)
                CountClick(keys, command.Label);
            command.Run();
        };
        return item;
    }

    private void AddGroup(IList<MenuFlyoutItemBase> items, CommandGroup group, IReadOnlyList<PhotoItemViewModel> targets, bool viewer)
    {
        foreach (var command in Commands.Where(c => c.Group == group && (!viewer || c.InViewer)))
            items.Add(MenuItem(command, targets));
        if (group == CommandGroup.Send)
            AddFavouriteApps(items, targets, viewer);
    }

    // The two favourite apps from Settings › Linked apps, with their own icons.
    private void AddFavouriteApps(IList<MenuFlyoutItemBase> items, IReadOnlyList<PhotoItemViewModel> targets, bool viewer)
    {
        var apps = ViewModel.FavouriteApps;
        for (var i = 0; i < apps.Count && i < 2; i++)
        {
            var item = new MenuFlyoutItem
            {
                Text = Tr("Edit in {0}", apps[i].Name),
                Tag = i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                IsEnabled = targets.Count > 0,
                Icon = i < _favouriteIcons.Count && _favouriteIcons[i] is { } source ? new ImageIcon { Source = source } : (IconElement)new FontIcon { Glyph = "\uE70F" },
            };
            ToolTipService.SetToolTip(item, Tr("Open the selected photos in {0}. A copy is kept in .originals first, so Back to original still works.", apps[i].Name));
            item.Click += viewer ? (RoutedEventHandler)ViewerFavouriteApp_Click : FavouriteApp_Click;
            items.Add(item);
        }
    }

    private MenuFlyoutSubItem SubMenu(string text, string glyph, CommandGroup group, IReadOnlyList<PhotoItemViewModel> targets, bool viewer)
    {
        var sub = new MenuFlyoutSubItem { Text = text, Icon = new FontIcon { Glyph = glyph }, IsEnabled = targets.Count > 0 };
        AddGroup(sub.Items, group, targets, viewer);
        return sub;
    }

    private MenuFlyoutSubItem RatingMenu(IReadOnlyList<PhotoItemViewModel> targets)
    {
        var sub = new MenuFlyoutSubItem { Text = Tr("Rating"), Icon = new FontIcon { Glyph = "\uE734" }, IsEnabled = targets.Count > 0 };
        for (var stars = 5; stars >= 0; stars--)
        {
            var n = stars;
            var item = new MenuFlyoutItem
            {
                Text = n == 0 ? Tr("No rating") : new string('★', n),
                KeyboardAcceleratorTextOverride = n.ToString(System.Globalization.CultureInfo.InvariantCulture),
            };
            item.Click += (_, _) =>
            {
                CountRatingClick();
                _ = RateAsync(n);
            };
            sub.Items.Add(item);
        }
        return sub;
    }

    /// <summary>
    /// The right-click menu on photos, and the viewer's "…" menu, which is the same menu for the photo shown:
    /// Repair (only for a damaged file), Mark, Look, then Describe, Send to and Organise as submenus.
    /// </summary>
    private void FillPhotoMenu(MenuFlyout menu, bool viewer)
    {
        var targets = CommandTargets();
        var items = menu.Items;
        items.Clear();
        if (!viewer && _damagedInSelection is not null)
        {
            var repair = new MenuFlyoutItem { Text = Tr("Repair…"), Icon = new FontIcon { Glyph = "\uE90F" } };
            ToolTipService.SetToolTip(repair, Tr("Repair damaged file"));
            repair.Click += RepairSelected_Click;
            items.Add(repair);
            items.Add(new MenuFlyoutSeparator());
        }
        items.Add(RatingMenu(targets));
        AddGroup(items, CommandGroup.Mark, targets, viewer);
        items.Add(new MenuFlyoutSeparator());
        AddGroup(items, CommandGroup.Look, targets, viewer);
        items.Add(new MenuFlyoutSeparator());
        items.Add(SubMenu(Tr("Describe"), "\uE70F", CommandGroup.Describe, targets, viewer));
        items.Add(SubMenu(Tr("Send to"), "\uE724", CommandGroup.Send, targets, viewer));
        items.Add(SubMenu(Tr("Organise"), "\uE8B7", CommandGroup.Organise, targets, viewer));
    }

    private void PhotoMenu_Opening(object sender, object e)
    {
        if (sender is MenuFlyout menu)
            FillPhotoMenu(menu, viewer: false);
    }

    private void ViewerMoreMenu_Opening(object sender, object e)
    {
        if (sender is MenuFlyout menu)
            FillPhotoMenu(menu, viewer: true);
    }

    /// <summary>
    /// The selection bar: Flag and Reject, Compare and Rotate, then Describe and Send as menus; Organise is in
    /// its "…". Made once; its menus and "…" are brought up to date as they open.
    /// </summary>
    private void BuildSelectionBar()
    {
        var bar = SelectionCommands;

        AppBarButton MakeButton(PhotoCommand command, bool iconOnly = false)
        {
            var button = new AppBarButton { Label = command.Label, Icon = Icon(command) };
            if (iconOnly)
            {
                // Without a label the button keeps the labelled width, which leaves a gap; make it square.
                button.LabelPosition = CommandBarLabelPosition.Collapsed;
                button.Width = 48;
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, command.Label);
            }
            if (command.Keys is { } keys)
                button.KeyboardAcceleratorTextOverride = keys;
            ToolTipService.SetToolTip(button, WithKeys(command.Tip ?? command.Label, command.Keys));
            if (command.Gated)
                Busy.SetGate(button, true);
            button.Click += (_, _) =>
            {
                if (command.Keys is { } keys)
                    CountClick(keys, command.Label);
                command.Run();
            };
            return button;
        }

        AppBarButton MakeGroup(string label, string glyph, CommandGroup group, string tip)
        {
            var flyout = new MenuFlyout { Placement = FlyoutPlacementMode.Top };
            flyout.Opening += (_, _) =>
            {
                flyout.Items.Clear();
                AddGroup(flyout.Items, group, SelectedPhotos, viewer: false);
            };
            var button = new AppBarButton { Label = label, Icon = new FontIcon { Glyph = glyph }, Flyout = flyout };
            ToolTipService.SetToolTip(button, tip);
            return button;
        }

        bar.PrimaryCommands.Add(MakeButton(Command(Tr("Flag"))));
        bar.PrimaryCommands.Add(MakeButton(Command(Tr("Reject"))));
        bar.PrimaryCommands.Add(new AppBarSeparator());
        bar.PrimaryCommands.Add(MakeButton(Command(Tr("Compare"))));
        bar.PrimaryCommands.Add(MakeButton(Command(Tr("Rotate left")), iconOnly: true));
        bar.PrimaryCommands.Add(MakeButton(Command(Tr("Rotate right")), iconOnly: true));
        bar.PrimaryCommands.Add(new AppBarSeparator());
        bar.PrimaryCommands.Add(MakeGroup(Tr("Describe"), "\uE70F", CommandGroup.Describe,
            Tr("Place, tags, people, date and time, and copying details from one photo to others")));
        bar.PrimaryCommands.Add(MakeGroup(Tr("Send"), "\uE724", CommandGroup.Send,
            Tr("Upload, share, or open the photos in another app")));

        foreach (var command in Commands.Where(c => c.Group == CommandGroup.Organise))
        {
            var button = MakeButton(command);
            _barOverflow.Add((command, button));
            bar.SecondaryCommands.Add(button);
        }
    }

    // The "…" of the selection bar follows the selection like the right-click menu.
    private void SelectionCommands_Opening(object? sender, object e)
    {
        var photos = SelectedPhotos;
        foreach (var (command, button) in _barOverflow)
        {
            button.Label = command.LabelFor?.Invoke(photos) ?? command.Label;
            button.IsEnabled = command.Enabled?.Invoke(photos) ?? true;
        }
    }

    /// <summary>
    /// The keys of the command list that are not single letters (those are in Root_KeyDown): Ctrl+C, Ctrl+V,
    /// Ctrl+D, Ctrl+Z, Ctrl+Shift+D, Ctrl+I, Ctrl+K, Ctrl+1–3, Del and F5, and in the viewer R, Ctrl+U and the rotate keys.
    /// True when the key was used. Typing in a box keeps its own Ctrl+C and Ctrl+V.
    /// </summary>
    private bool HandleCommandKeys(KeyRoutedEventArgs e)
    {
        // The command box opens from anywhere, even while typing in the search box.
        if (e.Key == VirtualKey.K && IsDown(VirtualKey.Control) && !IsDown(VirtualKey.Shift) && !IsDown(VirtualKey.Menu))
        {
            e.Handled = true;
            _ = ShowCommandBoxAsync();
            return true;
        }
        if (IsTyping() || IsDown(VirtualKey.Menu) || IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows))
            return false;
        var ctrl = IsDown(VirtualKey.Control);
        var shift = IsDown(VirtualKey.Shift);
        var viewer = IsViewerOpen;
        var photoTools = viewer && !IsEditing && !_viewerShowsVideo; // also when a narrow bar hides the buttons
        Action? action = (ctrl, shift, e.Key) switch
        {
            (true, false, VirtualKey.C) => () => _ = CopyFilesAsync(),
            (true, false, VirtualKey.V) when !viewer => () => _ = PasteFilesAsync(),
            (true, false, VirtualKey.D) => () => _ = DuplicateAsync(),
            (true, false, VirtualKey.Z) when !viewer || !IsEditing => () => _ = UndoAsync(),
            (true, true, VirtualKey.D) => () => ShiftDates_Click(this, new RoutedEventArgs()),
            (true, false, VirtualKey.I) when !viewer => () => Import_Click(this, new RoutedEventArgs()),
            (true, false, VirtualKey.Number1 or VirtualKey.NumberPad1) when !viewer => () => ShowWorkspace(Workspace.Library),
            (true, false, VirtualKey.Number2 or VirtualKey.NumberPad2) when !viewer => () => ShowWorkspace(Workspace.Map),
            (true, false, VirtualKey.Number3 or VirtualKey.NumberPad3) when !viewer => () => ShowWorkspace(Workspace.People),
            (true, false, VirtualKey.U) when photoTools => () =>
            {
                ViewerAutoButton.IsChecked = true;
                ViewerAuto_Click(this, new RoutedEventArgs());
            },
            // In the grid Ctrl+, and Ctrl+. are keyboard accelerators (MainWindow.Frame.cs).
            (true, false, CommaKey) when photoTools => () => ViewerRotateLeft_Click(this, new RoutedEventArgs()),
            (true, false, PeriodKey) when photoTools => () => ViewerRotateRight_Click(this, new RoutedEventArgs()),
            (false, false, VirtualKey.R) when photoTools => () =>
            {
                ViewerCropButton.IsChecked = true;
                ViewerCrop_Click(this, new RoutedEventArgs());
            },
            (false, false, VirtualKey.Delete) when !viewer || !IsEditing => () => _ = RecycleSelectedAsync(),
            (false, false, VirtualKey.F5) when !viewer => () => Rescan_Click(this, new RoutedEventArgs()),
            _ => null,
        };
        if (action is null)
            return false;
        e.Handled = true;
        action();
        return true;
    }

    // ---- Organise: copy, paste, duplicate, move, recycle ----

    private static bool ClipboardHasFiles()
    {
        try
        {
            return Clipboard.GetContent().Contains(StandardDataFormats.StorageItems);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            return false; // Another app holds the clipboard just now.
        }
    }

    // Ctrl+C: the files go on the clipboard as File Explorer puts them there, so they paste anywhere.
    private async Task CopyFilesAsync()
    {
        var paths = CommandTargets().Select(p => p.Path).ToList();
        if (paths.Count == 0)
            return;
        // Storage files are made off the UI thread: Windows storage calls there can re-enter WinUI and crash it.
        var files = await Task.Run(async () =>
        {
            var found = new List<Windows.Storage.IStorageItem>();
            foreach (var path in paths)
            {
                try
                {
                    found.Add(await Windows.Storage.StorageFile.GetFileFromPathAsync(path));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    // A file moved or deleted meanwhile is left out.
                }
            }
            return found;
        });
        if (files.Count == 0)
        {
            ViewModel.Warn(Tr("The selected photos could not be found."));
            return;
        }
        try
        {
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetStorageItems(files);
            Clipboard.SetContent(package);
            Clipboard.Flush(); // Stays on the clipboard after the app closes.
            ViewModel.Inform(Tr("Copied {0}. Paste them with Ctrl+V into a folder here or in File Explorer.", MainViewModel.PhotoCount(files.Count)));
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            ViewModel.Warn(Tr("The clipboard is in use by another app. Try again."));
        }
    }

    // Ctrl+V: files copied here or in File Explorer go into the folder shown.
    private async Task PasteFilesAsync()
    {
        if (WaitsForBusyWork())
            return;
        if (ViewModel.SelectedFolder is not { } folder)
        {
            ViewModel.Warn(Tr("Open the folder to paste the photos into."));
            return;
        }
        List<string> paths;
        try
        {
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.StorageItems))
            {
                ViewModel.Warn(Tr("The clipboard holds no photos or videos to paste."));
                return;
            }
            var items = await content.GetStorageItemsAsync();
            paths = items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            ViewModel.Warn(Tr("The clipboard is in use by another app. Try again."));
            return;
        }
        await ViewModel.PasteFilesAsync(paths, folder.FullPath, folder.Name);
    }

    private async Task DuplicateAsync()
    {
        if (!WaitsForBusyWork())
            await ViewModel.DuplicateAsync(CommandTargets());
    }

    // Like dragging onto a folder in the left pane, for those who prefer a menu; the folder must be in the library.
    private async Task MoveToFolderAsync()
    {
        var photos = CommandTargets();
        if (photos.Count == 0 || WaitsForBusyWork())
            return;
        var picker = new Windows.Storage.Pickers.FolderPicker { SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);
        if (await picker.PickSingleFolderAsync() is not { } folder)
            return;
        var target = Path.GetFullPath(folder.Path).TrimEnd(Path.DirectorySeparatorChar);
        if (!ViewModel.Folders.Any(root =>
            {
                var top = Path.GetFullPath(root.FullPath).TrimEnd(Path.DirectorySeparatorChar);
                return target.Equals(top, StringComparison.OrdinalIgnoreCase) ||
                       target.StartsWith(top + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }))
        {
            ViewModel.Warn(Tr("Pick a folder in your library. To add one, use Add a folder in the Library menu."));
            return;
        }
        if (IsViewerOpen)
            CloseViewer();
        await ViewModel.TransferAsync(photos, folder.Path, folder.Name, copy: false);
    }

    // Del: to the Recycle Bin after asking; the question also offers the gentler reject mark (X).
    private async Task RecycleSelectedAsync()
    {
        var photos = CommandTargets();
        if (photos.Count == 0 || WaitsForBusyWork())
            return;
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = Tr("Move {0} to the Recycle Bin?", MainViewModel.PhotoCount(photos.Count)),
            Content = new TextBlock
            {
                Text = Tr("They go to the Recycle Bin with their earlier versions, and can be restored from there until it is emptied. To only mark them as bad and decide later, reject them instead (X)."),
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = Tr("Move to Recycle Bin"),
            SecondaryButtonText = Tr("Only reject (X)"),
            CloseButtonText = Tr("Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        switch (await dialog.ShowThemedAsync())
        {
            case ContentDialogResult.Primary:
                if (IsViewerOpen)
                    CloseViewer();
                App.Trace($"recycle bin: {photos.Count} photos");
                await ViewModel.RecycleAsync(photos);
                break;
            case ContentDialogResult.Secondary:
                await MarkAsync(p => ViewModel.ToggleRejectAsync(p.Where(x => !x.Photo.Rejected).ToList()));
                break;
        }
    }
}
