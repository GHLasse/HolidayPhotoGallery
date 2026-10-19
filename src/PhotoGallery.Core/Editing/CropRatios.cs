namespace PhotoGallery.Core.Editing;

/// <summary>A crop shape the user can pick, written landscape (e.g. 3:2); it turns with the photo.</summary>
/// <param name="Value">Width / height; 0 for <see cref="CropRatios.Free"/> and <see cref="CropRatios.Original"/>.</param>
public sealed record CropRatio(string Key, string Name, double Value);

public static class CropRatios
{
    public const string Original = "original";
    public const string Free = "free";

    /// <summary>The photo's own shape first (the default), then any shape, then common print, screen and social media shapes.</summary>
    public static readonly IReadOnlyList<CropRatio> All =
    [
        new(Original, "Original shape", 0),
        new(Free, "Free", 0),
        new("1:1", "1:1  square, Instagram", 1),
        new("5:4", "5:4  20×25 / 8×10″ print, Instagram portrait", 5.0 / 4),
        new("4:3", "4:3  compact and phone cameras, iPad", 4.0 / 3),
        new("7:5", "7:5  13×18 / 5×7″ print", 7.0 / 5),
        new("3:2", "3:2  10×15 / 4×6″ print, DSLR", 3.0 / 2),
        new("16:10", "16:10  laptop screens", 16.0 / 10),
        new("16:9", "16:9  TV, PC screens, YouTube", 16.0 / 9),
        new("1.91:1", "1.91:1  Facebook and link previews", 1.91),
        new("2:1", "2:1  panorama", 2),
        new("19.5:9", "19.5:9  phone screens, wallpapers", 19.5 / 9),
        new("21:9", "21:9  ultrawide screens", 21.0 / 9),
    ];

    /// <summary>The ratio with this key, or the photo's own shape for an unknown or missing key.</summary>
    public static CropRatio Find(string? key) => All.FirstOrDefault(r => r.Key == key) ?? All[0];

    /// <summary>
    /// Width / height of the crop for a photo of the given size: 0 for free, the photo's own shape for
    /// original, otherwise the ratio turned to match the photo (portrait photos get 2:3 for 3:2).
    /// <paramref name="turned"/> swaps that, for a portrait crop of a landscape photo and vice versa.
    /// </summary>
    public static double Resolve(CropRatio ratio, int width, int height, bool turned)
    {
        if (ratio.Key == Free || width <= 0 || height <= 0)
            return 0;
        var value = ratio.Key == Original ? (double)width / height : ratio.Value;
        var portrait = ratio.Key != Original && height > width;
        if (portrait)
            value = 1 / value;
        return turned ? 1 / value : value;
    }
}
