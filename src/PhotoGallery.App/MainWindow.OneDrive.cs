using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhotoGallery.App.ViewModels;
using PhotoGallery.Core.Cloud;
using PhotoGallery.Core.OneDrive;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.App;

/// <summary>
/// OneDrive: the Settings tab with its connection wizard, and the upload commands. With an app
/// registration built into the app the tab is just "Sign in with Microsoft".
/// </summary>
public sealed partial class MainWindow
{
    private const string AppRegistrations = "https://portal.azure.com/#view/Microsoft_AAD_RegisteredApps/ApplicationsListBlade";

    private static WizardStep[] OneDriveSteps =>
    [
        new(Tr("Register the app with Microsoft"),
            Tr("Microsoft lets apps use OneDrive once they are registered. It is free. The Azure portal may show German names; they are in brackets.") + "\n\n" +
            Tr("1. Open the Azure portal and sign in with the Microsoft account whose OneDrive should get the photos. If Microsoft asks you to set up a free account or directory first, follow its steps.") + "\n" +
            Tr("2. Click New registration (Neue Registrierung).") + "\n" +
            Tr("3. Name: \"Holiday Photo Gallery\".") + "\n" +
            Tr("4. Supported account types (Unterstützte Kontotypen): Personal Microsoft accounts only (Nur persönliche Microsoft-Konten).") + "\n" +
            Tr("5. Redirect URI (Umleitungs-URI): choose Public client/native (mobile & desktop) (Öffentlicher Client/nativ) and type http://localhost") + "\n" +
            Tr("6. Click Register (Registrieren)."),
            [(Tr("Open the Azure portal: app registrations"), AppRegistrations)]),
        new(Tr("Copy the app's ID"),
            Tr("The app's Overview (Übersicht) page shows the Application (client) ID (Anwendungs-ID (Client)), a code like 0a1b2c3d-…. Copy it into the box below. Microsoft needs no secret for desktop apps."),
            []),
        new(Tr("Sign in"),
            Tr("Click Sign in with Microsoft. Your browser opens Microsoft's sign-in page. Allow the app to use your OneDrive files, then come back here."),
            []),
    ];

    private async void UploadAlbumToOneDrive_Click(object sender, RoutedEventArgs e) => await UploadAlbumAsync(CloudService.OneDrive, sender);

    private async void UploadPhotosToOneDrive_Click(object sender, RoutedEventArgs e) => await UploadPhotosAsync(CloudService.OneDrive);

    /// <summary>What the user picked in <see cref="ChooseOneDriveAlbumAsync"/>: an album they have, or null for the first choice.</summary>
    private sealed record OneDriveAlbumChoice(OneDriveAlbum? Album);

