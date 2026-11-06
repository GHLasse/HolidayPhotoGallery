using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhotoGallery.App.ViewModels;
using PhotoGallery.Core.Cloud;
using PhotoGallery.Core.GooglePhotos;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.App;

/// <summary>
/// Google Photos: the Settings tab with the connection wizard, and the upload commands.
/// With a key built into the app the tab is just "Sign in with Google"; the wizard for the user's
/// own Google Cloud key stays available behind a link.
/// </summary>
public sealed partial class MainWindow
{
    private const string GoogleConsole = "https://console.cloud.google.com/";

    private sealed record WizardStep(string Title, string Text, (string Label, string Url)[] Links);

    private static WizardStep[] GoogleSteps =>
    [
        new(Tr("Create a Google Cloud project"),
            Tr("Google lets apps upload photos only through a \"project\" in Google Cloud. It is free and needs no payment details.") + "\n\n" +
            Tr("Open Google Cloud, sign in with the Google account whose Google Photos should get the photos, give the project a name such as \"Holiday Photo Gallery\", and click Create.") + " " +
            Tr("Wait until it is ready, and make sure it is selected at the top of the page."),
            [(Tr("Open Google Cloud: new project"), GoogleConsole + "projectcreate")]),
        new(Tr("Turn on the Photos Library API"),
            Tr("This lets the project talk to Google Photos.") + "\n\n" + Tr("Open the page below, check that your new project is selected at the top, and click Enable."),
            [(Tr("Open the Photos Library API"), GoogleConsole + "apis/library/photoslibrary.googleapis.com")]),
        new(Tr("Set up the sign-in screen"),
            Tr("This is the page Google shows when you sign in from this app.") + "\n\n" +
            Tr("Google Cloud may show German names; they are in brackets.") + "\n\n" +
            Tr("1. Open the sign-in setup and click Get started (Erste Schritte).") + "\n" +
            Tr("2. App information (App-Informationen): app name \"Holiday Photo Gallery\" and your e-mail address. Click Next (Weiter).") + "\n" +
            Tr("3. Audience (Zielgruppe): choose External (Extern). Internal (Intern) only works for company accounts. Click Next.") + "\n" +
            Tr("4. Contact information (Kontaktdaten): your e-mail address. Agree to the policy, click Continue, then Create (Erstellen).") + "\n" +
            Tr("5. Open Audience (Zielgruppe). Under Test users (Testnutzer) click Add users and add your own Google address, and that of anyone else who will use your key. Save.") + "\n\n" +
            Tr("If Google shows no Get started button, the sign-in screen exists already: just do step 5."),
            [(Tr("Open the sign-in setup"), GoogleConsole + "auth/overview"), (Tr("Open Audience (test users)"), GoogleConsole + "auth/audience")]),
        new(Tr("Create the app key"),
            Tr("1. Open the page below.") + "\n" +
            Tr("2. Application type (Anwendungstyp): Desktop app (Desktop-App). Name: anything, such as \"Holiday Photo Gallery\". Click Create.") + "\n" +
            Tr("3. Google shows a Client ID and a Client secret. Copy both into the boxes below. (Missed them? Click the key in the Clients list to see them again.)"),
            [(Tr("Open Google Cloud: create a key"), GoogleConsole + "auth/clients/create")]),
        new(Tr("Sign in"),
            Tr("Click Sign in with Google. Your browser opens Google's sign-in page.") + "\n\n" +
            Tr("• Google warns \"Google hasn't verified this app\" (\"Google hat diese App nicht überprüft\"). That's expected for your own key: click Continue (Weiter).") + "\n" +
            Tr("• Allow access to Google Photos, then come back here.") + "\n\n" +
            Tr("Tip: while the project is in testing, Google asks you to sign in again every 7 days. To avoid that, open Audience (Zielgruppe) and click Publish app (App veröffentlichen).") + " " +
            Tr("It stays unverified, which is fine for your own use."),
            [(Tr("Open Audience"), GoogleConsole + "auth/audience")]),
    ];

