namespace PhotoGallery.Core.Editing;

/// <summary>
/// Picks the looks that suit a photo best, matching what each look's tip says it is best for. It goes by what the
/// photo itself looks like (dark or bright, flat or contrasty, colourful or dull, warm or cool, much sky, water or
/// greenery), by whether it shows faces, and by its tags. It runs no AI model, so it is instant.
/// </summary>
public static class LookSuggestions
{
    /// <summary>
    /// What a small copy of the photo looks like, each from 0 to 1: <see cref="Warmth"/> is the colour cast of its
    /// greys (0.5 neutral, lower blue), the last three are the share of the photo in those colours.
    /// </summary>
    public sealed record Traits(double Brightness, double Contrast, double Colourfulness, double Warmth,
                                double SkyAndWater, double Greenery, double WarmColours);

    /// <summary>Measures a (small) 8-bit BGRA copy of the photo.</summary>
    public static Traits Measure(byte[] bgra, int width, int height)
    {
        var count = width * height;
        if (count == 0)
            return new Traits(0.5, 0.5, 0.3, 0.5, 0, 0, 0);
        // Every pixel of a small copy, every few of a big one.
        var step = Math.Max(1, count / 40_000);
        double sum = 0, squares = 0, saturation = 0, warmth = 0;
        int n = 0, blue = 0, green = 0, warm = 0, neutral = 0;
        for (var i = 0; i < count; i += step)
        {
            double b = bgra[i * 4], g = bgra[i * 4 + 1], r = bgra[i * 4 + 2];
            var light = (0.299 * r + 0.587 * g + 0.114 * b) / 255;
            sum += light;
            squares += light * light;
            var max = Math.Max(r, Math.Max(g, b));
            var min = Math.Min(r, Math.Min(g, b));
            var s = max <= 0 ? 0 : (max - min) / max;
            saturation += s;
            // The colour cast shows on what should be grey or white (clouds, walls, skin in shade), not on blue sky.
            if (s < 0.25)
            {
                warmth += (r - b) / 255;
                neutral++;
            }
            if (s > 0.2 && max > 50)
            {
                // A hue on a 0-360 circle: blues for sky and water, greens for plants, reds to yellows for sunsets,
                // flowers and warm light.
                var hue = max == r ? 60 * ((g - b) / (max - min)) : max == g ? 60 * (2 + (b - r) / (max - min)) : 60 * (4 + (r - g) / (max - min));
                if (hue < 0)
                    hue += 360;
                if (hue is >= 180 and < 250)
                    blue++;
                else if (hue is >= 70 and < 170)
                    green++;
                else if (hue is < 50 or >= 340)
                    warm++;
            }
            n++;
        }
        var mean = sum / n;
        var spread = Math.Sqrt(Math.Max(0, squares / n - mean * mean));
        return new Traits(mean, Math.Min(1, spread / 0.3), Math.Min(1, saturation / n / 0.6), neutral == 0 ? 0.5 : Math.Clamp(0.5 + warmth / neutral * 2, 0, 1),
                          (double)blue / n, (double)green / n, (double)warm / n);
    }

