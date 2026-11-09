using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PhotoGallery.Core.Import;
using Windows.Storage.Pickers;
using Windows.System;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.App;

/// <summary>
/// Importing from a website (F-8): enter an address, the app reads the gallery on that one site, shows the photos it
/// found in a grid to tick, and downloads the ticked ones into a folder named like a device import.
/// </summary>
public sealed partial class MainWindow
{
    private const string GlobeGlyph = "";

    private async Task WebImportAsync()
    {
        // Only sites the user may download from: asked before every scan.
        var notice = new InfoBar
        {
            IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Warning,
            Title = Tr("Only from sites you may download from"),
            Message = Tr("Use this for your own sites, or with the owner’s permission. Photos are protected by copyright, and many sites’ terms don’t allow downloading them."),
        };
        var allowed = new CheckBox { Content = Tr("I may download the photos from this site") };

        var address = new TextBox { PlaceholderText = "https://", InputScope = new InputScope { Names = { new InputScopeName(InputScopeNameValue.Url) } } };
        AutomationProperties.SetName(address, Tr("Web address"));
        var findContent = new StackPanel
        {
            Orientation = Microsoft.UI.Xaml.Controls.Orientation.Horizontal, Spacing = 8,
            Children = { new FontIcon { Glyph = GlobeGlyph, FontSize = 14 }, new TextBlock { Text = Tr("Find photos") } },
        };
        var find = new Button { Content = findContent, IsEnabled = false, Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        ToolTipService.SetToolTip(find, Tr("Goes online: reads the pages of this site"));
        var stop = new Button { Content = Tr("Stop"), Visibility = Visibility.Collapsed };
        var addressRow = new Grid
        {
            ColumnSpacing = 8,
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition(),
                new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition { Width = GridLength.Auto },
            },
        };
        var globe = new FontIcon { Glyph = GlobeGlyph, FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Foreground = ThemeBrush("TextFillColorSecondaryBrush") };
        ToolTipService.SetToolTip(globe, Tr("Goes online"));
        Grid.SetColumn(address, 1);
        Grid.SetColumn(find, 2);
        Grid.SetColumn(stop, 3);
        addressRow.Children.Add(globe);
        addressRow.Children.Add(address);
        addressRow.Children.Add(find);
        addressRow.Children.Add(stop);

        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var progress = new ProgressBar { IsIndeterminate = true, Visibility = Visibility.Collapsed };

        var grid = new GridView
        {
            SelectionMode = ListViewSelectionMode.Multiple, IsMultiSelectCheckBoxEnabled = true, Height = 360,
            Visibility = Visibility.Collapsed,
        };
        AutomationProperties.SetName(grid, Tr("Photos found"));
        var chosenText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = ThemeBrush("TextFillColorSecondaryBrush") };
        var all = new HyperlinkButton { Content = Tr("Select all") };
        var none = new HyperlinkButton { Content = Tr("Select none") };
        var selectRow = new StackPanel { Orientation = Microsoft.UI.Xaml.Controls.Orientation.Horizontal, Spacing = 4, Visibility = Visibility.Collapsed, Children = { all, none, chosenText } };

