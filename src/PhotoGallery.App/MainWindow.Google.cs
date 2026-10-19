using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhotoGallery.App.ViewModels;
using PhotoGallery.Core.GooglePhotos;

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

    private static readonly WizardStep[] GoogleSteps =
    [
        new("Create a Google Cloud project",
            "Google lets apps upload photos only through a \"project\" in Google Cloud. It is free and needs no payment details.\n\n" +
            "Open Google Cloud, sign in with the Google account whose Google Photos should get the photos, give the project a name such as \"Holiday Photo Gallery\", and click Create. " +
            "Wait until it is ready, and make sure it is selected at the top of the page.",
            [("Open Google Cloud: new project", GoogleConsole + "projectcreate")]),
        new("Turn on the Photos Library API",
            "This lets the project talk to Google Photos.\n\nOpen the page below, check that your new project is selected at the top, and click Enable.",
            [("Open the Photos Library API", GoogleConsole + "apis/library/photoslibrary.googleapis.com")]),
        new("Set up the sign-in screen",
            "This is the page Google shows when you sign in from this app.\n\n" +
            "Google Cloud may show German names; they are in brackets.\n\n" +
            "1. Open the sign-in setup and click Get started (Erste Schritte).\n" +
            "2. App information (App-Informationen): app name \"Holiday Photo Gallery\" and your e-mail address. Click Next (Weiter).\n" +
            "3. Audience (Zielgruppe): choose External (Extern). Internal (Intern) only works for company accounts. Click Next.\n" +
            "4. Contact information (Kontaktdaten): your e-mail address. Agree to the policy, click Continue, then Create (Erstellen).\n" +
            "5. Open Audience (Zielgruppe). Under Test users (Testnutzer) click Add users and add your own Google address, and that of anyone else who will use your key. Save.\n\n" +
            "If Google shows no Get started button, the sign-in screen exists already: just do step 5.",
            [("Open the sign-in setup", GoogleConsole + "auth/overview"), ("Open Audience (test users)", GoogleConsole + "auth/audience")]),
        new("Create the app key",
            "1. Open the page below.\n" +
            "2. Application type (Anwendungstyp): Desktop app (Desktop-App). Name: anything, such as \"Holiday Photo Gallery\". Click Create.\n" +
            "3. Google shows a Client ID and a Client secret. Copy both into the boxes below. (Missed them? Click the key in the Clients list to see them again.)",
            [("Open Google Cloud: create a key", GoogleConsole + "auth/clients/create")]),
        new("Sign in",
            "Click Sign in with Google. Your browser opens Google's sign-in page.\n\n" +
            "• Google warns \"Google hasn't verified this app\" (\"Google hat diese App nicht überprüft\"). That's expected for your own key: click Continue (Weiter).\n" +
            "• Allow access to Google Photos, then come back here.\n\n" +
            "Tip: while the project is in testing, Google asks you to sign in again every 7 days. To avoid that, open Audience (Zielgruppe) and click Publish app (App veröffentlichen). " +
            "It stays unverified, which is fine for your own use.",
            [("Open Audience", GoogleConsole + "auth/audience")]),
    ];

    private async void ConnectGoogle_Click(object sender, RoutedEventArgs e) => await ShowSettingsAsync(googleTab: true);

    private async void UploadAlbumToGoogle_Click(object sender, RoutedEventArgs e)
    {
        var folder = TagOf<AlbumViewModel>(sender)?.Folder ?? TagOf<FolderNodeViewModel>(sender)?.FullPath;
        if (folder is not null && await EnsureGoogleAsync())
            await ViewModel.UploadAlbumToGoogleAsync(folder);
    }

    private async void UploadPhotosToGoogle_Click(object sender, RoutedEventArgs e)
    {
        var photos = SelectedPhotos;
        if (photos.Count > 0 && await EnsureGoogleAsync())
            await ViewModel.UploadPhotosToGoogleAsync(photos);
    }

    private void OpenAlbumInGoogle_Click(object sender, RoutedEventArgs e)
    {
        if (TagOf<AlbumViewModel>(sender)?.Album.GoogleUrl is { } url)
            MainViewModel.OpenInBrowser(new Uri(url));
    }

    private void CancelUpload_Click(object sender, RoutedEventArgs e) => ViewModel.CancelUpload();

    /// <summary>Opens the Google tab when nobody is signed in yet; true when uploading can go ahead.</summary>
    private async Task<bool> EnsureGoogleAsync()
    {
        if (ViewModel.IsGoogleSignedIn)
            return true;
        await ShowSettingsAsync(googleTab: true);
        return ViewModel.IsGoogleSignedIn;
    }

    /// <summary>The Google Photos tab. Signing in takes effect at once, not on Save; closing the dialog cancels a sign-in.</summary>
    private FrameworkElement GoogleTab(CancellationToken closed)
    {
        var panel = new StackPanel { Spacing = 12 };
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
            message.Text = "Finish signing in in your browser, then come back here…";
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
            panel.Children.Add(Text("Upload albums and photos to your Google Photos. Each album becomes a Google Photos album of the same name, " +
                "and uploading it again later only sends the new photos. Only you see the uploaded albums, until you share them in Google Photos."));

            if (ViewModel.IsGoogleSignedIn)
            {
                panel.Children.Add(new InfoBar
                {
                    IsOpen = true,
                    IsClosable = false,
                    Severity = InfoBarSeverity.Success,
                    Title = "Connected",
                    Message = ViewModel.GoogleAccount is { } account ? $"Signed in as {account}." : "Signed in to Google Photos.",
                });
                var signOut = new Button { Content = "Sign out" };
                signOut.Click += (_, _) =>
                {
                    ViewModel.SignOutOfGoogle();
                    ShowStatus();
                };
                panel.Children.Add(signOut);
                panel.Children.Add(Text("Right-click an album or photos and choose Upload to Google Photos. Uploaded photos and albums show the " +
                    "Google Photos mark, and the Google Photos filter finds what isn't uploaded yet. Google keeps the uploaded photos " +
                    "if you sign out; to remove them, delete them in Google Photos.", secondary: true));
            }
            else if (MainViewModel.BuiltInGoogleClient is { } builtIn && ViewModel.OwnGoogleClient is null)
            {
                var message = Text("", secondary: true);
                var signIn = new Button { Content = "Sign in with Google", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
                signIn.Click += async (_, _) => await SignInAsync(builtIn, message, signIn);
                panel.Children.Add(signIn);
                panel.Children.Add(message);
            }
            else if (ViewModel.OwnGoogleClient is { } own)
            {
                // Signed out (or the sign-in expired) with the user's own key already set up.
                var message = Text("", secondary: true);
                var signIn = new Button { Content = "Sign in with Google", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
                signIn.Click += async (_, _) => await SignInAsync(own, message, signIn);
                panel.Children.Add(Text("This app uses your own Google Cloud key.", secondary: true));
                panel.Children.Add(signIn);
                panel.Children.Add(message);
            }
            else
            {
                var start = new Button { Content = "Connect to Google Photos…", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
                start.Click += (_, _) => ShowWizard(0);
                panel.Children.Add(Text("This copy of the app has no Google key built in, so it needs a free key of your own from Google. " +
                    "The steps take about 10 minutes, once.", secondary: true));
                panel.Children.Add(start);
            }

            // The key: set up an own one, change it, or go back to the app's.
            if (ViewModel.OwnGoogleClient is { } current)
            {
                panel.Children.Add(Link("Change the Google Cloud key…", () => ShowWizard(3)));
                if (MainViewModel.BuiltInGoogleClient is not null)
                {
                    panel.Children.Add(Link("Use the app's built-in key instead", () =>
                    {
                        ViewModel.SignOutOfGoogle(forgetOwnKey: true);
                        ShowStatus();
                    }));
                }
                panel.Children.Add(Text($"Client ID: {current.ClientId}", secondary: true));
            }
            else if (MainViewModel.BuiltInGoogleClient is not null && !ViewModel.IsGoogleSignedIn)
            {
                panel.Children.Add(Link("Use your own Google Cloud key instead (advanced)…", () => ShowWizard(0)));
            }
        }

        var clientId = new TextBox { Header = "Client ID", PlaceholderText = "…apps.googleusercontent.com", Text = ViewModel.OwnGoogleClient?.ClientId ?? "" };
        var clientSecret = new PasswordBox { Header = "Client secret", PlaceholderText = "GOCSPX-…", Password = ViewModel.OwnGoogleClient?.ClientSecret ?? "" };

        void ShowWizard(int index)
        {
            var step = GoogleSteps[index];
            panel.Children.Clear();
            panel.Children.Add(new TextBlock
            {
                Text = $"Step {index + 1} of {GoogleSteps.Length}",
                Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
            });
            panel.Children.Add(new TextBlock { Text = step.Title, Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"] });
            panel.Children.Add(Text(step.Text));
            foreach (var (label, url) in step.Links)
            {
                panel.Children.Add(Link(label + " ↗", () => MainViewModel.OpenInBrowser(new Uri(url))));
            }

            var message = Text("", secondary: true);
            var back = new Button { Content = index == 0 ? "Cancel" : "Back" };
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
                var next = new Button { Content = "Next", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
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
                var signIn = new Button { Content = "Sign in with Google", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
                var client = new GoogleClient(clientId.Text.Trim(), clientSecret.Password.Trim());
                signIn.Click += async (_, _) => await SignInAsync(client, message, signIn, back);
                buttons.Children.Add(signIn);
            }
            panel.Children.Add(buttons);
            panel.Children.Add(message);
        }

        ShowStatus();
        return new ScrollViewer { Content = panel, MaxHeight = 480, Padding = new Thickness(0, 0, 12, 0) };
    }

    /// <summary>Catches the usual copy-and-paste slips before Google gives a vaguer error.</summary>
    private static string? KeyProblem(string id, string secret)
    {
        id = id.Trim();
        secret = secret.Trim();
        if (id.Length == 0 || secret.Length == 0)
            return "Paste both the Client ID and the Client secret.";
        if (!id.EndsWith(".apps.googleusercontent.com", StringComparison.Ordinal))
            return "The Client ID ends with .apps.googleusercontent.com. Check that all of it was copied.";
        if (id == secret || secret.EndsWith(".apps.googleusercontent.com", StringComparison.Ordinal))
            return "The Client secret is the other value (it usually starts with GOCSPX-).";
        return null;
    }
}
