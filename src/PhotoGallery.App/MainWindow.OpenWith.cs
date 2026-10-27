using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhotoGallery.App.Imaging;
using PhotoGallery.App.Native;
using PhotoGallery.App.ViewModels;
using PhotoGallery.Core.Library;
using Windows.Storage.Pickers;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.App;

/// <summary>Opening a photo in another app with Windows' "Open with" dialog, e.g. to edit it in Affinity or GIMP.</summary>
public sealed partial class MainWindow
{
    private async void OpenWith_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPhotos is [var photo])
            await OpenWithAsync(photo);
    }

    private async void ViewerOpenWith_Click(object sender, RoutedEventArgs e)
    {
        if (ViewerItem is { } photo && !IsEditing)
            await OpenWithAsync(photo);
    }

    private void ShowInFolderMenu_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPhotos is [var photo])
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{photo.Path}\"") { UseShellExecute = true });
    }

    private async Task OpenWithAsync(PhotoItemViewModel photo)
    {
        if (!ViewModel.PrepareForOtherApp(photo))
            return;
        try
        {
            if (!await OpenWithDialog.ShowAsync(WindowHandle, photo.Path))
                ViewModel.OtherAppCancelled(photo);
        }
        catch (Exception ex)
        {
            ViewModel.OtherAppCancelled(photo);
            ViewModel.ErrorMessage = Tr("Could not open {0} in another app: {1}", photo.FileName, ex.Message);
        }
    }

    // Another app saved the photo: redraw it if the viewer shows it, and refresh the info pane.
    private void OnPhotoChangedOutside(PhotoItemViewModel photo)
    {
        if (ViewerItem == photo && !IsEditing)
        {
            _ = ShowViewerImageAsync();
            UpdateUndoButtons();
        }
        if (SelectedPhotos.Contains(photo) && !ViewModel.Editor.IsDirty)
            ViewModel.Editor.Load(SelectedPhotos);
    }

    // ---- Favourite apps: "Edit in …" buttons ----

    private async void FavouriteApp_Click(object sender, RoutedEventArgs e)
    {
        if (FavouriteFor(sender) is { } app)
            await EditInAsync(app, SelectedPhotos);
    }

    private async void ViewerFavouriteApp_Click(object sender, RoutedEventArgs e)
    {
        if (FavouriteFor(sender) is { } app && ViewerItem is { } photo && !IsEditing)
            await EditInAsync(app, [photo]);
    }

    private FavouriteApp? FavouriteFor(object sender) =>
        (sender as FrameworkElement)?.Tag is string tag && int.TryParse(tag, out var i) && i < ViewModel.FavouriteApps.Count
            ? ViewModel.FavouriteApps[i]
            : null;

    private async Task EditInAsync(FavouriteApp app, IReadOnlyList<PhotoItemViewModel> photos)
    {
        if (photos.Count == 0)
        {
            ViewModel.Warn(Tr("Select the photos to edit in {0} first.", app.Name));
            return;
        }
        var ready = photos.Where(ViewModel.PrepareForOtherApp).Select(p => p.Path).ToList();
        if (ready.Count == 0)
            return;
        try
        {
            if (IsProgram(app.Path))
            {
                // A desktop program gets all photos at once, so it opens them together, and stays open when
                // this app closes.
                await Task.Run(() => Native.ChildProcessGuard.StartDetached(app.Path, ready));
            }
            else
            {
                // A Store app (such as Photos) is opened the way Explorer's "Open with" does, one photo at a time.
                foreach (var path in ready)
                {
                    if (!await AssocHandlers.OpenAsync(app.Path, path))
                    {
                        ViewModel.ErrorMessage = Tr("Windows no longer offers {0} for {1} files. Choose the app again in Settings › Editing.", app.Name, Path.GetExtension(path));
                        return;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            ViewModel.ErrorMessage = Tr("Could not start {0}: {1}. Check the app in Settings › Editing.", app.Name, ex.Message);
        }
    }

    // The favourite apps' icons, for their items in the Send menus (MainWindow.Commands.cs).
    private readonly List<Microsoft.UI.Xaml.Media.ImageSource?> _favouriteIcons = [];

    /// <summary>Shows a button with the app's icon for each favourite app in the viewer; the Send menus list them too.</summary>
    private async Task ShowFavouriteAppsAsync()
    {
        var apps = ViewModel.FavouriteApps;
        Button[] viewer = [ViewerFavourite1Button, ViewerFavourite2Button];
        _favouriteIcons.Clear();
        for (var i = 0; i < viewer.Length; i++)
        {
            if (i >= apps.Count)
            {
                viewer[i].Visibility = Visibility.Collapsed;
                continue;
            }
            var app = apps[i];
            ToolTipService.SetToolTip(viewer[i], Tr("Open the selected photos in {0}. A copy is kept in .originals first, so Back to original still works.", app.Name));
            viewer[i].Visibility = Visibility.Visible;

            var icon = await AppIconAsync(app.Path);
            _favouriteIcons.Add(icon);
            viewer[i].Content = new StackPanel
            {
                Orientation = Microsoft.UI.Xaml.Controls.Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    icon is null ? new FontIcon { Glyph = "\uE70F", FontSize = 16 } : new Image { Source = icon, Width = 16, Height = 16 },
                    new TextBlock { Text = app.Name },
                },
            };
        }
        FitViewerToolbar(); // the app names may not fit beside the other labels
    }

    /// <summary>The icon of a program (its path) or of a Store app (its app id); null keeps the pencil icon.</summary>
    private static async Task<Microsoft.UI.Xaml.Media.ImageSource?> AppIconAsync(string app)
    {
        try
        {
            if (File.Exists(app) && await ImageLoader.LoadShellThumbnailAsync(app, 32) is { } thumbnail)
                return thumbnail;
        }
        catch (Exception)
        {
            // A Store app's program can't be read from its folder; the shell still knows its icon.
        }
        try
        {
            return await AssocHandlers.IconAsync(app, 32) is { } icon ? await ImageLoader.ToImageSourceAsync(icon) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The name an app shows for itself ("Affinity Photo 2"), or its file name.</summary>
    private static string AppName(string exe)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(exe);
            if (!string.IsNullOrWhiteSpace(info.FileDescription))
                return info.FileDescription.Trim();
            if (!string.IsNullOrWhiteSpace(info.ProductName))
                return info.ProductName.Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return Path.GetFileNameWithoutExtension(exe);
    }

    private static bool IsProgram(string path) =>
        path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path);

    /// <summary>
    /// The two favourite-app slots in Settings › Editing: a list of the apps Windows offers for JPEG
    /// photos, plus "Other app…" for a program that isn't listed. <paramref name="chosen"/> gives the result on Save.
    /// </summary>
    private StackPanel FavouriteAppsSettings(out Func<List<FavouriteApp>> chosen)
    {
        var picked = new FavouriteApp?[] { ViewModel.FavouriteApps.ElementAtOrDefault(0), ViewModel.FavouriteApps.ElementAtOrDefault(1) };
        ComboBox[] boxes =
        [
            new() { Header = Tr("Favourite app 1"), MinWidth = 320, PlaceholderText = Tr("Loading the apps…") },
            new() { Header = Tr("Favourite app 2"), MinWidth = 320, PlaceholderText = Tr("Loading the apps…") },
        ];
        var panel = new StackPanel
        {
            Spacing = 8,
            Margin = new Thickness(0, 12, 0, 0),
            Children =
            {
                new TextBlock
                {
                    Text = Tr("Up to two apps get an \u201cEdit in \u2026\u201d button in the toolbar and the viewer. The list shows the apps Windows offers for photos."),
                    TextWrapping = TextWrapping.Wrap,
                },
                boxes[0],
                boxes[1],
            },
        };
        _ = FillFavouriteBoxesAsync(boxes, picked);
        chosen = () => picked.OfType<FavouriteApp>().ToList();
        return panel;
    }

    private async Task FillFavouriteBoxesAsync(ComboBox[] boxes, FavouriteApp?[] picked)
    {
        var None = Tr("None");
        var Other = Tr("Other app…");
        List<FavouriteApp> offered;
        try
        {
            offered = (await AssocHandlers.ListAsync(".jpg")).Select(a => new FavouriteApp(a.Name, a.DisplayName)).ToList();
        }
        catch (Exception ex)
        {
            offered = []; // Only "Other app…" then.
            ViewModel.Warn(Tr("Couldn't read the apps Windows offers for photos ({0}). \"Other app…\" still works.", ex.Message));
        }

        for (var i = 0; i < boxes.Length; i++)
        {
            var slot = i;
            var box = boxes[slot];
            var apps = offered.ToList();
            // A program chosen earlier with "Other app…" stays in the list.
            foreach (var app in picked.OfType<FavouriteApp>())
            {
                if (!apps.Any(a => a.Path.Equals(app.Path, StringComparison.OrdinalIgnoreCase)))
                    apps.Add(app);
            }

            void Fill()
            {
                box.Items.Clear();
                box.Items.Add(None);
                foreach (var app in apps)
                    box.Items.Add(app.Name);
                box.Items.Add(Other);
                var index = picked[slot] is { } current ? apps.FindIndex(a => a.Path.Equals(current.Path, StringComparison.OrdinalIgnoreCase)) : -1;
                box.SelectedIndex = index + 1;
            }

            Fill();
            box.SelectionChanged += async (_, _) =>
            {
                var index = box.SelectedIndex;
                if (index < 0)
                    return;
                if (index == 0)
                    picked[slot] = null;
                else if (index <= apps.Count)
                    picked[slot] = apps[index - 1];
                else
                {
                    // "Other app…": pick a program; cancelling keeps the earlier choice.
                    if (await PickAppAsync() is { } exe)
                    {
                        var app = new FavouriteApp(exe, AppName(exe));
                        if (!apps.Any(a => a.Path.Equals(exe, StringComparison.OrdinalIgnoreCase)))
                            apps.Add(app);
                        picked[slot] = app;
                    }
                    Fill();
                }
            };
        }
    }

    private async Task<string?> PickAppAsync()
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
        picker.FileTypeFilter.Add(".exe");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }
}
