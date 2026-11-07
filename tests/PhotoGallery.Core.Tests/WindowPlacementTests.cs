using PhotoGallery.Core.Appearance;
using PhotoGallery.Core.Library;

namespace PhotoGallery.Core.Tests;

public class WindowPlacementTests
{
    private static readonly ScreenArea Main = new(0, 0, 2560, 1392, 1.5);
    private static readonly ScreenArea Right = new(2560, 0, 1920, 1040, 1);

    [Fact]
    public void A_first_start_opens_centred_on_the_main_screen_at_its_scale()
    {
        var r = WindowPlacement.Fit(null, [Main, Right]);
        Assert.Equal(2250, r.Width); // 1500 × 1.5 = 2250 fits; 950 × 1.5 = 1425 > 90 % of 1392
        Assert.Equal((int)(1392 * 0.9), r.Height);
        Assert.Equal((2560 - r.Width) / 2, r.X);
        Assert.Equal((1392 - r.Height) / 2, r.Y);
    }

    [Fact]
    public void Remembered_bounds_come_back_as_they_were()
    {
        var saved = new WindowPlacement { X = 2700, Y = 50, Width = 1200, Height = 800, Scale = 1 };
        Assert.Equal(new ScreenRect(2700, 50, 1200, 800), WindowPlacement.Fit(saved, [Main, Right]));
    }

    [Fact]
    public void A_window_on_a_screen_that_is_gone_opens_centred_on_the_main_screen()
    {
        var saved = new WindowPlacement { X = 2700, Y = 50, Width = 1200, Height = 800, Scale = 1 };
        var r = WindowPlacement.Fit(saved, [Main]);
        Assert.Equal(new ScreenRect((2560 - 1800) / 2, (1392 - 1200) / 2, 1800, 1200), r); // scaled 1 → 1.5
    }

    [Fact]
    public void A_window_partly_off_its_screen_slides_back_onto_it()
    {
        var saved = new WindowPlacement { X = 2000, Y = -100, Width = 1000, Height = 800, Scale = 1.5 };
        var r = WindowPlacement.Fit(saved, [Main with { X = 0 }]);
        Assert.Equal(new ScreenRect(1560, 0, 1000, 800), r);
    }

    [Fact]
    public void A_change_of_scaling_keeps_the_visible_size()
    {
        var saved = new WindowPlacement { X = 100, Y = 100, Width = 1000, Height = 600, Scale = 1 };
        var r = WindowPlacement.Fit(saved, [Main]);
        Assert.Equal(1500, r.Width);
        Assert.Equal(900, r.Height);
    }

    [Fact]
    public void A_window_never_comes_back_tinier_than_the_minimum_or_bigger_than_the_screen()
    {
        var tiny = WindowPlacement.Fit(new WindowPlacement { X = 10, Y = 10, Width = 50, Height = 40 }, [Right]);
        Assert.Equal((WindowPlacement.MinimumWidth, WindowPlacement.MinimumHeight), (tiny.Width, tiny.Height));

        var huge = WindowPlacement.Fit(new WindowPlacement { X = 2560, Y = 0, Width = 5000, Height = 3000 }, [Main, Right]);
        Assert.Equal(new ScreenRect(2560, 0, 1920, 1040), huge);
    }

    [Fact]
    public void The_placement_is_kept_in_the_settings()
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Path, "settings.json");
        new LibrarySettings { Window = new() { X = -5, Y = 7, Width = 1300, Height = 900, Scale = 1.25, Maximized = true } }.Save(path);

        var window = LibrarySettings.Load(path).Window!;
        Assert.Equal((-5, 7, 1300, 900, 1.25, true), (window.X, window.Y, window.Width, window.Height, window.Scale, window.Maximized));
        Assert.Null(new LibrarySettings().Window);
    }
}
