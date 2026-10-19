using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using PhotoGallery.Core.Appearance;
using Orientation = Microsoft.UI.Xaml.Controls.Orientation;
using UiColor = Windows.UI.Color;

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
    private PaletteShades? _lightShades, _darkShades;

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
        _lightShades = palette.ShadesFor(dark: false);
        _darkShades = palette.ShadesFor(dark: true);
        var themes = Root.Resources.ThemeDictionaries;
        themes.Clear();
        if (_lightShades is not null && _darkShades is not null)
        {
            themes["Light"] = PaletteResources(_lightShades);
            themes["Dark"] = PaletteResources(_darkShades);
            themes["Default"] = PaletteResources(_darkShades);
        }

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
        var shades = dark ? _darkShades : _lightShades;
        s_paletteBrushes = shades is null ? [] : Brushes(shades);

        // A palette washes the window with its colour and the toolbar and status bar a little more;
        // the Windows accent colour keeps the Mica background. The viewer keeps its neutral background.
        Root.Background = shades is null ? null : new SolidColorBrush(ToColor(shades.WindowBackground));
        var bars = shades is null ? null : new SolidColorBrush(ToColor(shades.BarBackground));
        TopBar.Background = bars;
        StatusBar.Background = bars;

        // Badges and faces are built in code, with the brushes of the moment.
        ShowTagBadges();
        ShowFilterBadges();
        _ = ShowTopPeopleAsync();
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
        };
    }

    private static UiColor ToColor(Rgb c) => UiColor.FromArgb(255, c.R, c.G, c.B);

    /// <summary>
    /// A theme brush for UI built in code: the palette's own when it has one, otherwise WinUI's for the
    /// window's light or dark mode. Looked up carefully, so a missing one only loses its colour.
    /// </summary>
    private static Brush? ThemeBrush(string key)
    {
        if (s_paletteBrushes.TryGetValue(key, out var own))
            return own;
        return FindInTheme(Application.Current.Resources, key) as Brush
            ?? (Application.Current.Resources.TryGetValue(key, out var value) ? value as Brush : null);
    }

    // The app's resources follow Windows' mode, not the one picked in Settings, so look in the right theme dictionary.
    private static object? FindInTheme(ResourceDictionary dictionary, string key)
    {
        if (dictionary.ThemeDictionaries.TryGetValue(s_themeKey, out var theme) && theme is ResourceDictionary themed
            && (themed.TryGetValue(key, out var found) || (found = FindInMerged(themed, key)) is not null))
            return found;
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

    private static readonly (string Key, string Name)[] Themes = [("system", "Follow Windows"), ("light", "Light"), ("dark", "Dark")];

    /// <summary>The Appearance tab; changes show straight away, and <paramref name="revert"/> undoes them on Cancel.</summary>
    private StackPanel AppearanceTab(out Func<(string Theme, string Palette)> chosen, out Action revert)
    {
        var savedTheme = ViewModel.Theme;
        var savedPalette = ViewModel.Palette;

        var theme = new RadioButtons { Header = "Mode" };
        foreach (var (_, name) in Themes)
            theme.Items.Add(name);
        theme.SelectedIndex = Math.Max(0, Array.FindIndex(Themes, t => t.Key == savedTheme));

        var palette = new RadioButtons { Header = "Colours", MaxColumns = 2 };
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
                    Text = "The photo viewer keeps a neutral background in every palette, so colours don't change how a photo looks.",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
                },
            },
        };
    }
}
