using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PhotoGallery.Core.Import;
using PhotoGallery.Core.Library;
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

        // How fast the app reads the site: slower for sites that start asking whether you're human.
        var pace = new ComboBox
        {
            Header = Tr("Pace"), MinWidth = 320,
            ItemsSource = new[] { Tr("Normal"), Tr("Slow: a page every 2 seconds"), Tr("Very slow: a page every 5 seconds") },
            SelectedIndex = ViewModel.WebImportPace,
        };
        pace.SelectionChanged += (_, _) => ViewModel.WebImportPace = Math.Max(0, pace.SelectedIndex);

        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };

        // A sign-in or a human check the site asks for: the user does it on the site's own page, here in the app,
        // and the scan carries on with that sign-in.
        var openPage = new Button { Content = Tr("Open the page") };
        var challengeBar = new InfoBar
        {
            IsOpen = false, IsClosable = false, Severity = InfoBarSeverity.Warning,
            Title = Tr("The site asks for a sign-in or a check that you’re human"),
            Message = Tr("Open the page, sign in or pass the check there yourself, then choose Carry on. A slower pace helps the check not come back."),
            ActionButton = openPage,
        };
        var browser = new WebView2 { Height = 480, MinWidth = 640 };
        AutomationProperties.SetName(browser, Tr("The site’s page"));
        var browserAddress = new TextBlock { Foreground = ThemeBrush("TextFillColorSecondaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var carryOn = new Button { Content = Tr("Carry on"), Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        var closeBrowser = new Button { Content = Tr("Close the page") };
        var browserButtons = new Grid
        {
            ColumnSpacing = 8,
            ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition { Width = GridLength.Auto } },
        };
        Grid.SetColumn(closeBrowser, 1);
        Grid.SetColumn(carryOn, 2);
        browserButtons.Children.Add(browserAddress);
        browserButtons.Children.Add(closeBrowser);
        browserButtons.Children.Add(carryOn);
        var browserPanel = new StackPanel
        {
            Spacing = 8, Visibility = Visibility.Collapsed,
            Children = { new TextBlock { Text = Tr("Sign in or pass the check on the site’s page, then choose Carry on."), TextWrapping = TextWrapping.Wrap }, browser, browserButtons },
        };
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
        // File names: their own, what the page calls them, or numbered in gallery order like the batch rename.
        var naming = new ComboBox
        {
            Header = Tr("File names"), MinWidth = 320,
            ItemsSource = new[] { Tr("Keep the names they have on the site"), Tr("Use the names the page gives them"), Tr("A name and a number, in gallery order") },
            SelectedIndex = 0,
        };
        var numberedName = new TextBox { Header = Tr("Name before the number"), Visibility = Visibility.Collapsed, MinWidth = 320 };
        var namingRow = new StackPanel
        {
            Orientation = Microsoft.UI.Xaml.Controls.Orientation.Horizontal, Spacing = 12,
            Children = { naming, numberedName },
        };
        var chosenTags = new List<string>();
        var tags = TagPicker(Tr("Tags for all of them (optional)"), chosenTags);
        var where = new StackPanel { Spacing = 12, Visibility = Visibility.Collapsed, Children = { folderName, into, namingRow, example, tags } };

        WebSiteClient? client = null;
        // The site's cookies, kept from scan to scan: a sign-in made on the page here goes with every request.
        var cookies = new CookieContainer();
        Uri? challengeAt = null;
        var browserReady = false;
        CancellationTokenSource? scanning = null;
        List<WebPhoto> photos = [];
        IReadOnlySet<Uri> siteImages = new HashSet<Uri>();
        ContentDialog? dialog = null;

        string Target() => Path.Combine(destination.Text, CleanFolderName(folderName.Text));

        WebPhotoNaming Naming() => (WebPhotoNaming)Math.Max(0, naming.SelectedIndex);

        // The chosen photos in gallery order, with the names they get.
        (List<WebPhoto> Photos, IReadOnlyList<string?> Names) Chosen()
        {
            var selected = grid.SelectedItems.OfType<FrameworkElement>().ToHashSet();
            var chosen = grid.Items.OfType<FrameworkElement>().Where(selected.Contains).Select(t => t.Tag).OfType<WebPhoto>().ToList();
            return (chosen, WebPhotoNames.For(chosen, Naming(), numberedName.Text));
        }

        void Update()
        {
            var chosen = grid.SelectedItems.Count;
            chosenText.Text = Tr("{0:N0} of {1:N0} chosen", chosen, grid.Items.Count);
            numberedName.Visibility = Naming() == WebPhotoNaming.Numbered ? Visibility.Visible : Visibility.Collapsed;
            var (chosenPhotos, names) = Chosen();
            example.Text = chosenPhotos.Count == 0
                ? Tr("The photos go into {0}", Target())
                : Tr("The photos go into {0}, for example as {1}", Target(),
                     WebSiteClient.FileNameFor(chosenPhotos[0].Url, null, names[0]));
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
            var site = client = new WebSiteClient(start, WebImportOptions.ForPace(pace.SelectedIndex), cookies: cookies) { Log = App.Trace };
            challengeBar.IsOpen = false;
            browserPanel.Visibility = Visibility.Collapsed;
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
            siteImages = result.SiteImages;
            status.Text = Describe(result);
            App.Trace($"web import scan {start.Host}: {result.Photos.Count} photos, {result.PagesRead} pages, {result.LinksFailed} failed, {result.OtherSiteLinks} other sites, {result.BlockedByRobots} robots{(result.Challenged ? ", challenged" : "")}");
            if (result.Challenged)
            {
                challengeAt = result.ChallengeAddress ?? start;
                challengeBar.IsOpen = true;
            }
            if (photos.Count > 0)
            {
                if (string.IsNullOrWhiteSpace(folderName.Text))
                    folderName.Text = CleanFolderName(result.Title ?? start.Host);
                if (string.IsNullOrWhiteSpace(numberedName.Text))
                    numberedName.Text = folderName.Text;
                foreach (var photo in photos)
                    grid.Items.Add(Tile(photo));
                grid.SelectAll();
                grid.Visibility = selectRow.Visibility = where.Visibility = Visibility.Visible;
            }
            Update();
            if (photos.Count > 0)
                await LoadPreviewsAsync(site, grid, siteImages, at =>
                {
                    challengeAt = at;
                    challengeBar.IsOpen = true;
                });
        }

        // The site's page in the app, with the same browser data as the map (so a sign-in is remembered there).
        async Task OpenPageAsync()
        {
            if (challengeAt is null)
                return;
            try
            {
                // Shown first: the browser starts once it is on screen.
                browserPanel.Visibility = Visibility.Visible;
                if (!browserReady)
                {
                    var environment = await CoreWebView2Environment.CreateWithOptionsAsync(
                        null, Path.Combine(LibrarySettings.DefaultDataFolder, "WebView2"), new CoreWebView2EnvironmentOptions());
                    await browser.EnsureCoreWebView2Async(environment);
                    browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
                    // Links that open a new window stay in this one.
                    browser.CoreWebView2.NewWindowRequested += (_, args) =>
                    {
                        args.Handled = true;
                        if (Uri.TryCreate(args.Uri, UriKind.Absolute, out var to) && to.Scheme is "http" or "https")
                            browser.CoreWebView2.Navigate(to.AbsoluteUri);
                    };
                    browser.CoreWebView2.SourceChanged += (_, _) => browserAddress.Text = browser.CoreWebView2.Source;
                    browserReady = true;
                }
                browser.CoreWebView2.Navigate(challengeAt.AbsoluteUri);
                browser.StartBringIntoView();
            }
            catch (Exception ex)
            {
                App.Trace($"web import browser: {ex}");
                browserPanel.Visibility = Visibility.Collapsed;
                status.Text = Tr("The page could not be opened in the app.");
            }
        }

        // The cookies the page set for this site go to the app's own requests; then the scan starts again.
        async Task CarryOnAsync()
        {
            if (browserReady && client is { } site)
            {
                try
                {
                    var copied = 0;
                    foreach (var cookie in await browser.CoreWebView2.CookieManager.GetCookiesAsync(string.Empty))
                    {
                        if (!site.IsSiteCookieDomain(cookie.Domain))
                            continue;
                        try
                        {
                            cookies.Add(new Cookie(cookie.Name, cookie.Value, cookie.Path, cookie.Domain)
                            {
                                Secure = cookie.IsSecure,
                                HttpOnly = cookie.IsHttpOnly,
                            });
                            copied++;
                        }
                        catch (CookieException)
                        {
                        }
                    }
                    App.Trace($"web import: {copied} cookies from the page");
                }
                catch (Exception ex)
                {
                    App.Trace($"web import cookies: {ex}");
                }
            }
            browserPanel.Visibility = Visibility.Collapsed;
            challengeBar.IsOpen = false;
            await ScanAsync();
        }

        openPage.Click += async (_, _) => await OpenPageAsync();
        carryOn.Click += async (_, _) => await CarryOnAsync();
        closeBrowser.Click += (_, _) => browserPanel.Visibility = Visibility.Collapsed;
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
        naming.SelectionChanged += (_, _) => Update();
        numberedName.TextChanged += (_, _) => Update();
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
                    Children = { notice, allowed, addressRow, pace, progress, status, challengeBar, browserPanel, selectRow, grid, where },
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
        try
        {
            browser.Close();
        }
        catch (Exception ex)
        {
            App.Trace($"web import browser close: {ex.Message}");
        }
        var (chosen, chosenNames) = Chosen();
        if (answer != ContentDialogResult.Primary || chosen.Count == 0 || client is null)
        {
            client?.Dispose();
            return;
        }
        ViewModel.ImportFolder = destination.Text;
        await ViewModel.WebImportAsync(client, chosen, chosenNames, Target(), chosenTags, siteImages);
    }

    private CancellationTokenSource? _previewLoading;

    // The grid's pictures, one at a time and politely paced like the scan. Each photo is read at full size here and
    // kept in the temp folder, so the download needs nothing more from the site; a photo whose picture can't be read
    // isn't a photo after all (a counter, a script) and leaves the grid.
    private async Task LoadPreviewsAsync(WebSiteClient client, GridView grid, IReadOnlySet<Uri> siteImages, Action<Uri>? onChallenge = null)
    {
        _previewLoading?.Cancel();
        _previewLoading = new CancellationTokenSource();
        var cancel = _previewLoading.Token;
        // The same picture twice (a preview also listed on its own, or one address written two ways) is shown once:
        // the one that has a larger photo behind it stays.
        var seen = new Dictionary<string, Grid>(StringComparer.Ordinal);
        foreach (var tile in grid.Items.OfType<Grid>().ToList())
        {
            if (cancel.IsCancellationRequested || tile.Tag is not WebPhoto photo)
                return;
            WebFetchResult result;
            try
            {
                // A photo page the scan didn't get to: its large photo is looked up now.
                if (photo.PhotoPage is not null)
                {
                    var (large, why) = await Task.Run(() => WebGalleryScanner.FindLargePhotoAsync(client, photo, siteImages, cancel));
                    if (why == WebFetchFailure.Challenge)
                    {
                        onChallenge?.Invoke(photo.PhotoPage);
                        return;
                    }
                    tile.Tag = photo = large;
                }
                // The photo itself, at full size: it waits in the temp folder for the download, so the download
                // asks the site for nothing more. The gallery's preview only when the photo can't be had.
                result = await Task.Run(() => client.GetAsync(photo.Url, image: true, cancel));
                if ((!result.Succeeded || WebSiteClient.ImageExtension(result.Body) is null) && photo.Preview != photo.Url &&
                    result.Failure != WebFetchFailure.Challenge)
                    result = await Task.Run(() => client.GetAsync(photo.Preview, image: true, cancel));
                // The site wants a sign-in or a check: the photos stay, without their pictures, rather than leave the grid.
                if (result.Failure == WebFetchFailure.Challenge)
                {
                    onChallenge?.Invoke(result.Address);
                    return;
                }
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
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(result.Body!));
            if (seen.TryGetValue(hash, out var other))
            {
                var keepNew = other.Tag is WebPhoto first && first.Url == first.Preview && photo.Url != photo.Preview;
                grid.Items.Remove(keepNew ? other : tile);
                if (!keepNew)
                    continue;
            }
            seen[hash] = tile;
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
            Text = photo.Width is { } w && photo.Height is { } h ? $"{photo.Caption ?? photo.Name} · {w} × {h}" : photo.Caption ?? photo.Name,
            FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 4, 0, 0),
        };
        Grid.SetRow(name, 1);
        tile.Children.Add(name);
        ToolTipService.SetToolTip(tile, photo.Caption is null ? photo.Url.AbsoluteUri : $"{photo.Caption}{Environment.NewLine}{photo.Url.AbsoluteUri}");
        AutomationProperties.SetName(tile, photo.Name);

        // Right-click: the photo's page or picture in the browser, to see what the site shows.
        WebPhoto Current() => tile.Tag as WebPhoto ?? photo;
        var openPage = new MenuFlyoutItem { Text = Tr("Open the photo’s page in the browser"), Icon = new FontIcon { Glyph = GlobeGlyph } };
        openPage.Click += async (_, _) => await Launcher.LaunchUriAsync(Current().PhotoPage ?? Current().FoundOn);
        var openPicture = new MenuFlyoutItem { Text = Tr("Open the picture in the browser"), Icon = new FontIcon { Glyph = "\uEB9F" } };
        openPicture.Click += async (_, _) => await Launcher.LaunchUriAsync(Current().Url);
        var copy = new MenuFlyoutItem { Text = Tr("Copy the picture’s address"), Icon = new FontIcon { Glyph = "\uE8C8" } };
        copy.Click += (_, _) =>
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(Current().Url.AbsoluteUri);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        };
        tile.ContextFlyout = new MenuFlyout { Items = { openPage, openPicture, copy } };
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
                WebFetchFailure.Challenge => Tr("The site shows a sign-in or a check that you’re human instead of the page."),
                _ => Tr("The page could not be read. Check the address and your internet connection."),
            };
        }
        if (result.Photos.Count == 0 && result.Challenged)
            return Tr("The site asked for a sign-in or a check that you’re human before any photos were found.");
        if (result.Photos.Count == 0)
            return result.Stopped
                ? Tr("Stopped before any photos were found.")
                : Tr("No photos were found on this page. Sites that build their gallery with scripts, or that need a sign-in, can’t be read.");
        var text = Tr("{0:N0} photos found on {1:N0} pages. Tick the ones to download.", result.Photos.Count, result.PagesRead);
        if (result.Challenged)
            text += " " + Tr("The site then asked for a sign-in or a check that you’re human: there may be more.");
        else if (result.Stopped)
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
