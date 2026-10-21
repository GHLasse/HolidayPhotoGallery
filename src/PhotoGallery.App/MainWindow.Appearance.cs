using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using PhotoGallery.Core.Appearance;
using Orientation = Microsoft.UI.Xaml.Controls.Orientation;
using UiColor = Windows.UI.Color;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.App;

/// <summary>
/// Light or dark mode, and the colour palette. The palette's colours replace the accent colours WinUI's
/// controls use, as theme dictionaries on the window's root, so they apply straight away, and tint the window.
/// </summary>
public sealed partial class MainWindow
{
    // The palette's brushes for the current light or dark mode, for UI built in code (badges, faces).
    private static Dictionary<string, Brush> s_paletteBrushes = [];
    private static string s_themeKey = "Light";
    private static PaletteShades? s_lightShades, s_darkShades;

    private void ApplyAppearance(string theme, string paletteKey)
    {
        Root.RequestedTheme = theme switch { "light" => ElementTheme.Light, "dark" => ElementTheme.Dark, _ => ElementTheme.Default };
        AppWindow.TitleBar.PreferredTheme = theme switch
        {
            "light" => TitleBarTheme.Light,
            "dark" => TitleBarTheme.Dark,
            _ => TitleBarTheme.UseDefaultAppMode,
        };

        var palette = ColorPalette.Find(paletteKey);
        s_lightShades = palette.ShadesFor(dark: false);
        s_darkShades = palette.ShadesFor(dark: true);
        AddPaletteResources(Root.Resources);

        // Theme resources are looked up again only when the theme changes, so switch away and back.
        var requested = Root.RequestedTheme;
        Root.RequestedTheme = Root.ActualTheme == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
        Root.RequestedTheme = requested;
        OnActualThemeChanged();
    }

    private void Root_ActualThemeChanged(FrameworkElement sender, object args) => OnActualThemeChanged();

    private void OnActualThemeChanged()
    {
        var dark = Root.ActualTheme == ElementTheme.Dark;
        s_themeKey = dark ? "Dark" : "Light";
        var shades = dark ? s_darkShades : s_lightShades;
        s_paletteBrushes = shades is null ? [] : Brushes(shades);

        // A palette washes the window with its colour and the title bar and toolbar a little more; the
        // Windows accent colour keeps the Mica background. The photos and the viewer stay on neutral surfaces.
        Root.Background = shades is null ? null : new SolidColorBrush(ToColor(shades.WindowBackground));
        var bars = shades is null ? null : new SolidColorBrush(ToColor(shades.BarBackground));
        AppTitleBar.Background = bars;
        TopBar.Background = bars;

        // Badges and faces are built in code, with the brushes of the moment.
        ShowTagBadges();
        ShowFilterBadges();
        _ = ShowTopPeopleAsync();
        RecolourTabMarks();
        foreach (var tree in new[] { FolderTree, DateTree, PlaceTree })
            RecolourTreeMark(tree);
    }

    // Dialogs and their popups are not inside Root, so each dialog gets the palette and mode as it opens.
    internal static void ApplyAppearance(ContentDialog dialog)
    {
        dialog.RequestedTheme = s_themeKey == "Dark" ? ElementTheme.Dark : ElementTheme.Light;
        AddPaletteResources(dialog.Resources);
    }

    private static void AddPaletteResources(ResourceDictionary resources)
    {
        var themes = resources.ThemeDictionaries;
        themes.Clear();
        if (s_lightShades is not null && s_darkShades is not null)
        {
            themes["Light"] = PaletteResources(s_lightShades);
            themes["Dark"] = PaletteResources(s_darkShades);
            themes["Default"] = PaletteResources(s_darkShades);
        }
    }

    private static ResourceDictionary PaletteResources(PaletteShades shades)
    {
        var resources = new ResourceDictionary
        {
            ["SystemAccentColor"] = ToColor(shades.Accent),
            ["SystemAccentColorLight1"] = ToColor(shades.Light1),
            ["SystemAccentColorLight2"] = ToColor(shades.Light2),
            ["SystemAccentColorLight3"] = ToColor(shades.Light3),
            ["SystemAccentColorDark1"] = ToColor(shades.Dark1),
            ["SystemAccentColorDark2"] = ToColor(shades.Dark2),
            ["SystemAccentColorDark3"] = ToColor(shades.Dark3),
        };
        foreach (var (key, brush) in Brushes(shades))
            resources[key] = brush;
        return resources;
    }