        var folderName = new TextBox { Header = Tr("Folder name"), PlaceholderText = Tr("e.g. Lisbon") };
        var destination = new TextBox { Header = Tr("Into"), Text = ViewModel.ImportFolder, IsReadOnly = true, MinWidth = 320 };
        var browse = new Button { Content = Tr("Change…"), VerticalAlignment = VerticalAlignment.Bottom };
        var into = new Grid { ColumnSpacing = 8, ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
        Grid.SetColumn(browse, 1);
        into.Children.Add(destination);
        into.Children.Add(browse);
        var example = new TextBlock { Foreground = ThemeBrush("TextFillColorSecondaryBrush"), TextWrapping = TextWrapping.Wrap };
        var chosenTags = new List<string>();
        var tags = TagPicker(Tr("Tags for all of them (optional)"), chosenTags);
        var where = new StackPanel { Spacing = 12, Visibility = Visibility.Collapsed, Children = { folderName, into, example, tags } };

        WebSiteClient? client = null;
        CancellationTokenSource? scanning = null;
        List<WebPhoto> photos = [];
        ContentDialog? dialog = null;

        string Target() => Path.Combine(destination.Text, CleanFolderName(folderName.Text));

        void Update()
        {
            var chosen = grid.SelectedItems.Count;
            chosenText.Text = Tr("{0:N0} of {1:N0} chosen", chosen, grid.Items.Count);
            example.Text = Tr("The photos go into {0}", Target());
            find.IsEnabled = allowed.IsChecked == true && TryAddress(address.Text, out _) && scanning is null;
            if (dialog is not null)
                dialog.IsPrimaryButtonEnabled = chosen > 0 && scanning is null && allowed.IsChecked == true;
        }

        async Task ScanAsync()
        {
            if (!TryAddress(address.Text, out var start) || allowed.IsChecked != true)
                return;
            scanning?.Cancel();
            _previewLoading?.Cancel();
            scanning = new CancellationTokenSource();
            var cancel = scanning.Token;
            client?.Dispose();
            var site = client = new WebSiteClient(start);
            photos = [];
            grid.Items.Clear();
            grid.Visibility = selectRow.Visibility = where.Visibility = Visibility.Collapsed;
            progress.Visibility = stop.Visibility = Visibility.Visible;
            status.Text = Tr("Reading {0}…", start.Host);
            Update();
            WebScanResult result;
            try
            {
                var reported = new Progress<WebScanProgress>(p =>
                    status.Text = Tr("Reading the site… {0:N0} pages read, {1:N0} photos found so far", p.PagesRead, p.PhotosFound));
                result = await Task.Run(() => WebGalleryScanner.ScanAsync(site, start, reported, cancel));
            }
            catch (Exception ex)
            {
                App.Trace($"web import scan {start}: {ex}");
                if (scanning?.Token == cancel)
                {
                    scanning = null;
                    status.Text = ex is OperationCanceledException
                        ? Tr("Stopped before any photos were found.")
                        : Tr("The page could not be read. Check the address and your internet connection.");
                    Update();
                }
                return;
            }
            finally
            {
                progress.Visibility = stop.Visibility = Visibility.Collapsed;
            }
            if (scanning?.Token != cancel)
                return;
            scanning = null;
            photos = result.Photos.ToList();
            status.Text = Describe(result);
            App.Trace($"web import scan {start.Host}: {result.Photos.Count} photos, {result.PagesRead} pages, {result.LinksFailed} failed, {result.OtherSiteLinks} other sites, {result.BlockedByRobots} robots");
            if (photos.Count > 0)
            {
                if (string.IsNullOrWhiteSpace(folderName.Text))
                    folderName.Text = CleanFolderName(result.Title ?? start.Host);
                foreach (var photo in photos)
                    grid.Items.Add(Tile(photo));
                grid.SelectAll();
                grid.Visibility = selectRow.Visibility = where.Visibility = Visibility.Visible;
            }
            Update();
            if (photos.Count > 0)
                await LoadPreviewsAsync(site, grid);
        }

        address.TextChanged += (_, _) => Update();
        address.KeyDown += async (_, args) =>
        {
            if (args.Key == VirtualKey.Enter && find.IsEnabled)
            {
                args.Handled = true;
                await ScanAsync();
            }
        };
        allowed.Click += (_, _) => Update();
        find.Click += async (_, _) => await ScanAsync();
        stop.Click += (_, _) => scanning?.Cancel();
        grid.SelectionChanged += (_, _) => Update();
        all.Click += (_, _) => grid.SelectAll();
        none.Click += (_, _) => grid.SelectedItems.Clear();
        folderName.TextChanged += (_, _) => Update();
        browse.Click += async (_, _) =>
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);
            if (await picker.PickSingleFolderAsync() is { } folder)
            {
                destination.Text = folder.Path;
                Update();
            }
        };

        dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = Tr("Import from a website"),
            Content = new ScrollViewer
            {
                Content = new StackPanel
                {
                    Spacing = 12,
                    Children = { notice, allowed, addressRow, progress, status, selectRow, grid, where },
                },
            },
            PrimaryButtonText = Tr("Download"),
            CloseButtonText = Tr("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
        };
        dialog.Resources["ContentDialogMaxWidth"] = 960.0;
        Update();
        var answer = await dialog.ShowThemedAsync();
        // A scan or the previews still loading stop with the dialog.
        scanning?.Cancel();
        _previewLoading?.Cancel();
        var chosen = grid.SelectedItems.OfType<FrameworkElement>().Select(t => t.Tag).OfType<WebPhoto>().ToList();
        if (answer != ContentDialogResult.Primary || chosen.Count == 0 || client is null)
        {
            client?.Dispose();
            return;
        }
        ViewModel.ImportFolder = destination.Text;
        await ViewModel.WebImportAsync(client, chosen, Target(), chosenTags);
    }

    private CancellationTokenSource? _previewLoading;

    // The grid's pictures, one at a time and politely paced like the scan; a photo whose picture can't be read
    // isn't a photo after all (a counter, a script) and leaves the grid.
    private async Task LoadPreviewsAsync(WebSiteClient client, GridView grid)
    {
        _previewLoading?.Cancel();
        _previewLoading = new CancellationTokenSource();
        var cancel = _previewLoading.Token;
        foreach (var tile in grid.Items.OfType<Grid>().ToList())
        {
            if (cancel.IsCancellationRequested || tile.Tag is not WebPhoto photo)
                return;
            WebFetchResult result;
            try
            {
                result = await Task.Run(() => client.GetAsync(photo.Preview, image: true, cancel));
                if ((!result.Succeeded || WebSiteClient.ImageExtension(result.Body) is null) && photo.Preview != photo.Url)
                    result = await Task.Run(() => client.GetAsync(photo.Url, image: true, cancel));
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            Microsoft.UI.Xaml.Media.ImageSource? source = null;
            if (result.Succeeded && WebSiteClient.ImageExtension(result.Body) is not null)
            {
                try
                {
                    source = await Imaging.ImageLoader.FromEncodedAsync(result.Body!, 240);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    App.Trace($"web import preview {photo.Preview}: {ex.Message}");
                }
            }
            if (cancel.IsCancellationRequested)
                return;
            if (source is null)
            {
                grid.Items.Remove(tile);
                continue;
            }
            if (tile.Children[0] is Image image)
                image.Source = source;
            if (tile.Children[1] is ProgressRing ring)
                ring.Visibility = Visibility.Collapsed;
        }
    }

    // One photo in the grid: its picture (once loaded), its file name and the size the page gives.
    private static Grid Tile(WebPhoto photo)
    {
        var tile = new Grid
        {
            Width = 168, Height = 156, Tag = photo, Padding = new Thickness(4),
            RowDefinitions = { new RowDefinition(), new RowDefinition { Height = GridLength.Auto } },
        };
        tile.Children.Add(new Image { Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform });
        tile.Children.Add(new ProgressRing { IsActive = true, Width = 20, Height = 20 });
        var name = new TextBlock
        {
            Text = photo.Width is { } w && photo.Height is { } h ? $"{photo.Name} · {w} × {h}" : photo.Name,
            FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 4, 0, 0),
        };
        Grid.SetRow(name, 1);
        tile.Children.Add(name);
        ToolTipService.SetToolTip(tile, photo.Url.AbsoluteUri);
        AutomationProperties.SetName(tile, photo.Name);
        return tile;
    }

    private static string Describe(WebScanResult result)
    {
        if (result.StartFailure != WebFetchFailure.None)
        {
            return result.StartFailure switch
            {
                WebFetchFailure.OtherSite => Tr("This address sends the app on to another site, which it doesn’t follow. Enter the address the page ends up at in your browser."),
                WebFetchFailure.Robots => Tr("This site asks apps like this one not to read the page (in its robots.txt), so the app leaves it alone."),
                WebFetchFailure.TooLarge => Tr("The page is too large to read."),
                _ => Tr("The page could not be read. Check the address and your internet connection."),
            };
        }
        if (result.Photos.Count == 0)
            return result.Stopped
                ? Tr("Stopped before any photos were found.")
                : Tr("No photos were found on this page. Sites that build their gallery with scripts, or that need a sign-in, can’t be read.");
        var text = Tr("{0:N0} photos found on {1:N0} pages. Tick the ones to download.", result.Photos.Count, result.PagesRead);
        if (result.Stopped)
            text += " " + Tr("Stopped early: there may be more.");
        else if (result.PageLimitReached)
            text += " " + Tr("The app stopped after {0:N0} pages: there may be more.", result.PagesRead);
        if (result.LinksFailed > 0)
            text += " " + Tr("{0:N0} links could not be read.", result.LinksFailed);
        if (result.BlockedByRobots > 0)
            text += " " + Tr("{0:N0} pages are closed to apps by the site and were left out.", result.BlockedByRobots);
        if (result.OtherSiteLinks > 0)
            text += " " + Tr("{0:N0} links to other sites were left out.", result.OtherSiteLinks);
        return text;
    }

    // An address typed without http:// gets https://.
    private static bool TryAddress(string text, out Uri address)
    {
        text = text.Trim();
        if (text.Length > 0 && !text.Contains("://", StringComparison.Ordinal))
            text = "https://" + text;
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.Host.Contains('.'))
        {
            address = uri;
            return true;
        }
        address = null!;
        return false;
    }

    // A page title as a folder name: characters Windows doesn't allow left out, not too long.
    private static string CleanFolderName(string text)
    {
        var name = Regex.Replace(text, @"[\\/:*?""<>|\x00-\x1F]", " ");
        name = Regex.Replace(name, @"\s+", " ").Trim().TrimEnd('.');
        return name.Length > 80 ? name[..80].Trim() : name;
    }
}