    /// <summary>
    /// The keys of the <paramref name="count"/> looks that suit the photo best, best first, leaving out
    /// <paramref name="except"/> (the pinned looks, already shown).
    /// </summary>
    /// <param name="faces">How many faces large enough to matter the photo shows.</param>
    public static IReadOnlyList<string> Suggest(Traits photo, int faces, IEnumerable<string> tags, int count,
                                                IEnumerable<string>? except = null)
    {
        if (count <= 0)
            return [];
        var words = tags.SelectMany(t => t.Split('/', ' ', '-', ',')).Select(w => w.Trim().ToLowerInvariant()).Where(w => w.Length > 0).ToHashSet();
        bool Tagged(params string[] any) => any.Any(words.Contains);
        var portrait = faces > 0 || Tagged("portrait", "porträt", "retrato", "person", "people", "menschen", "personas", "selfie");

        // A small base so that, with nothing else to go by, the all-rounders come first.
        var score = new Dictionary<string, double>
        {
            ["crisp"] = 1.0, ["fresh"] = 0.8, ["vivid-warm"] = 0.7, ["golden"] = 0.6, ["bw"] = 0.5, ["warm"] = 0.4,
            ["vivid"] = 0.4, ["cool"] = 0.3, ["soft-haze"] = 0.3, ["matte"] = 0.2, ["vintage"] = 0.2, ["bw-contrast"] = 0.2,
            ["sepia"] = 0.1, ["beauty"] = 0,
        };
        void Add(string key, double points) => score[key] = score.GetValueOrDefault(key) + points;

        // Flat or dull photos gain most from contrast and colour; already strong ones from the softer looks.
        var flat = 1 - photo.Contrast;
        Add("crisp", 2 * flat);
        Add("bw-contrast", 1.5 * flat);
        Add("vivid", 1.5 * (1 - photo.Colourfulness));
        Add("vivid-warm", 1.0 * (1 - photo.Colourfulness));
        Add("bw", 2 * Math.Max(0, 0.35 - photo.Colourfulness) / 0.35);
        Add("soft-haze", 1.5 * Math.Max(0, photo.Colourfulness - 0.6) / 0.4);
        Add("matte", 1.5 * Math.Max(0, photo.Contrast - 0.7) / 0.3);

        // Dark photos: the brighter looks; very bright ones: the ones that calm the highlights.
        Add("fresh", 2 * Math.Max(0, 0.45 - photo.Brightness) / 0.45);
        Add("golden", 1.0 * Math.Max(0, 0.45 - photo.Brightness) / 0.45);
        Add("soft-haze", 1.0 * Math.Max(0, photo.Brightness - 0.65) / 0.35);

        // Sky, water and greenery: nature, beaches, snow; warm colours: sunsets, flowers, food, street.
        Add("fresh", 4 * Math.Min(0.5, photo.SkyAndWater + photo.Greenery));
        Add("cool", 4 * Math.Min(0.4, photo.SkyAndWater));
        Add("vivid-warm", 4 * Math.Min(0.4, photo.WarmColours));
        Add("golden", 3 * Math.Min(0.4, photo.WarmColours));
        Add("vivid", 3 * Math.Min(0.4, photo.WarmColours + photo.Greenery / 2));
        // A cold, grey or blue cast: Warm puts the sun back in.
        Add("warm", 2 * Math.Min(1, Math.Max(0, 0.48 - photo.Warmth) / 0.15));

        if (portrait)
        {
            Add("beauty", faces > 0 ? 3 : 1.5);
            Add("warm", 1.5);
            Add("bw", 1.0);
        }
        if (Tagged("beach", "strand", "playa", "sea", "meer", "mar", "nature", "natur", "naturaleza", "landscape", "landschaft", "paisaje", "outdoors", "mountain", "berge", "montaña"))
            Add("fresh", 1.5);
        if (Tagged("snow", "schnee", "nieve", "water", "wasser", "agua", "city", "stadt", "ciudad", "lake", "see", "lago"))
            Add("cool", 1.5);
        if (Tagged("sunset", "sonnenuntergang", "atardecer", "sunrise", "sonnenaufgang", "amanecer"))
        {
            Add("golden", 2);
            Add("warm", 1);
        }
        if (Tagged("food", "essen", "comida", "flower", "flowers", "blume", "blumen", "flor", "flores"))
            Add("vivid", 2);
        if (Tagged("street", "straße", "strasse", "calle", "travel", "reise", "viaje", "market", "markt", "mercado"))
            Add("vivid-warm", 1.5);
        if (Tagged("architecture", "architektur", "arquitectura", "building", "gebäude", "edificio", "church", "kirche", "iglesia"))
            Add("bw-contrast", 1.5);
        if (Tagged("indoor", "indoors", "innen", "interior", "minimal"))
            Add("soft-haze", 1);

        var skip = except?.ToHashSet() ?? [];
        return Looks.All.Select(l => l.Key)
            .Where(k => !skip.Contains(k))
            .OrderByDescending(k => score.GetValueOrDefault(k))
            .ThenBy(k => Looks.All.ToList().FindIndex(l => l.Key == k))
            .Take(count)
            .ToList();
    }
}
