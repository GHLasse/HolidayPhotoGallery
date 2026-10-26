using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhotoGallery.App.ViewModels;
using PhotoGallery.Core.Updates;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.App;

/// <summary>
/// Updates inside the app (R-2): a quiet check shortly after the start, at most once a day, a blue banner
/// with "What's new" and "Update now", and Settings › App for the channel and a check by hand.
/// </summary>
public sealed partial class MainWindow
{
    private bool _updating;

    /// <summary>Started once the window is up; waits a little, so the check never slows the start down.</summary>
    private async Task CheckForUpdatesLaterAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(15));
        if (!ViewModel.IsUpdateCheckDue)
            return;
        try
        {
            if (await ViewModel.FindUpdateAsync(ViewModel.UpdateChannel) is { } update)
                OfferUpdate(update);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            // No connection is no news: the check runs again on the next start.
            App.Trace($"update check failed: {ex.Message}");
        }
    }

    private void OfferUpdate(AppRelease update) =>
        ViewModel.Offer(Tr("Version {0} is available.", AppUpdates.Format(update.Version)),
        [
            new NoticeAction(Tr("What's new"), () => ShowWhatsNewAsync(update)),
            new NoticeAction(Tr("Update now"), () => UpdateAsync(update)),
        ]);

    private async Task ShowWhatsNewAsync(AppRelease update)
    {
        var notes = AppUpdates.PlainNotes(update.Notes);
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = Tr("What's new in version {0}", AppUpdates.Format(update.Version)),
            Content = new ScrollViewer
            {
                MaxHeight = 420,
                Content = new TextBlock
                {
                    Text = notes.Length > 0 ? notes : Tr("This version has no notes."),
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                },
            },
            PrimaryButtonText = Tr("Update now"),
            CloseButtonText = Tr("Later"),
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowThemedAsync() == ContentDialogResult.Primary)
            await UpdateAsync(update);
    }

    /// <summary>
    /// Downloads the installer, checks it, and hands over to it: it installs quietly over this version and
    /// starts the app again. Settings, the index and the photos stay where they are.
    /// </summary>
    private async Task UpdateAsync(AppRelease update)
    {
        if (_updating)
            return;
        if (!MainViewModel.IsInstalled)
        {
            // Unpacked from the zip: there is no installation to update, so the new zip is on the release page.
            MainViewModel.OpenInBrowser(new Uri(update.PageUrl));
            ViewModel.Inform(Tr("This copy of the app was unpacked from a zip, so it can't update itself. Download version {0} from the page that opened.",
                                AppUpdates.Format(update.Version)));
            return;
        }
        if (IsEditing)
        {
            ViewModel.Warn(Tr("Save or discard the changes to the photo first."));
            return;
        }
        if (ViewModel.IsBusy || ViewModel.IsUploading)
        {
            var doing = string.Join(" ", new[] { ViewModel.IsBusy ? ViewModel.StatusText : "", ViewModel.IsUploading ? ViewModel.UploadText : "" }
                .Where(t => t.Length > 0));
            ViewModel.Warn(Tr("The update waits until the app has finished: {0}", doing));
            return;
        }

        _updating = true;
        using var cancel = new CancellationTokenSource();
        var progress = new ProgressBar { Maximum = 1 };
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = Tr("Updating to version {0}", AppUpdates.Format(update.Version)),
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = Tr("Downloading the new version. The app then closes, installs it and opens again."),
                        TextWrapping = TextWrapping.Wrap,
                    },
                    progress,
                },
            },
            CloseButtonText = Tr("Cancel"),
        };
        dialog.CloseButtonClick += (_, _) => cancel.Cancel();
        _ = dialog.ShowThemedAsync();
        try
        {
            string setup;
            try
            {
                setup = await DownloadAsync(await FreshAsync(update));
            }
            catch (UpdateChangedException ex)
            {
                // The test version was replaced while it downloaded: once more with GitHub's new checksum.
                App.Trace($"update: {ex.Message} Asking GitHub again.");
                progress.Value = 0;
                setup = await DownloadAsync(await FreshAsync(update));
            }

            // A small batch file waits until the app has closed, runs Setup, notes how it went and opens the
            // app again, also when Setup fails (see AppUpdates.HandOverScript).
            var script = Path.Combine(MainViewModel.UpdatesFolder, AppUpdates.ScriptFile);
            File.WriteAllText(script, AppUpdates.HandOverScript(Environment.ProcessId, AppContext.BaseDirectory, Path.GetFileName(Environment.ProcessPath!),
                                                                setup, update.Version, MainViewModel.UpdatesFolder));
            File.WriteAllText(Path.Combine(MainViewModel.UpdatesFolder, AppUpdates.PendingFile), AppUpdates.Format(update.Version));
            App.Trace($"update: handing over to {script} for {setup}");
            Process.Start(new ProcessStartInfo("cmd.exe", $"/d /c \"\"{script}\"\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = MainViewModel.UpdatesFolder,
            });
            dialog.Hide();
            _closeConfirmed = true;
            Close();
        }
        catch (OperationCanceledException)
        {
            dialog.Hide();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            dialog.Hide();
            App.Trace($"update failed: {ex}");
            ViewModel.ErrorMessage = Tr("The update didn't work: {0}", ex.Message);
        }
        finally
        {
            _updating = false;
        }

        // What's on GitHub now: the test version may have been replaced since the check found it.
        async Task<AppRelease> FreshAsync(AppRelease found)
        {
            try
            {
                var now = await ViewModel.FindUpdateAsync(ViewModel.UpdateChannel, cancel.Token);
                return now is not null && now.Version >= found.Version ? now : found;
            }
            catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException)
            {
                return found;
            }
        }

        async Task<string> DownloadAsync(AppRelease release)
        {
            update = release;
            using var http = AppUpdates.CreateClient();
            return await AppUpdates.DownloadSetupAsync(http, release, MainViewModel.UpdatesFolder,
                new Progress<double>(p => progress.Value = p), cancel.Token);
        }
    }

    /// <summary>On the start after an update: says whether it worked, and points at Setup's log when it didn't.</summary>
    private void ReportLastUpdate()
    {
        var stalled = AppUpdates.TakeStalledUpdate(MainViewModel.UpdatesFolder);
        if (AppUpdates.TakeLastResult(MainViewModel.UpdatesFolder) is not { } result)
        {
            if (stalled is { } wanted && MainViewModel.CurrentVersion < wanted)
                ReportStalledUpdate(wanted);
            return;
        }
        var (version, code) = result;
        App.Trace($"update to {AppUpdates.Format(version)}: Setup exit code {code}, running {AppUpdates.Format(MainViewModel.CurrentVersion)}");
        if (code == 0 && MainViewModel.CurrentVersion >= version)
        {
            ViewModel.Inform(Tr("Updated to version {0}.", AppUpdates.Format(version)));
            return;
        }
        var log = Path.Combine(MainViewModel.UpdatesFolder, AppUpdates.LogFile);
        ViewModel.Fail(Tr("The update to version {0} didn't install (Setup ended with code {1}). You can try again in Settings › App.", AppUpdates.Format(version), code),
            File.Exists(log)
                ? [new NoticeAction(Tr("Show Setup's log"), () =>
                    {
                        Process.Start(new ProcessStartInfo(log) { UseShellExecute = true });
                        return Task.CompletedTask;
                    })]
                : []);
    }

    // The hand-over never got as far as Setup (something stopped it, such as security software): say so and
    // offer Setup with its own window, which needs nothing in between.
    private void ReportStalledUpdate(Version version)
    {
        var folder = MainViewModel.UpdatesFolder;
        var setup = Path.Combine(folder, $"HolidayPhotoGallery-Setup-{AppUpdates.Format(version)}.exe");
        var steps = Path.Combine(folder, AppUpdates.StepsFile);
        App.Trace($"update to {AppUpdates.Format(version)} didn't run; steps noted: {(File.Exists(steps) ? File.ReadAllText(steps).Trim() : "none")}");
        var actions = new List<NoticeAction>();
        if (File.Exists(setup))
            actions.Add(new NoticeAction(Tr("Run Setup"), () =>
            {
                _closeConfirmed = true;
                Process.Start(new ProcessStartInfo(setup) { UseShellExecute = true });
                Close();
                return Task.CompletedTask;
            }));
        if (File.Exists(steps))
            actions.Add(new NoticeAction(Tr("Show what happened"), () =>
            {
                Process.Start(new ProcessStartInfo(steps) { UseShellExecute = true });
                return Task.CompletedTask;
            }, Closes: false));
        ViewModel.Fail(Tr("The update to version {0} didn't run after the app closed. Something on this PC may have stopped it. Run Setup to install it yourself.", AppUpdates.Format(version)), actions);
    }

    /// <summary>
    /// The Updates part of Settings › App. <paramref name="updateNow"/> is called with a version found by
    /// "Look for a new version now" when the user wants it installed; Settings then closes.
    /// </summary>
    private StackPanel UpdatesSettings(Action<AppRelease> updateNow, out Func<(string Channel, bool Check)> chosen)
    {
        var channel = new ComboBox { Header = Tr("Versions to update to"), MinWidth = 320 };
        foreach (var (_, name) in MainViewModel.UpdateChannels)
            channel.Items.Add(name);
        channel.SelectedIndex = Array.FindIndex(MainViewModel.UpdateChannels, c => c.Key == ViewModel.UpdateChannel);
        var automatic = new ToggleSwitch
        {
            OnContent = Tr("On"),
            OffContent = Tr("Off"),
            Header = Tr("Look for a new version by itself (once a day)"),
            IsOn = ViewModel.CheckForUpdates,
        };
        var result = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        var install = new Button { Content = Tr("Update now"), Visibility = Visibility.Collapsed, Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        var check = new Button { Content = Tr("Look for a new version now") };
        AppRelease? found = null;
        string ChosenChannel() => MainViewModel.UpdateChannels[Math.Max(0, channel.SelectedIndex)].Key;
        check.Click += async (_, _) =>
        {
            check.IsEnabled = false;
            install.Visibility = Visibility.Collapsed;
            result.Text = Tr("Looking…");
            try
            {
                found = await ViewModel.FindUpdateAsync(ChosenChannel());
                result.Text = found is null
                    ? Tr("You have the newest version.")
                    : Tr("Version {0} is available.", AppUpdates.Format(found.Version));
                install.Visibility = found is null ? Visibility.Collapsed : Visibility.Visible;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                result.Text = Tr("GitHub could not be reached: {0}", ex.Message);
            }
            finally
            {
                check.IsEnabled = true;
            }
        };
        install.Click += (_, _) =>
        {
            if (found is not null)
                updateNow(found);
        };
        chosen = () => (ChosenChannel(), automatic.IsOn);
        return new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = Tr("Updates"), Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] },
                new TextBlock { Text = Tr("This is version {0}.", AppUpdates.Format(MainViewModel.CurrentVersion)) },
                channel,
                new TextBlock
                {
                    Text = Tr("Test versions have the newest changes before they are released. Going back to Stable offers the newest released version, even if it is older."),
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
                },
                automatic,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { check, install } },
                result,
            },
        };
    }
}
