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
            var start = new ProcessStartInfo(app.Path) { UseShellExecute = false };
            foreach (var path in ready)
                start.ArgumentList.Add(path);
            await Task.Run(() => Process.Start(start));
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

    /// <summary>The two favourite-app slots in Settings › Editing; <paramref name="chosen"/> gives the result on Save.</summary>
    private StackPanel FavouriteAppsSettings(out Func<List<FavouriteApp>> chosen)
    {
        var apps = ViewModel.FavouriteApps.ToList();
        var rows = new StackPanel { Spacing = 8 };
        var panel = new StackPanel
        {
            Spacing = 8,
            Margin = new Thickness(0, 12, 0, 0),
            Children =
            {
                new TextBlock { Text = "Favourite apps", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] },
                new TextBlock
                {
                    Text = "Up to two apps, such as Affinity Photo or GIMP, get an \u201cEdit in \u2026\u201d button in the toolbar and the viewer.",
                    TextWrapping = TextWrapping.Wrap,
                },
                rows,
            },
        };

        void Show()
        {
            rows.Children.Clear();
            for (var i = 0; i < 2; i++)
            {
                var slot = i;
                var name = new TextBlock
                {
                    Text = slot < apps.Count ? apps[slot].Name : "Not set",
                    VerticalAlignment = VerticalAlignment.Center,
                    Width = 180,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                if (slot < apps.Count)
                    ToolTipService.SetToolTip(name, apps[slot].Path);
                var choose = new Button { Content = slot < apps.Count ? "Change…" : "Choose app…" };
                choose.Click += async (_, _) =>
                {
                    if (await PickAppAsync() is not { } exe)
                        return;
                    var app = new FavouriteApp(exe, AppName(exe));
                    if (slot < apps.Count)
                        apps[slot] = app;
                    else
                        apps.Add(app);
                    Show();
                };
                var row = new StackPanel { Orientation = Microsoft.UI.Xaml.Controls.Orientation.Horizontal, Spacing = 8, Children = { name, choose } };
                if (slot < apps.Count)
                {
                    var remove = new Button { Content = "Remove" };
                    remove.Click += (_, _) =>
                    {
                        apps.RemoveAt(slot);
                        Show();
                    };
                    row.Children.Add(remove);
                }
                rows.Children.Add(row);
                if (slot >= apps.Count)
                    break; // One empty slot at a time.
            }
        }
        Show();
        chosen = () => apps;
        return panel;
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
