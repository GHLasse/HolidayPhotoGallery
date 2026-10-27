using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using PhotoGallery.App.ViewModels;
using PhotoGallery.Core.Updates;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.App;

/// <summary>
/// Settings › Info: the version, how the app was made, and the work of others it builds on with their licences.
/// </summary>
public sealed partial class MainWindow
{
    private const string RepositoryUrl = "https://github.com/GHLasse/HolidayPhotoGallery";

    /// <summary>
    /// What the app uses, and for what. Names and licence names stay as their makers write them; keep this
    /// list in step with the Credits in README.md when a component comes or goes.
    /// </summary>
    private static (string Name, string Use, string Licence, string Url)[] Credits() =>
    [
        ("ExifTool by Phil Harvey", Tr("Reads and writes the details in the photo files"), "Perl Artistic License / GPL", "https://exiftool.org"),
        (".NET, Windows App SDK (WinUI 3)", Tr("The ground the app is built on"), "MIT", "https://github.com/microsoft/WindowsAppSDK"),
        ("CommunityToolkit.Mvvm", Tr("Links the windows to the app’s data"), "MIT", "https://github.com/CommunityToolkit/dotnet"),
        ("Microsoft.Data.Sqlite, SQLite", Tr("The search index"), "MIT, public domain", "https://www.sqlite.org"),
        ("ImageSharp by Six Labors", Tr("Rotating, cropping and saving pictures"), "Six Labors Split License (Apache 2.0)", "https://github.com/SixLabors/ImageSharp"),
        ("ONNX Runtime, DirectML", Tr("Runs the AI models on this PC"), "MIT", "https://onnxruntime.ai"),
        ("Microsoft.ML.Tokenizers", Tr("Prepares the words for AI tagging"), "MIT", "https://github.com/dotnet/machinelearning"),
        ("SigLIP 2 by Google", Tr("AI tagging"), "Apache 2.0", "https://huggingface.co/google/siglip2-base-patch16-224"),
        ("YuNet, SFace (OpenCV Zoo)", Tr("Finding and recognising faces"), "MIT, Apache 2.0", "https://github.com/opencv/opencv_zoo"),
        ("Real-ESRGAN by Xintao Wang et al.", Tr("AI upscaling when repairing photos, in the versions from Qualcomm AI Hub"), "BSD 3-Clause", "https://github.com/xinntao/Real-ESRGAN"),
        ("Leaflet, MapLibre GL JS", Tr("The map"), "BSD 2-Clause, BSD 3-Clause", "https://leafletjs.com"),
        ("OpenStreetMap", Tr("Map data, © OpenStreetMap contributors"), "ODbL", "https://www.openstreetmap.org/copyright"),
        ("OpenFreeMap", Tr("Map tiles"), "MIT, ODbL", "https://openfreemap.org"),
        ("Nominatim", Tr("Place names for a position, and positions for a place name"), "GPL 2.0, ODbL", "https://nominatim.org"),
        ("Unicode CLDR", Tr("Country names in many languages"), "Unicode License v3", "https://cldr.unicode.org"),
        ("Inno Setup by Jordan Russell", Tr("The installer"), "Inno Setup License", "https://jrsoftware.org/isinfo.php"),
    ];

    private FrameworkElement InfoPage()
    {
        var version = MainViewModel.CurrentVersion;
        var heading = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"];
        var secondary = ThemeBrush("TextFillColorSecondaryBrush");

        var name = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = "HolidayPhotoGallery", Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"] },
                new TextBlock { Text = Tr("Version {0}", AppUpdates.Format(version)), IsTextSelectionEnabled = true },
                new TextBlock
                {
                    // A build number means a test version from "prep"; released versions end in .0.
                    Text = version.Build > 0 ? Tr("Test version") : Tr("Released version"),
                    Foreground = secondary,
                },
            },
        };
        var top = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 16,
            Children =
            {
                new Image
                {
                    Width = 40,
                    Height = 40,
                    Source = new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon-40.png"))),
                },
                name,
            },
        };

        var page = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                top,
                new TextBlock
                {
                    Text = Tr("A photo manager for Windows in the spirit of Windows Live Photo Gallery. Everything you add to a photo is saved in the photo file itself."),
                    TextWrapping = TextWrapping.Wrap,
                },
                new HyperlinkButton { Content = Tr("Website and source code on GitHub"), NavigateUri = new Uri(RepositoryUrl), Padding = new Thickness(0) },
                new TextBlock { Text = Tr("Free and open source under the Apache 2.0 licence."), Foreground = secondary, TextWrapping = TextWrapping.Wrap },

                new TextBlock { Text = Tr("Credits"), Style = heading, Margin = new Thickness(0, 12, 0, 0) },
                new TextBlock
                {
                    Text = Tr("Made by Lasse with Claude Code, the AI coding assistant from Anthropic. Claude wrote the code; Lasse decided what the app should do and tested every version."),
                    TextWrapping = TextWrapping.Wrap,
                },
                new HyperlinkButton { Content = "Claude Code", NavigateUri = new Uri("https://claude.com/claude-code"), Padding = new Thickness(0) },

                new TextBlock { Text = Tr("Built with"), Style = heading, Margin = new Thickness(0, 12, 0, 0) },
            },
        };
        foreach (var (title, use, licence, url) in Credits())
        {
            page.Children.Add(new StackPanel
            {
                Spacing = 0,
                Children =
                {
                    new HyperlinkButton { Content = title, NavigateUri = new Uri(url), Padding = new Thickness(0) },
                    new TextBlock { Text = use, TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = Tr("Licence: {0}", licence), Foreground = secondary, TextWrapping = TextWrapping.Wrap },
                },
            });
        }
        page.Children.Add(new TextBlock
        {
            Text = Tr("The AI models are downloaded from their makers the first time they are needed, and run only on this PC."),
            Foreground = secondary,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        });

        return new ScrollViewer { Content = page, MaxHeight = 480, Padding = new Thickness(0, 0, 12, 0), Visibility = Visibility.Collapsed };
    }
}
