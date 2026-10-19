using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhotoGallery.App.Imaging;
using PhotoGallery.App.Native;
using PhotoGallery.App.ViewModels;
using PhotoGallery.Core.Library;
using Windows.Storage.Pickers;

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
            ViewModel.ErrorMessage = $"Could not open {photo.FileName} in another app: {ex.Message}";
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
            ViewModel.Warn($"Select the photos to edit in {app.Name} first.");
            return;
        }
        var ready = photos.Where(ViewModel.PrepareForOtherApp).Select(p => p.Path).ToList();
        if (ready.Count == 0)
            return;
        try
        {
            if (IsProgram(app.Path))
            {
                // A desktop program gets all photos at once, so it opens them together.
                var start = new ProcessStartInfo(app.Path) { UseShellExecute = false };
                foreach (var path in ready)
                    start.ArgumentList.Add(path);
                await Task.Run(() => Process.Start(start));
            }
            else
            {
                // A Store app (such as Photos) is opened the way Explorer's "Open with" does, one photo at a time.
                foreach (var path in ready)
                {
                    if (!await AssocHandlers.OpenAsync(app.Path, path))
                    {
                        ViewModel.ErrorMessage = $"Windows no longer offers {app.Name} for {Path.GetExtension(path)} files. Choose the app again in Settings › Editing.";
                        return;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            ViewModel.ErrorMessage = $"Could not start {app.Name}: {ex.Message}. Check the app in Settings › Editing.";
        }
    }

    /// <summary>Shows a button with the app's icon for each favourite app, in the command bar and the viewer.</summary>
    private async Task ShowFavouriteAppsAsync()
    {
        var apps = ViewModel.FavouriteApps;
        AppBarButton[] bar = [FavouriteApp1Button, FavouriteApp2Button];
        Button[] viewer = [ViewerFavourite1Button, ViewerFavourite2Button];
        for (var i = 0; i < bar.Length; i++)
        {
            if (i >= apps.Count)
            {
                bar[i].Visibility = Visibility.Collapsed;
                viewer[i].Visibility = Visibility.Collapsed;
                continue;
            }
            var app = apps[i];
            var tip = $"Open the selected photos in {app.Name}. A copy is kept in .originals first, so Revert still works.";
            bar[i].Label = $"Edit in {app.Name}";
            ToolTipService.SetToolTip(bar[i], tip);
            ToolTipService.SetToolTip(viewer[i], tip);
            bar[i].Visibility = Visibility.Visible;
            viewer[i].Visibility = Visibility.Visible;

            var icon = await AppIconAsync(app.Path);
            bar[i].Icon = icon is null ? new FontIcon { Glyph = "\uE70F" } : new ImageIcon { Source = icon };
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
    }

    private static async Task<Microsoft.UI.Xaml.Media.ImageSource?> AppIconAsync(string exe)
    {
        try
        {
            return File.Exists(exe) ? await ImageLoader.LoadShellThumbnailAsync(exe, 32) : null;
        }
        catch (Exception)
        {
            return null; // The button keeps a pencil icon.
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
            new() { Header = "Favourite app 1", MinWidth = 320, PlaceholderText = "Loading the apps…" },
            new() { Header = "Favourite app 2", MinWidth = 320, PlaceholderText = "Loading the apps…" },
        ];
        var panel = new StackPanel
        {
            Spacing = 8,
            Margin = new Thickness(0, 12, 0, 0),
            Children =
            {
                new TextBlock
                {
                    Text = "Up to two apps get an \u201cEdit in \u2026\u201d button in the toolbar and the viewer. The list shows the apps Windows offers for photos.",
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
        const string None = "None";
        const string Other = "Other app…";
        List<FavouriteApp> offered;
        try
        {
            offered = (await AssocHandlers.ListAsync(".jpg")).Select(a => new FavouriteApp(a.Name, a.DisplayName)).ToList();
        }
        catch (Exception ex)
        {
            offered = []; // Only "Other app…" then.
            ViewModel.Warn($"Couldn't read the apps Windows offers for photos ({ex.Message}). \"Other app…\" still works.");
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
