namespace PhotoGallery.Core.Editing;

/// <summary>
/// The colour an eye fix gives the part of the iris that the flash turned red (E-15). Automatic keeps the colour of
/// the iris that is still intact, or a neutral dark grey when none is left, so a blue eye never turns brown.
/// </summary>
public enum EyeColour
{
    Auto,
    Blue,
    Green,
    Hazel,
    Brown,
    Grey,
}

/// <summary>
/// Fixes the wrong colour the flash gives eyes (E-15): red eye on people, and the yellow, green, white or red
/// eyeshine of animals. Only the glowing patch changes. When it is no larger than the pupil it becomes a dark pupil;
/// when it covers the whole iris (white of the eye right around it), only its middle becomes the pupil and the rest
/// gets the iris colour back, with the photo's own texture. The highlight of the flash in a person's eye is kept;
/// an animal's eye gets a small one, since its eyeshine covered it. Pixels are 8-bit BGRA, changed in place, only
/// inside the given circle.
/// </summary>
public static class EyeFix
{
    /// <summary>Fixes the eye around (<paramref name="centerX"/>, <paramref name="centerY"/>) in pixels; false when no glowing patch was found there.</summary>
    /// <param name="radius">How far from the centre to look, in pixels: about the size of the eye.</param>
    /// <param name="animal">Animal eyeshine (any bright colour) instead of red eye.</param>
    public static bool Fix(byte[] bgra, int width, int height, double centerX, double centerY, double radius,
                           bool animal, EyeColour iris = EyeColour.Auto)
    {
        if (radius < 1.5)
            return false;
        var x0 = Math.Max(0, (int)Math.Floor(centerX - radius));
        var y0 = Math.Max(0, (int)Math.Floor(centerY - radius));
        var x1 = Math.Min(width - 1, (int)Math.Ceiling(centerX + radius));
        var y1 = Math.Min(height - 1, (int)Math.Ceiling(centerY + radius));
        if (x1 < x0 || y1 < y0)
            return false;
        var (bw, bh) = (x1 - x0 + 1, y1 - y0 + 1);
        var n = bw * bh;
        var inside = new bool[n];
        var score = new float[n];
        var circle = 0;
        var brightness = new List<int>();
        for (var y = 0; y < bh; y++)
        {
            for (var x = 0; x < bw; x++)
            {
                var (dx, dy) = (x0 + x + 0.5 - centerX, y0 + y + 0.5 - centerY);
                if (dx * dx + dy * dy > radius * radius)
                    continue;
                var i = y * bw + x;
                inside[i] = true;
                circle++;
                var o = ((y0 + y) * width + x0 + x) * 4;
                score[i] = Redness(bgra[o + 2], bgra[o + 1], bgra[o]);
                if (animal)
                    brightness.Add(Math.Max(bgra[o + 2], Math.Max(bgra[o + 1], bgra[o])));
            }
        }
        if (circle < 9)
            return false;

        // Eyeshine glows: much brighter than the darker part of the eye and the fur around it, in any colour.
        if (animal)
        {
            brightness.Sort();
            var (dim, top) = (brightness[brightness.Count / 10], brightness[(int)(brightness.Count * 0.95)]);
            if (top - dim >= 40)
            {
                var threshold = Math.Max(110, dim + (top - dim) * 0.5);
                for (var y = 0; y < bh; y++)
                {
                    for (var x = 0; x < bw; x++)
                    {
                        var i = y * bw + x;
                        if (!inside[i])
                            continue;
                        var o = ((y0 + y) * width + x0 + x) * 4;
                        var v = Math.Max(bgra[o + 2], Math.Max(bgra[o + 1], bgra[o]));
                        score[i] = Math.Max(score[i], (float)Smooth(v, threshold - 15, threshold + 15));
                    }
                }
            }
        }

        // The patch: the glowing pixels joined to the one nearest the centre (where the user clicked).
        // The glow is bright: darker reddish pixels next to it (lashes, the lid's shadow) are not part of it, even
        // when their colour is just as red.
        var seed = -1;
        var nearest = double.MaxValue;
        var peak = 0;
        for (var y = 0; y < bh; y++)
        {
            for (var x = 0; x < bw; x++)
            {
                var i = y * bw + x;
                if (!inside[i] || score[i] < 0.5f)
                    continue;
                var (dx, dy) = (x0 + x + 0.5 - centerX, y0 + y + 0.5 - centerY);
                var d = dx * dx + dy * dy;
                if (d > radius * radius * 0.36)
                    continue;
                peak = Math.Max(peak, Glow(bgra, ((y0 + y) * width + x0 + x) * 4, animal));
                if (d < nearest)
                    (nearest, seed) = (d, i);
            }
        }
        if (seed < 0 || peak < 90)
            return false;
        var dimmest = peak * 0.55;
        var patch = new bool[n];
        var queue = new Queue<int>();
        patch[seed] = true;
        queue.Enqueue(seed);
        while (queue.TryDequeue(out var i))
        {
            var (x, y) = (i % bw, i / bw);
            foreach (var j in Neighbours(x, y, bw, bh))
            {
                if (!patch[j] && inside[j] && score[j] >= 0.3f && Glow(bgra, ((y0 + j / bw) * width + x0 + j % bw) * 4, animal) >= dimmest)
                {
                    patch[j] = true;
                    queue.Enqueue(j);
                }
            }
        }

        // Holes in the patch (the flash's highlight) belong to it: kept on people, covered over on animals.
        var outside = new bool[n];
        for (var y = 0; y < bh; y++)
        {
            for (var x = 0; x < bw; x++)
            {
                var i = y * bw + x;
                if (!inside[i] || patch[i])
                    continue;
                var edge = x == 0 || y == 0 || x == bw - 1 || y == bh - 1 || Neighbours(x, y, bw, bh).Any(j => !inside[j]);
                if (edge)
                {
                    outside[i] = true;
                    queue.Enqueue(i);
                }
            }
        }
        while (queue.TryDequeue(out var i))
        {
            foreach (var j in Neighbours(i % bw, i / bw, bw, bh))
            {
                if (!outside[j] && inside[j] && !patch[j])
                {
                    outside[j] = true;
                    queue.Enqueue(j);
                }
            }
        }

        double count = 0, sumX = 0, sumY = 0;
        for (var i = 0; i < n; i++)
        {
            if (inside[i] && !outside[i])
            {
                count++;
                sumX += i % bw + 0.5;
                sumY += i / bw + 0.5;
            }
        }
        // Too small to be an eye, or so large that it ran out over red skin or fur: left alone.
        if (count < Math.Max(4, circle * 0.006) || count > circle * 0.8)
            return false;
        var (cx, cy) = (sumX / count, sumY / count);
        var patchRadius = Math.Sqrt(count / Math.PI);
        var straying = 0;
        for (var i = 0; i < n; i++)
        {
            if (patch[i] && Distance(i % bw + 0.5, i / bw + 0.5, cx, cy) > patchRadius * (animal ? 2.8 : 1.9))
                straying++;
        }
        if (straying > count * 0.15)
            return false; // not round: a red pattern, not an eye (eyeshine may be a streak when the photo is blurred)

        var covered = new bool[n];
        for (var i = 0; i < n; i++)
            covered[i] = inside[i] && !outside[i];

        // A person's pupil is round. Red skin next to it can join the patch on soft or small photos, so the fix keeps
        // to the round part: its middle found again without the strays, its edge where the patch thins out.
        if (!animal)
        {
            // Each pixel's share of the glow: how red and how bright it is, without a hard threshold, so the darker
            // side of a dim red eye counts too.
            var weight = new double[n];
            for (var i = 0; i < n; i++)
            {
                if (covered[i] && !patch[i])
                    weight[i] = 1; // the flash's highlight in the middle
                else if (inside[i])
                    weight[i] = Smooth(score[i], 0.08, 0.4)
                                * Smooth(Glow(bgra, ((y0 + i / bw) * width + x0 + i % bw) * 4, animal), dimmest * 0.7, dimmest);
            }
            for (var round = 0; round < 3; round++)
            {
                double sw = 0, sx = 0, sy = 0;
                for (var i = 0; i < n; i++)
                {
                    var (x, y) = (i % bw + 0.5, i / bw + 0.5);
                    if (weight[i] > 0 && Distance(x, y, cx, cy) <= patchRadius * 1.4)
                        (sw, sx, sy) = (sw + weight[i], sx + weight[i] * x, sy + weight[i] * y);
                }
                if (sw > 0)
                    (cx, cy) = (sx / sw, sy / sw);
            }
            // The edge: where the glow, averaged around the circle, falls to half of what it is in the middle.
            var rings = (int)Math.Ceiling(patchRadius * 2.5) + 2;
            var (all, sum) = (new int[rings], new double[rings]);
            for (var i = 0; i < n; i++)
            {
                var ring = (int)Distance(i % bw + 0.5, i / bw + 0.5, cx, cy);
                if (ring >= rings || !inside[i])
                    continue;
                all[ring]++;
                sum[ring] += weight[i];
            }
            double Mean(int k) => all[k] > 0 ? sum[k] / all[k] : 0;
            var middle = Enumerable.Range(0, Math.Max(2, (int)patchRadius)).Max(Mean);
            if (middle < 0.2)
                return false;
            var edge = (double)Enumerable.Range(0, Math.Max(2, (int)patchRadius)).First(k => Mean(k) >= middle);
            while (edge < rings - 1 && Mean((int)edge) >= middle * 0.5)
                edge++;
            // Between the last ring above half and the first below it.
            var (above, below) = (Mean((int)edge - 1), Mean((int)edge));
            patchRadius = edge - 1 + (above - middle * 0.5) / Math.Max(1e-6, above - below) + 0.5;
        }

        // On people: is the white of the eye right around the patch? Then the red covers the whole iris, and only its
        // middle is the pupil. Animals' eyeshine fills the pupil, which can be nearly the whole eye.
        var pupilRadius = double.MaxValue;
        var irisColour = (R: 0.0, G: 0.0, B: 0.0);
        // A colour picked by hand says the iris is under the red too.
        if (!animal && (CoversIris(bgra, width, height, x0 + cx, y0 + cy, patchRadius, out var intact) || iris != EyeColour.Auto))
        {
            pupilRadius = patchRadius * 0.5;
            irisColour = IrisColour(iris, intact);
        }

        // How much each pixel changes. People: the round patch, faded out over a pixel at its edge, as far as the
        // pixel is red and bright (the flash's highlight in it is kept). Animals: the patch with its highlight,
        // softened at the edge as much as the photo is soft, so a blurred glow doesn't leave a sharp dark cut-out.
        var amount = new double[n];
        var core = new double[n];
        var feather = animal ? Math.Max(2, (int)Math.Round(patchRadius * 0.5)) : 0;
        var soft = Math.Max(1.0, patchRadius * 0.2);
        for (var i = 0; i < n; i++)
        {
            if (!inside[i])
                continue;
            var o = ((y0 + i / bw) * width + x0 + i % bw) * 4;
            if (animal)
            {
                amount[i] = core[i] = covered[i] ? 1 : 0;
                continue;
            }
            // Near the edge only bright red counts, so lashes and the lid's shadow touching the pupil stay as they are.
            var d = Distance(i % bw + 0.5, i / bw + 0.5, cx, cy);
            var disc = 1 - Smooth(d, patchRadius - 0.5, patchRadius + soft);
            var strict = Smooth(d, patchRadius * 0.6, patchRadius * 0.9);
            amount[i] = disc * Smooth(score[i], 0.08, 0.4)
                        * Smooth(Glow(bgra, o, animal), dimmest * Lerp(0.7, 0.95, strict), dimmest * Lerp(1.0, 1.2, strict));
        }
        if (feather > 0)
        {
            // The glow's halo around the patch goes too, faded out as softly as the glow itself.
            amount = Soften(Grow(amount, inside, bw, bh, feather), inside, bw, bh, feather);
            core = Soften(core, inside, bw, bh, Math.Max(1, feather / 2));
        }

        // The colour around the glow: an animal's pupil takes a dark shade of it, and the glow's halo goes back to it.
        double aroundR = 0, aroundG = 0, aroundB = 0, aroundCount = 0;
        if (animal)
        {
            for (var i = 0; i < n; i++)
            {
                var o = ((y0 + i / bw) * width + x0 + i % bw) * 4;
                if (inside[i] && amount[i] == 0 && Glow(bgra, o, animal) < dimmest)
                    (aroundR, aroundG, aroundB, aroundCount) = (aroundR + bgra[o + 2], aroundG + bgra[o + 1], aroundB + bgra[o], aroundCount + 1);
            }
            if (aroundCount > 0)
                (aroundR, aroundG, aroundB) = (aroundR / aroundCount, aroundG / aroundCount, aroundB / aroundCount);
            else
                (aroundR, aroundG, aroundB) = (60, 55, 50);
        }

        // The texture of the patch, for the iris and to keep the pupil from looking painted.
        double textureSum = 0, textureCount = 0;
        for (var i = 0; i < n; i++)
        {
            if (covered[i])
            {
                var o = ((y0 + i / bw) * width + x0 + i % bw) * 4;
                textureSum += Texture(bgra[o + 2], bgra[o + 1], bgra[o], animal);
                textureCount++;
            }
        }
        var textureMean = Math.Max(1, textureSum / Math.Max(1, textureCount));

        var changed = false;
        for (var i = 0; i < n; i++)
        {
            var (x, y) = (i % bw + 0.5, i / bw + 0.5);
            var o = ((y0 + i / bw) * width + x0 + i % bw) * 4;
            var (r, g, b) = (bgra[o + 2], bgra[o + 1], bgra[o]);
            if (!animal && inside[i] && outside[i] == false && !patch[i]
                && Distance(x, y, cx, cy) <= patchRadius)
            {
                // The flash's highlight stays, without the pink the red around it gave it.
                var grey = Math.Max(r, Math.Max(g, b)) * 0.9 + Math.Min(r, Math.Min(g, b)) * 0.1;
                bgra[o + 2] = Byte(Lerp(r, grey, 0.8));
                bgra[o + 1] = Byte(Lerp(g, grey, 0.8));
                bgra[o] = Byte(Lerp(b, grey, 0.8));
                changed = true;
                continue;
            }
            if (!animal && inside[i] && score[i] > 0)
            {
                // Just outside the fix, red fringes lose their red cast, so no pink rim is left around the pupil.
                // Dark lashes and lid shadows outside the pupil, red as they may be, are not fringe.
                var fringe = score[i] * (1 - Smooth(Distance(x, y, cx, cy), patchRadius + soft, patchRadius + 2.5 * soft))
                             * (Distance(x, y, cx, cy) < patchRadius ? 1 : Smooth(r, dimmest * 0.8, dimmest * 1.1));
                if (fringe > 0)
                {
                    r = Byte(Lerp(r, Math.Max(g, b), fringe));
                    bgra[o + 2] = r;
                    changed = true;
                }
            }
            var alpha = amount[i];
            if (alpha <= 0.002)
                continue;
            var texture = Math.Clamp(Texture(r, g, b, animal) / textureMean, 0.6, 1.4);
            var d = Distance(x, y, cx, cy);

            double nr, ng, nb;
            if (animal)
            {
                // Inside: a dark pupil in the shade of the fur around it. The soft rim outside: the colour around.
                var dark = 0.32 * (0.85 + 0.15 * texture);
                var k = Math.Min(1, core[i] / Math.Max(alpha, 1e-3));
                (nr, ng, nb) = (Lerp(aroundR, aroundR * dark, k), Lerp(aroundG, aroundG * dark, k), Lerp(aroundB, aroundB * dark, k));
            }
            else
            {
                var pupil = Math.Min(55, Texture(r, g, b, animal) * 0.75);
                (nr, ng, nb) = (pupil, pupil, pupil);
                if (pupilRadius < double.MaxValue)
                {
                    // From pupil to iris over a pixel or so, the iris a little darker at its rim, as real ones are.
                    var k = Smooth(d, pupilRadius - soft, pupilRadius + soft);
                    var rim = 1 - 0.3 * Smooth(d / patchRadius, 0.75, 1.05);
                    var shade = rim * (0.85 + 0.15 * texture);
                    (nr, ng, nb) = (Lerp(nr, irisColour.R * shade, k), Lerp(ng, irisColour.G * shade, k), Lerp(nb, irisColour.B * shade, k));
                }
            }
            bgra[o + 2] = Byte(Lerp(r, nr, alpha));
            bgra[o + 1] = Byte(Lerp(g, ng, alpha));
            bgra[o] = Byte(Lerp(b, nb, alpha));
            changed = true;
        }

        // An animal's eye gets back a small highlight, up and to the left of the middle, where light usually comes from.
        if (animal && changed)
        {
            var spot = Math.Max(0.8, Math.Min(patchRadius * 0.18, radius * 0.06));
            var (hx, hy) = (cx - patchRadius * 0.3, cy - patchRadius * 0.3);
            for (var i = 0; i < n; i++)
            {
                if (!inside[i])
                    continue;
                var d = Distance(i % bw + 0.5, i / bw + 0.5, hx, hy);
                var a = 1 - Smooth(d, spot * 0.5, spot * 1.4);
                if (a <= 0)
                    continue;
                var o = ((y0 + i / bw) * width + x0 + i % bw) * 4;
                for (var c = 0; c < 3; c++)
                    bgra[o + c] = Byte(Lerp(bgra[o + c], 225, a * 0.8));
            }
        }
        return changed;
    }