    private async void UploadAlbumToGoogle_Click(object sender, RoutedEventArgs e) => await UploadAlbumAsync(CloudService.GooglePhotos, sender);

    private async void UploadPhotosToGoogle_Click(object sender, RoutedEventArgs e) => await UploadPhotosAsync(CloudService.GooglePhotos);

    private void OpenAlbumInGoogle_Click(object sender, RoutedEventArgs e) => OpenInBrowser(TagOf<AlbumViewModel>(sender)?.Album.GoogleUrl);

    /// <summary>The Google Photos part of the Linked apps tab. Signing in takes effect at once, not on Save; closing the dialog cancels a sign-in.</summary>
    private FrameworkElement GoogleSection(CancellationToken closed)
    {
        var panel = new StackPanel { Spacing = 12 };
        var signInChecked = false;
        CancellationTokenSource? signingIn = null;
        closed.Register(() => signingIn?.Cancel());

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

        // Runs the browser sign-in, showing progress and any error in place of the panel's last line.
        async Task SignInAsync(GoogleClient client, TextBlock message, params Control[] disable)
        {
            signingIn = new CancellationTokenSource();
            foreach (var control in disable)
                control.IsEnabled = false;
            message.Text = Tr("Finish signing in in your browser, then come back here…");
            message.Foreground = ThemeBrush("TextFillColorSecondaryBrush");
            var error = await ViewModel.SignInToGoogleAsync(client, signingIn.Token);
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
            if (await ViewModel.CheckSignInAsync(CloudService.GooglePhotos) && !closed.IsCancellationRequested)
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
                    new Controls.GooglePhotosLogo { Width = 24, Height = 24 },
                    new TextBlock { Text = "Google Photos", Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"] },
                },
            });
            panel.Children.Add(Text(Tr("Upload albums and photos to your Google Photos. Each album becomes a Google Photos album of the same name, and uploading it again later only sends the new photos. Only you see the uploaded albums, until you share them in Google Photos.")));