    // The accent brushes WinUI's controls use for selection, checked buttons, links and accent text.
    private static Dictionary<string, Brush> Brushes(PaletteShades s)
    {
        SolidColorBrush Solid(Rgb color, double opacity = 1) => new(ToColor(color)) { Opacity = opacity };
        return new Dictionary<string, Brush>
        {
            ["AccentFillColorDefaultBrush"] = Solid(s.Fill),
            ["AccentFillColorSecondaryBrush"] = Solid(s.Fill, 0.9),
            ["AccentFillColorTertiaryBrush"] = Solid(s.Fill, 0.8),
            ["AccentFillColorSelectedTextBackgroundBrush"] = Solid(s.Fill),
            ["AccentTextFillColorPrimaryBrush"] = Solid(s.Text),
            ["AccentTextFillColorSecondaryBrush"] = Solid(s.Text),
            ["AccentTextFillColorTertiaryBrush"] = Solid(s.Text, 0.9),
            ["TextOnAccentFillColorPrimaryBrush"] = Solid(s.TextOnFill),
            ["TextOnAccentFillColorSecondaryBrush"] = Solid(s.TextOnFill, 0.7),
            ["TextOnAccentFillColorSelectedTextBrush"] = Solid(s.TextOnFill),
            ["SystemControlHighlightAccentBrush"] = Solid(s.Fill),
            ["SystemControlForegroundAccentBrush"] = Solid(s.Text),
            ["SystemControlHighlightListAccentLowBrush"] = Solid(s.Fill, 0.6),
            ["SystemControlHighlightListAccentMediumBrush"] = Solid(s.Fill, 0.8),
            ["SystemControlHighlightListAccentHighBrush"] = Solid(s.Fill, 0.9),
            ["SelectorBarItemPillFill"] = Solid(s.Fill),
            // The line beside the chosen entry of a list, such as the tabs in Settings.
            ["ListViewItemSelectionIndicatorBrush"] = Solid(s.Fill),
            ["ListViewItemSelectionIndicatorPointerOverBrush"] = Solid(s.Fill),
            ["ListViewItemSelectionIndicatorPressedBrush"] = Solid(s.Fill),
            // The same line beside the chosen folder, date or place in the left pane.
            ["TreeViewItemSelectionIndicatorForeground"] = Solid(s.Fill),

            // WinUI's controls read these, set once from the Windows accent colour, so they are replaced too.
            ["AccentButtonBackground"] = Solid(s.Fill),
            ["AccentButtonBackgroundPointerOver"] = Solid(s.Fill, 0.9),
            ["AccentButtonBackgroundPressed"] = Solid(s.Fill, 0.8),
            ["AccentButtonForeground"] = Solid(s.TextOnFill),
            ["AccentButtonForegroundPointerOver"] = Solid(s.TextOnFill),
            ["AccentButtonForegroundPressed"] = Solid(s.TextOnFill, 0.7),
            ["SliderThumbBackground"] = Solid(s.Fill),
            ["SliderThumbBackgroundPointerOver"] = Solid(s.Fill, 0.9),
            ["SliderThumbBackgroundPressed"] = Solid(s.Fill, 0.8),
            ["SliderTrackValueFill"] = Solid(s.Fill),
            ["SliderTrackValueFillPointerOver"] = Solid(s.Fill),
            ["SliderTrackValueFillPressed"] = Solid(s.Fill),
            ["ProgressBarForeground"] = Solid(s.Fill),
            ["ProgressBarIndeterminateForeground"] = Solid(s.Fill),
            ["ProgressRingForegroundThemeBrush"] = Solid(s.Fill),
            ["RadioButtonOuterEllipseCheckedStroke"] = Solid(s.Fill),
            ["RadioButtonOuterEllipseCheckedStrokePointerOver"] = Solid(s.Fill, 0.9),
            ["RadioButtonOuterEllipseCheckedStrokePressed"] = Solid(s.Fill, 0.8),
            ["RadioButtonOuterEllipseCheckedFill"] = Solid(s.Fill),
            ["RadioButtonOuterEllipseCheckedFillPointerOver"] = Solid(s.Fill, 0.9),
            ["RadioButtonOuterEllipseCheckedFillPressed"] = Solid(s.Fill, 0.8),
            ["RadioButtonCheckGlyphFill"] = Solid(s.TextOnFill),
            ["RadioButtonCheckGlyphFillPointerOver"] = Solid(s.TextOnFill),
            ["RadioButtonCheckGlyphFillPressed"] = Solid(s.TextOnFill),
            ["ToggleSwitchFillOn"] = Solid(s.Fill),
            ["ToggleSwitchFillOnPointerOver"] = Solid(s.Fill, 0.9),
            ["ToggleSwitchFillOnPressed"] = Solid(s.Fill, 0.8),
            ["ToggleSwitchStrokeOn"] = Solid(s.Fill),
            ["ToggleSwitchStrokeOnPointerOver"] = Solid(s.Fill, 0.9),
            ["ToggleSwitchStrokeOnPressed"] = Solid(s.Fill, 0.8),
            ["ToggleSwitchKnobFillOn"] = Solid(s.TextOnFill),
            ["ToggleSwitchKnobFillOnPointerOver"] = Solid(s.TextOnFill),
            ["ToggleSwitchKnobFillOnPressed"] = Solid(s.TextOnFill),
            ["CheckBoxCheckBackgroundFillChecked"] = Solid(s.Fill),
            ["CheckBoxCheckBackgroundFillCheckedPointerOver"] = Solid(s.Fill, 0.9),
            ["CheckBoxCheckBackgroundFillCheckedPressed"] = Solid(s.Fill, 0.8),
            ["CheckBoxCheckBackgroundStrokeChecked"] = Solid(s.Fill),
            ["CheckBoxCheckBackgroundStrokeCheckedPointerOver"] = Solid(s.Fill, 0.9),
            ["CheckBoxCheckBackgroundStrokeCheckedPressed"] = Solid(s.Fill, 0.8),
            ["CheckBoxCheckGlyphForegroundChecked"] = Solid(s.TextOnFill),
            ["CheckBoxCheckGlyphForegroundCheckedPointerOver"] = Solid(s.TextOnFill),
            ["CheckBoxCheckGlyphForegroundCheckedPressed"] = Solid(s.TextOnFill),
        };
    }

