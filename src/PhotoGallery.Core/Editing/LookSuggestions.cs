namespace PhotoGallery.Core.Editing;

/// <summary>
/// Picks the looks that suit a photo best, matching what each look's tip says it is best for. It goes by what the
/// photo itself looks like (dark or bright, flat or contrasty, colourful or dull, warm or cool, much sky, water or
/// greenery), by whether it shows faces, and by its tags. When the tagging model is installed it also goes by
/// what the photo shows (an old car, a historic street, a sunset), through the picture vector the model makes
/// for searching in your own words; see <see cref="Scenes"/>.
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
    /// Scenes the tagging model can recognise and the look that suits each; the scenes without a look stand for
    /// everyday photos, so that an ordinary photo doesn't push any look. Written in English, which the model reads
    /// best, whatever the app's language.
    /// </summary>
    public static readonly IReadOnlyList<(string? Look, string Prompt)> Scenes =
    [
        ("crisp", "this is a photo of a famous landmark on a clear day."),
        ("crisp", "this is a photo of a city skyline."),
        ("crisp", "this is a photo of a sports event."),
        ("crisp", "this is a photo of a harbour with boats on a sunny day."),
        ("crisp", "this is a photo of a group of friends outdoors."),
        ("vivid-warm", "this is a photo of a busy street market."),
        ("vivid-warm", "this is a photo of a lively street in a foreign city."),
        ("vivid-warm", "this is a photo of a festival with crowds."),
        ("vivid-warm", "this is a photo of a colourful autumn landscape."),
        ("vivid-warm", "this is a photo of a desert landscape."),
        ("soft-haze", "this is a photo of a foggy morning."),
        ("soft-haze", "this is a photo of a bright minimal interior."),
        ("soft-haze", "this is a photo of a white room with soft light."),
        ("soft-haze", "this is a photo of misty hills."),
        ("soft-haze", "this is a photo of pastel colours."),
        ("fresh", "this is a photo of a green forest."),
        ("fresh", "this is a photo of a meadow under a blue sky."),
        ("fresh", "this is a photo of a beach with turquoise water."),
        ("fresh", "this is a photo of green mountains on a hike."),
        ("fresh", "this is a photo of a garden in spring."),
        ("golden", "this is a photo of a sunset."),
        ("golden", "this is a photo of a sunrise."),
        ("golden", "this is a photo of a field in golden evening light."),
        ("golden", "this is a photo of friends having a picnic."),
        ("golden", "this is a photo of a beach at sunset."),
        ("beauty", "this is a photo of a close-up portrait of a person."),
        ("beauty", "this is a photo of a selfie."),
        ("beauty", "this is a photo of a smiling face close up."),
        ("vivid", "this is a photo of colourful flowers."),
        ("vivid", "this is a photo of a plate of food."),
        ("vivid", "this is a photo of colourful fruit and vegetables."),
        ("vivid", "this is a photo of colourful houses on a sunny day."),
        ("vivid", "this is a photo of fireworks."),
        ("warm", "this is a photo of a cosy café."),
        ("warm", "this is a photo of a candlelit dinner."),
        ("warm", "this is a photo of people around a campfire."),
        ("warm", "this is a photo of a grey overcast day."),
        ("warm", "this is a photo of an autumn forest."),
        ("cool", "this is a photo of snow and ice."),
        ("cool", "this is a photo of a mountain lake."),
        ("cool", "this is a photo of a waterfall."),
        ("cool", "this is a photo of a city at night."),
        ("cool", "this is a photo of a rainy street."),
        ("matte", "this is a photo of a quiet moody scene."),
        ("matte", "this is a photo of an empty street at dusk."),
        ("matte", "this is a photo of a calm lake at dawn."),
        ("matte", "this is a photo of a lonely tree in a field."),
        ("vintage", "this is a photo of an old vintage car."),
        ("vintage", "this is a photo of old rusty things."),
        ("vintage", "this is a photo of retro objects like a typewriter or a jukebox."),
        ("vintage", "this is a photo of a Vespa scooter or an old Volkswagen bus."),
        ("vintage", "this is a photo of a retro diner or an old shop front."),
        ("vintage", "this is a photo of a fairground carousel."),
        ("sepia", "this is a photo of a historic old town."),
        ("sepia", "this is a photo of a medieval castle."),
        ("sepia", "this is a photo of ancient ruins."),
        ("sepia", "this is a photo of an old church."),
        ("sepia", "this is a photo of an old steam train."),
        ("sepia", "this is a photo of an old wooden sailing ship."),
        ("bw", "this is a photo of a street photo of people walking."),
        ("bw", "this is a photo of an old man with a wrinkled face."),
        ("bw", "this is a photo of a black and white portrait."),
        ("bw", "this is a photo of a couple walking on a street."),
        ("bw-contrast", "this is a photo of dramatic architecture with strong lines."),
        ("bw-contrast", "this is a photo of a skyscraper seen from below."),
        ("bw-contrast", "this is a photo of a bridge with strong lines."),
        ("bw-contrast", "this is a photo of a staircase with strong shadows."),
        (null, "this is a photo of a modern car."),
        (null, "this is a photo of a modern city street."),
        (null, "this is a photo of a modern building."),
        (null, "this is a photo of people on holiday."),
        (null, "this is a photo of a room."),
        (null, "this is a photo of an animal."),
        (null, "this is a photo of a close-up of a dog or a cat."),
        (null, "this is a photo of a modern kitchen."),
        (null, "this is a photo of a laptop or a smartphone."),
        (null, "this is a photo of a modern motorbike."),
        (null, "this is a photo of a modern train or tram."),
        (null, "this is a photo of a shopping centre."),
        (null, "this is a photo of a hotel room."),
        (null, "this is a photo of an airport."),
        (null, "this is a photo of a forest path."),
        (null, "this is a photo of a park."),
        (null, "this is a photo of a swimming pool."),
        (null, "this is a screenshot or a document."),
    ];

    /// <summary>
    /// How strongly the photo shows the scenes of each look, from 0 to 1 (the shares of all looks sum to at most 1),
    /// given its picture vector and the vectors of <see cref="Scenes"/>' prompts, in the same order.
    /// </summary>
    public static IReadOnlyDictionary<string, double> SceneShares(float[] picture, IReadOnlyList<float[]> sceneVectors)
    {
        var shares = new Dictionary<string, double>();
        var scores = Tagging.ImageTagger.Scores(picture, sceneVectors);
        for (var i = 0; i < scores.Length && i < Scenes.Count; i++)
            if (Scenes[i].Look is { } look)
                shares[look] = shares.GetValueOrDefault(look) + scores[i];
        return shares;
    }

    // Each look's average score from the photo itself (the rules in Suggest before tags, faces and scenes) over 135
    // everyday holiday photos from Wikimedia Commons: beaches, cities, food, people, nature, nights and so on.
    private static readonly Dictionary<string, double> Typical = new()
    {
        ["crisp"] = 1.52, ["vivid-warm"] = 1.92, ["soft-haze"] = 0.64, ["fresh"] = 1.71, ["golden"] = 1.29, ["beauty"] = 0,
        ["vivid"] = 1.70, ["warm"] = 0.71, ["cool"] = 0.83, ["matte"] = 0.61, ["vintage"] = 0.2, ["bw"] = 0.68,
        ["bw-contrast"] = 0.59, ["sepia"] = 0.1,
    };

    /// <summary>
    /// The keys of the <paramref name="count"/> looks that suit the photo best, best first, leaving out
    /// <paramref name="except"/> (the pinned looks, already shown).
    /// </summary>
    /// <param name="faces">How many faces large enough to matter the photo shows.</param>
    /// <param name="scenes">What the photo shows, from <see cref="SceneShares"/>, or null without the tagging model.</param>
    public static IReadOnlyList<string> Suggest(Traits photo, int faces, IEnumerable<string> tags, int count,
                                                IEnumerable<string>? except = null, IReadOnlyDictionary<string, double>? scenes = null)
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

        // What counts is how much better a look suits this photo than it suits photos in general: the rules above
        // favour some looks on almost every photo (Vivid warm came up for nine photos in ten). So each look's
        // typical score, measured on 135 everyday holiday photos from Wikimedia Commons, is taken off.
        foreach (var (key, typical) in Typical)
            Add(key, -typical);
        // Beauty, Vintage and Sepia only come up when something speaks for them: faces, tags or what the photo shows.
        Add("beauty", -1);
        Add("vintage", -1);
        Add("sepia", -1);

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
        if (Tagged("vintage", "oldtimer", "antique", "antik", "antiguo", "retro", "nostalgia", "nostalgie", "nostalgía", "historic", "historisch", "histórico", "classic"))
        {
            Add("vintage", 3);
            Add("sepia", 2.5);
        }

        // What the photo shows counts most: a scene the model is sure of brings its look to the front.
        // A scene the model is only half sure of counts little, so that a near miss (a modern motorbike taken for
        // a classic one) doesn't push a look to the front.
        if (scenes is not null)
        {
            double Points(string key) => 5 * Math.Clamp((scenes.GetValueOrDefault(key) - 0.2) / 0.8, 0, 1);
            foreach (var key in scenes.Keys)
                Add(key, Points(key));
            // Vintage and Sepia both suit old things, so each brings the other along a little.
            var vintage = Points("vintage");
            Add("vintage", 0.3 * Points("sepia"));
            Add("sepia", 0.3 * vintage);
        }

        var skip = except?.ToHashSet() ?? [];
        return Looks.All.Select(l => l.Key)
            .Where(k => !skip.Contains(k))
            .OrderByDescending(k => score.GetValueOrDefault(k))
            .ThenBy(k => Looks.All.ToList().FindIndex(l => l.Key == k))
            .Take(count)
            .ToList();
    }
}
