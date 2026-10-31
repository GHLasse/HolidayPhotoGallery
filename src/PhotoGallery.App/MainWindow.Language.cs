using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhotoGallery.Core.Localization;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.App;

/// <summary>Settings › Language: the language the app is shown in, and where to add another one.</summary>
public sealed partial class MainWindow
{
    /// <summary>The page, and the language chosen when Save is clicked (null when it was left as it is).</summary>
    private FrameworkElement LanguagePage(out Func<string?> chosen)
    {
        var packs = Strings.Available(App.LanguageFolders);
        var list = new ComboBox { Header = Tr("Language of the app"), MinWidth = 260 };
        foreach (var pack in packs)
            list.Items.Add(pack.Name);
        // A language chosen earlier waits for the next start; the list shows it, not the one still on screen.
        var saved = ViewModel.SavedUiLanguage ?? Strings.Language;
        var current = packs.ToList().FindIndex(p => p.Code.Equals(saved, StringComparison.OrdinalIgnoreCase));
        if (current < 0)
            current = Math.Max(0, packs.ToList().FindIndex(p => p.Code.Equals(Strings.Language, StringComparison.OrdinalIgnoreCase)));
        list.SelectedIndex = current;
        chosen = () => list.SelectedIndex >= 0 && list.SelectedIndex != current ? packs[list.SelectedIndex].Code : null;

        var openFolder = new HyperlinkButton { Content = Tr("Open the languages folder"), Padding = new Thickness(0) };
        openFolder.Click += (_, _) =>
        {
            try
            {
                // The folder gets the template, the list of every text to translate.
                Directory.CreateDirectory(App.UserLanguagesFolder);
                var template = Path.Combine(AppContext.BaseDirectory, "Languages", "template.json");
                if (File.Exists(template))
                    File.Copy(template, Path.Combine(App.UserLanguagesFolder, "template.json"), overwrite: true);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(App.UserLanguagesFolder) { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                ViewModel.ErrorMessage = Tr("Could not open the languages folder: {0}", ex.Message);
            }
        };

        TextBlock Note(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = ThemeBrush("TextFillColorSecondaryBrush") };
        return new StackPanel
        {
            Spacing = 12,
            Visibility = Visibility.Collapsed,
            Children =
            {
                list,
                Note(Tr("The new language is used the next time the app starts.")),
                Note(Tr("To add a language, copy template.json in the languages folder, name it after the language (for example fr.json), fill in \"code\" and \"name\", and translate each text. Texts left empty stay in English. The app finds the file the next time it starts.")),
                openFolder,
            },
        };
    }
}