    /// <summary>
    /// Asks which of the user's OneDrive albums the photos go into. The first choice is
    /// <paramref name="firstChoice"/> (a new album, or each photo's own); an album named
    /// <paramref name="preselect"/> is picked to start with. Null when cancelled or the albums can't be read.
    /// </summary>
    private async Task<OneDriveAlbumChoice?> ChooseOneDriveAlbumAsync(string title, string firstChoice, string? preselect)
    {
        if (await ViewModel.ListOneDriveAlbumsAsync() is not { } albums)
            return null;
        var list = new ComboBox { Header = Tr("OneDrive album"), HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 320 };
        list.Items.Add(firstChoice);
        foreach (var album in albums)
            list.Items.Add(album.PhotoCount == 1 ? Tr("{0} (1 photo)", album.Name) : Tr("{0} ({1:N0} photos)", album.Name, album.PhotoCount));
        var same = preselect is null ? -1 : albums.ToList().FindIndex(a => string.Equals(a.Name, preselect, StringComparison.CurrentCultureIgnoreCase));
        list.SelectedIndex = same + 1;
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = title,
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    list,
                    new TextBlock
                    {
                        Text = Tr("The photos are stored in Pictures › {0} in your OneDrive and shown in the album you pick.", OneDriveClient.AppFolder) + " " +
                            Tr("Albums made on your phone or on the OneDrive website are listed too."),
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
                    },
                },
            },
            PrimaryButtonText = Tr("Upload"),
            CloseButtonText = Tr("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowThemedAsync() != ContentDialogResult.Primary)
            return null;
        return new OneDriveAlbumChoice(list.SelectedIndex > 0 ? albums[list.SelectedIndex - 1] : null);
    }

    private void OpenAlbumInOneDrive_Click(object sender, RoutedEventArgs e) => OpenInBrowser(TagOf<AlbumViewModel>(sender)?.Album.OneDriveUrl);

    /// <summary>The OneDrive part of the Linked apps tab. Signing in takes effect at once, not on Save; closing the dialog cancels a sign-in.</summary>
    private FrameworkElement OneDriveSection(CancellationToken closed)
    {
        var panel = new StackPanel { Spacing = 12 };
        var signInChecked = false;
        CancellationTokenSource? signingIn = null;
        closed.Register(() => signingIn?.Cancel());
        var clientId = new TextBox { Header = Tr("Application (client) ID"), PlaceholderText = "0a1b2c3d-…", Text = ViewModel.OwnOneDriveClientId ?? "" };

        TextBlock Text(string text, bool secondary = false)
        {
            var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
            if (secondary)
                block.Foreground = ThemeBrush("TextFillColorSecondaryBrush");
            return block;
        }

        HyperlinkButton Link(string label, Action click)
        {
            var link = new HyperlinkButton { Content = label, Padding = new Thickness(0) };
            link.Click += (_, _) => click();
            return link;
        }

        Button Accent(string label) => new() { Content = label, Style = (Style)Application.Current.Resources["AccentButtonStyle"] };

        async Task SignInAsync(string id, TextBlock message, params Control[] disable)
        {
            signingIn = new CancellationTokenSource();
            foreach (var control in disable)
                control.IsEnabled = false;
            message.Text = Tr("Finish signing in in your browser, then come back here…");
            message.Foreground = ThemeBrush("TextFillColorSecondaryBrush");
            var error = await ViewModel.SignInToOneDriveAsync(id, signingIn.Token);
            signingIn = null;
            foreach (var control in disable)
                control.IsEnabled = true;
            if (error is null)
                ShowStatus();
            else if (!closed.IsCancellationRequested)
            {
                message.Text = error;
                message.Foreground = ThemeBrush("SystemFillColorCriticalBrush");
            }
        }

        async Task CheckAsync()
        {
            if (await ViewModel.CheckSignInAsync(CloudService.OneDrive) && !closed.IsCancellationRequested)
                ShowStatus();
        }

        void ShowStatus()
        {
            panel.Children.Clear();
            panel.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new Controls.OneDriveLogo { Width = 24, Height = 24 },
                    new TextBlock { Text = "OneDrive", Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"] },
                },
            });
            panel.Children.Add(Text(Tr("Upload albums and photos to your OneDrive. They go into Pictures › {0} › album name, and each album goes into a OneDrive album: one you already have, or a new one. Uploading again later only sends the new photos. Photos already in your OneDrive folder on this PC aren't uploaded twice: the app puts the copy OneDrive has into the album.", Core.OneDrive.OneDriveClient.AppFolder)));

            if (ViewModel.SignInExpired(CloudService.OneDrive))
            {
                panel.Children.Add(new InfoBar
                {
                    IsOpen = true,
                    IsClosable = false,
                    Severity = InfoBarSeverity.Warning,
                    Title = Tr("Sign-in expired"),
                    Message = Tr("{0} no longer accepts the saved sign-in: it ran out or was withdrawn. Sign in again, and album renames that didn't go through are sent then.", "Microsoft"),
                });
            }
            if (ViewModel.IsOneDriveSignedIn)
            {
                // The saved sign-in can have run out without the app noticing yet: ask once, and show the truth.
                if (!signInChecked)
                {
                    signInChecked = true;
                    _ = CheckAsync();
                }
                panel.Children.Add(new InfoBar
                {
                    IsOpen = true,
                    IsClosable = false,
                    Severity = InfoBarSeverity.Success,
                    Title = Tr("Connected"),
                    Message = ViewModel.OneDriveAccount is { } account ? Tr("Signed in as {0}.", account) : Tr("Signed in to OneDrive."),
                });
                var signOut = new Button { Content = Tr("Sign out") };
                signOut.Click += (_, _) =>
                {
                    ViewModel.SignOutOfOneDrive();
                    ShowStatus();
                };
                panel.Children.Add(signOut);
                panel.Children.Add(Text(Tr("Right-click an album or photos and choose Upload to OneDrive. Photos in OneDrive show the cloud mark, and the Cloud filter finds what isn't there yet. OneDrive keeps the photos if you sign out."), secondary: true));
            }
            else if (ViewModel.OneDriveClientId is { } id)
            {
                var message = Text("", secondary: true);
                var signIn = Accent(Tr("Sign in with Microsoft"));
                signIn.Click += async (_, _) => await SignInAsync(id, message, signIn);
                panel.Children.Add(signIn);
                panel.Children.Add(message);
            }
            else
            {
                var start = Accent(Tr("Connect to OneDrive…"));
                start.Click += (_, _) => ShowWizard(0);
                panel.Children.Add(Text(Tr("This copy of the app has no Microsoft app registration built in, so it needs one of your own. It's free and takes about 5 minutes, once."), secondary: true));
                panel.Children.Add(start);
            }

            if (ViewModel.OwnOneDriveClientId is { } own)
            {
                panel.Children.Add(Link(Tr("Change the app registration…"), () => ShowWizard(1)));
                if (MainViewModel.BuiltInOneDriveClientId is not null)
                {
                    panel.Children.Add(Link(Tr("Use the app's built-in registration instead"), () =>
                    {
                        ViewModel.SignOutOfOneDrive(forgetOwnKey: true);
                        ShowStatus();
                    }));
                }
                panel.Children.Add(Text(Tr("Application ID: {0}", own), secondary: true));
            }
            else if (MainViewModel.BuiltInOneDriveClientId is not null && !ViewModel.IsOneDriveSignedIn)
            {
                panel.Children.Add(Link(Tr("Use your own app registration instead (advanced)…"), () => ShowWizard(0)));
            }
        }

        void ShowWizard(int index)
        {
            var step = OneDriveSteps[index];
            panel.Children.Clear();
            panel.Children.Add(new TextBlock { Text = Tr("Step {0} of {1}", index + 1, OneDriveSteps.Length), Foreground = ThemeBrush("TextFillColorSecondaryBrush") });
            panel.Children.Add(new TextBlock { Text = step.Title, Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"] });
            panel.Children.Add(Text(step.Text));
            foreach (var (label, url) in step.Links)
                panel.Children.Add(Link(label + " ↗", () => MainViewModel.OpenInBrowser(new Uri(url))));
            if (index == 1)
                panel.Children.Add(clientId);

            var message = Text("", secondary: true);
            var back = new Button { Content = index == 0 ? Tr("Cancel") : Tr("Back") };
            back.Click += (_, _) =>
            {
                if (index == 0)
                    ShowStatus();
                else
                    ShowWizard(index - 1);
            };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 0), Children = { back } };
            if (index < OneDriveSteps.Length - 1)
            {
                var next = Accent(Tr("Next"));
                next.Click += (_, _) =>
                {
                    if (index == 1 && !Guid.TryParse(clientId.Text.Trim(), out _))
                    {
                        message.Text = Tr("The Application (client) ID looks like 0a1b2c3d-4e5f-6a7b-8c9d-0e1f2a3b4c5d. Check that all of it was copied.");
                        message.Foreground = ThemeBrush("SystemFillColorCriticalBrush");
                        return;
                    }
                    ShowWizard(index + 1);
                };
                buttons.Children.Add(next);
            }
            else
            {
                var signIn = Accent(Tr("Sign in with Microsoft"));
                var id = clientId.Text.Trim();
                signIn.Click += async (_, _) => await SignInAsync(id, message, signIn, back);
                buttons.Children.Add(signIn);
            }
            panel.Children.Add(buttons);
            panel.Children.Add(message);
        }

        ShowStatus();
        return panel;
    }
}