    private static UiColor ToColor(Rgb c) => UiColor.FromArgb(255, c.R, c.G, c.B);

    /// <summary>
    /// A theme brush for UI built in code: the palette's own when it has one, otherwise WinUI's for the
    /// window's light or dark mode. Looked up carefully, so a missing one only loses its colour.
    /// </summary>
    internal static Brush? ThemeBrush(string key)
    {
        if (s_paletteBrushes.TryGetValue(key, out var own))
            return own;
        return FindInTheme(Application.Current.Resources, key) as Brush
            ?? (Application.Current.Resources.TryGetValue(key, out var value) ? value as Brush : null);
    }

    // The app's resources follow Windows' mode, not the one picked in Settings, so look in the right theme dictionary.
    private static object? FindInTheme(ResourceDictionary dictionary, string key)
    {
        // WinUI files its dark brushes under "Default", not "Dark"; missing that gave light-mode grey text
        // (and black sparkles) in dark Settings.
        foreach (var themeKey in s_themeKey == "Dark" ? new[] { "Dark", "Default" } : new[] { s_themeKey })
        {
            if (dictionary.ThemeDictionaries.TryGetValue(themeKey, out var theme) && theme is ResourceDictionary themed
                && (themed.TryGetValue(key, out var found) || (found = FindInMerged(themed, key)) is not null))
                return found;
        }
        foreach (var merged in dictionary.MergedDictionaries)
        {
            if (FindInTheme(merged, key) is { } value)
                return value;
        }
        return null;
    }

    private static object? FindInMerged(ResourceDictionary dictionary, string key)
    {
        foreach (var merged in dictionary.MergedDictionaries)
        {
            if (merged.TryGetValue(key, out var value) || (value = FindInMerged(merged, key)) is not null)
                return value;
        }
        return null;
    }

    // ---- The Appearance tab in Settings ----

    private static (string Key, string Name)[] Themes => [("system", Tr("Follow Windows")), ("light", Tr("Light")), ("dark", Tr("Dark"))];

    /// <summary>The Appearance tab; changes show straight away, and <paramref name="revert"/> undoes them on Cancel.</summary>
    private StackPanel AppearanceTab(out Func<(string Theme, string Palette)> chosen, out Action revert)
    {
        var savedTheme = ViewModel.Theme;
        var savedPalette = ViewModel.Palette;

        var theme = new RadioButtons { Header = Tr("Mode") };
        foreach (var (_, name) in Themes)
            theme.Items.Add(name);
        theme.SelectedIndex = Math.Max(0, Array.FindIndex(Themes, t => t.Key == savedTheme));

        var palette = new RadioButtons { Header = Tr("Colours"), MaxColumns = 2 };
        foreach (var p in ColorPalette.All)
        {
            var swatch = new Ellipse
            {
                Width = 18,
                Height = 18,
                Stroke = ThemeBrush("ControlStrokeColorDefaultBrush"),
                StrokeThickness = 1,
                Fill = new SolidColorBrush(p.Color is { } color ? ToColor(color) : (UiColor)Application.Current.Resources["SystemAccentColor"]),
            };
            palette.Items.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children = { swatch, new TextBlock { Text = p.Name, VerticalAlignment = VerticalAlignment.Center } },
            });
        }
        palette.SelectedIndex = Math.Max(0, ColorPalette.All.ToList().IndexOf(ColorPalette.Find(savedPalette)));

        (string, string) Current() =>
            (Themes[Math.Max(0, theme.SelectedIndex)].Key, ColorPalette.All[Math.Max(0, palette.SelectedIndex)].Key);
        void Preview()
        {
            var (t, p) = Current();
            ApplyAppearance(t, p);
        }
        theme.SelectionChanged += (_, _) => Preview();
        palette.SelectionChanged += (_, _) => Preview();

        chosen = Current;
        revert = () => ApplyAppearance(savedTheme, savedPalette);
        return new StackPanel
        {
            Spacing = 16,
            Children =
            {
                theme,
                palette,
                new TextBlock
                {
                    Text = Tr("The photo viewer keeps a neutral background in every palette, so colours don't change how a photo looks."),
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
                },
            },
        };
    }
}

/// <summary>Opens dialogs in the app's mode and palette (see <see cref="MainWindow.ApplyAppearance(ContentDialog)"/>).</summary>
internal static class ThemedDialogs
{
    public static Windows.Foundation.IAsyncOperation<ContentDialogResult> ShowThemedAsync(this ContentDialog dialog)
    {
        MainWindow.ApplyAppearance(dialog);
        return dialog.ShowAsync();
    }
}