            if (ViewModel.SignInExpired(CloudService.GooglePhotos))
            {
                panel.Children.Add(new InfoBar
                {
                    IsOpen = true,
                    IsClosable = false,
                    Severity = InfoBarSeverity.Warning,
                    Title = Tr("Sign-in expired"),
                    Message = Tr("{0} no longer accepts the saved sign-in: it ran out or was withdrawn. Sign in again, and album renames that didn't go through are sent then.", "Google"),
                });
            }
            if (ViewModel.IsGoogleSignedIn)
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
                    Message = ViewModel.GoogleAccount is { } account ? Tr("Signed in as {0}.", account) : Tr("Signed in to Google Photos."),
                });
                var signOut = new Button { Content = Tr("Sign out") };
                signOut.Click += (_, _) =>
                {
                    ViewModel.SignOutOfGoogle();
                    ShowStatus();
                };
                panel.Children.Add(signOut);
                panel.Children.Add(Text(Tr("Right-click an album or photos and choose Upload to Google Photos. Uploaded photos and albums show the Google Photos mark, and the Cloud filter finds what isn't uploaded yet. Google keeps the uploaded photos if you sign out; to remove them, delete them in Google Photos."), secondary: true));
            }
            else if (MainViewModel.BuiltInGoogleClient is { } builtIn && ViewModel.OwnGoogleClient is null)
            {
                var message = Text("", secondary: true);
                var signIn = new Button { Content = Tr("Sign in with Google"), Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
                signIn.Click += async (_, _) => await SignInAsync(builtIn, message, signIn);
                panel.Children.Add(signIn);
                panel.Children.Add(message);
            }
            else if (ViewModel.OwnGoogleClient is { } own)
            {
                // Signed out (or the sign-in expired) with the user's own key already set up.
                var message = Text("", secondary: true);
                var signIn = new Button { Content = Tr("Sign in with Google"), Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
                signIn.Click += async (_, _) => await SignInAsync(own, message, signIn);
                panel.Children.Add(Text(Tr("This app uses your own Google Cloud key."), secondary: true));
                panel.Children.Add(signIn);
                panel.Children.Add(message);
            }
            else
            {
                var start = new Button { Content = Tr("Connect to Google Photos…"), Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
                start.Click += (_, _) => ShowWizard(0);
                panel.Children.Add(Text(Tr("This copy of the app has no Google key built in, so it needs a free key of your own from Google. The steps take about 10 minutes, once."), secondary: true));
                panel.Children.Add(start);
            }

            // The key: set up an own one, change it, or go back to the app's.
            if (ViewModel.OwnGoogleClient is { } current)
            {
                panel.Children.Add(Link(Tr("Change the Google Cloud key…"), () => ShowWizard(3)));
                if (MainViewModel.BuiltInGoogleClient is not null)
                {
                    panel.Children.Add(Link(Tr("Use the app's built-in key instead"), () =>
                    {
                        ViewModel.SignOutOfGoogle(forgetOwnKey: true);
                        ShowStatus();
                    }));
                }
                panel.Children.Add(Text(Tr("Client ID: {0}", current.ClientId), secondary: true));
            }
            else if (MainViewModel.BuiltInGoogleClient is not null && !ViewModel.IsGoogleSignedIn)
            {
                panel.Children.Add(Link(Tr("Use your own Google Cloud key instead (advanced)…"), () => ShowWizard(0)));
            }
        }

        var clientId = new TextBox { Header = Tr("Client ID"), PlaceholderText = "…apps.googleusercontent.com", Text = ViewModel.OwnGoogleClient?.ClientId ?? "" };
        var clientSecret = new PasswordBox { Header = Tr("Client secret"), PlaceholderText = "GOCSPX-…", Password = ViewModel.OwnGoogleClient?.ClientSecret ?? "" };

        void ShowWizard(int index)
        {
            var step = GoogleSteps[index];
            panel.Children.Clear();
            panel.Children.Add(new TextBlock
            {
                Text = Tr("Step {0} of {1}", index + 1, GoogleSteps.Length),
                Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
            });
            panel.Children.Add(new TextBlock { Text = step.Title, Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"] });
            panel.Children.Add(Text(step.Text));
            foreach (var (label, url) in step.Links)
            {
                panel.Children.Add(Link(label + " ↗", () => MainViewModel.OpenInBrowser(new Uri(url))));
            }

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

            if (index == 3)
            {
                panel.Children.Add(clientId);
                panel.Children.Add(clientSecret);
            }
            if (index < GoogleSteps.Length - 1)
            {
                var next = new Button { Content = Tr("Next"), Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
                next.Click += (_, _) =>
                {
                    if (index == 3 && KeyProblem(clientId.Text, clientSecret.Password) is { } problem)
                    {
                        message.Text = problem;
                        message.Foreground = ThemeBrush("SystemFillColorCriticalBrush");
                        return;
                    }
                    ShowWizard(index + 1);
                };
                buttons.Children.Add(next);
            }
            else
            {
                var signIn = new Button { Content = Tr("Sign in with Google"), Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
                var client = new GoogleClient(clientId.Text.Trim(), clientSecret.Password.Trim());
                signIn.Click += async (_, _) => await SignInAsync(client, message, signIn, back);
                buttons.Children.Add(signIn);
            }
            panel.Children.Add(buttons);
            panel.Children.Add(message);
        }

        ShowStatus();
        return panel;
    }

    /// <summary>Catches the usual copy-and-paste slips before Google gives a vaguer error.</summary>
    private static string? KeyProblem(string id, string secret)
    {
        id = id.Trim();
        secret = secret.Trim();
        if (id.Length == 0 || secret.Length == 0)
            return Tr("Paste both the Client ID and the Client secret.");
        if (!id.EndsWith(".apps.googleusercontent.com", StringComparison.Ordinal))
            return Tr("The Client ID ends with .apps.googleusercontent.com. Check that all of it was copied.");
        if (id == secret || secret.EndsWith(".apps.googleusercontent.com", StringComparison.Ordinal))
            return Tr("The Client secret is the other value (it usually starts with GOCSPX-).");
        return null;
    }
}