    // A box blur of the amounts, twice (close to a Gaussian), kept inside the circle.
    private static double[] Soften(double[] values, bool[] inside, int width, int height, int radius)
    {
        var a = values;
        for (var pass = 0; pass < 2; pass++)
        {
            var b = new double[a.Length];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    double sum = 0;
                    var count = 0;
                    for (var dx = -radius; dx <= radius; dx++)
                    {
                        var xx = x + dx;
                        if (xx < 0 || xx >= width)
                            continue;
                        sum += a[y * width + xx];
                        count++;
                    }
                    b[y * width + x] = sum / count;
                }
            }
            var c = new double[a.Length];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    double sum = 0;
                    var count = 0;
                    for (var dy = -radius; dy <= radius; dy++)
                    {
                        var yy = y + dy;
                        if (yy < 0 || yy >= height)
                            continue;
                        sum += b[yy * width + x];
                        count++;
                    }
                    c[y * width + x] = inside[y * width + x] ? sum / count : 0;
                }
            }
            a = c;
        }
        return a;
    }

    // Spreads the amounts outwards by this many pixels (a 4-neighbour step at a time), inside the circle.
    private static double[] Grow(double[] values, bool[] inside, int width, int height, int steps)
    {
        var a = values;
        for (var step = 0; step < steps; step++)
        {
            var b = (double[])a.Clone();
            for (var i = 0; i < a.Length; i++)
            {
                if (!inside[i])
                    continue;
                foreach (var j in Neighbours(i % width, i / width, width, height))
                    b[i] = Math.Max(b[i], a[j]);
            }
            a = b;
        }
        return a;
    }

    /// <summary>How red a pixel glows, 0-1: red well above green and blue, and not too dark. Skin stays below it.</summary>
    internal static float Redness(byte r, byte g, byte b)
    {
        var other = Math.Max(g, b);
        if (r < 50 || r - other < 25)
            return 0;
        return (float)Smooth((r - other) / (double)r, 0.22, 0.45);
    }

    // How strongly a pixel glows: its red for red eye, its brightness for eyeshine.
    private static int Glow(byte[] bgra, int o, bool animal) =>
        animal ? Math.Max(bgra[o + 2], Math.Max(bgra[o + 1], bgra[o])) : bgra[o + 2];

    // The patch's own light and dark, without the glow: green and blue for red eye, a dimmed brightness for eyeshine.
    private static double Texture(byte r, byte g, byte b, bool animal) =>
        animal ? Math.Max(r, Math.Max(g, b)) * 0.12 + 12 : (g + b) / 2.0;

    // The white of the eye: bright and nearly without colour.
    private static bool Sclera(byte r, byte g, byte b)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        return max > 150 && (max - min) < max * 0.2;
    }

    // Looks at a ring just outside the patch: a good part of it the white of the eye means the patch is the whole
    // iris. The coloured pixels of the ring are what may be left of the real iris.
    private static bool CoversIris(byte[] bgra, int width, int height, double cx, double cy, double patchRadius,
                                   out List<(byte R, byte G, byte B)> intact)
    {
        intact = [];
        var (inner, outer) = (patchRadius * 1.15, Math.Max(patchRadius * 1.5, patchRadius + 1.5));
        var (total, white) = (0, 0);
        for (var y = (int)Math.Floor(cy - outer); y <= (int)Math.Ceiling(cy + outer); y++)
        {
            for (var x = (int)Math.Floor(cx - outer); x <= (int)Math.Ceiling(cx + outer); x++)
            {
                if (x < 0 || y < 0 || x >= width || y >= height)
                    continue;
                var d = Distance(x + 0.5, y + 0.5, cx, cy);
                if (d < inner || d > outer)
                    continue;
                var o = (y * width + x) * 4;
                var (r, g, b) = (bgra[o + 2], bgra[o + 1], bgra[o]);
                if (Redness(r, g, b) >= 0.3f)
                    continue;
                total++;
                if (Sclera(r, g, b))
                    white++;
                else if (Math.Max(r, Math.Max(g, b)) is >= 30 and <= 220)
                    intact.Add((r, g, b));
            }
        }
        return total >= 6 && white >= total * 0.3;
    }

    // The iris colour: the one chosen, or the colour of what is left of the real iris when it is clearly blue, green
    // or grey. Warm leftovers are not used, because they can't be told from the skin of the eyelid; a neutral dark
    // grey is used instead, which looks right on any eye and never turns a blue eye brown.
    private static (double R, double G, double B) IrisColour(EyeColour choice, List<(byte R, byte G, byte B)> intact)
    {
        switch (choice)
        {
            // Muted, as irises look in flash photos; brighter ones look painted on.
            case EyeColour.Blue: return (76, 96, 118);
            case EyeColour.Green: return (80, 96, 70);
            case EyeColour.Hazel: return (98, 84, 58);
            case EyeColour.Brown: return (74, 52, 36);
            case EyeColour.Grey: return (90, 94, 98);
        }
        var cool = intact.Where(p => p.B >= p.R || (p.G >= p.R && p.G >= p.B)).ToList();
        if (cool.Count >= 4 && cool.Count >= intact.Count * 0.4)
            return (cool.Average(p => (double)p.R), cool.Average(p => (double)p.G), cool.Average(p => (double)p.B));
        return (58, 58, 60);
    }

    private static IEnumerable<int> Neighbours(int x, int y, int width, int height)
    {
        if (x > 0) yield return y * width + x - 1;
        if (x < width - 1) yield return y * width + x + 1;
        if (y > 0) yield return (y - 1) * width + x;
        if (y < height - 1) yield return (y + 1) * width + x;
    }

    private static double Distance(double x, double y, double cx, double cy) => Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
    private static double Lerp(double a, double b, double t) => a + (b - a) * t;
    private static byte Byte(double v) => (byte)Math.Clamp(Math.Round(v), 0, 255);

    private static double Smooth(double v, double from, double to)
    {
        var t = Math.Clamp((v - from) / (to - from), 0, 1);
        return t * t * (3 - 2 * t);
    }
}
