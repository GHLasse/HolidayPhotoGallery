namespace PhotoGallery.Core.Editing;

/// <summary>The two retouch tools of the Edit pane (E-11).</summary>
public enum RetouchTool
{
    /// <summary>Blemish touch-up: a click or a short stroke over a spot, healed from matching skin or background nearby.</summary>
    Spot,

    /// <summary>Magic eraser: paint over what should go, and the AI model fills it with matching background.</summary>
    Erase,
}

/// <summary>
/// One brush stroke: the points it went through as fractions (0-1) of the upright photo's width and height, and
/// the brush radius as a fraction of the photo's longer side, so it applies to the preview and the full-size photo
/// alike.
/// </summary>
public sealed record RetouchStroke(RetouchTool Tool, IReadOnlyList<(double X, double Y)> Points, double Radius)
{
    /// <summary>The brush radius in pixels on a picture of this size.</summary>
    public double RadiusIn(int width, int height) => Math.Max(1, Radius * Math.Max(width, height));
}

/// <summary>Turns strokes into masks and heals spots (E-11). Pixels are 8-bit BGRA, changed in place.</summary>
public static class Retouch
{
    /// <summary>
    /// The pixels the stroke covers (255) on a picture of this size, one byte per pixel; <paramref name="grow"/>
    /// widens the brush by that many pixels.
    /// </summary>
    public static byte[] Mask(RetouchStroke stroke, int width, int height, double grow = 0)
    {
        var mask = new byte[width * height];
        var r = stroke.RadiusIn(width, height) + grow;
        var points = stroke.Points.Select(p => (X: p.X * width, Y: p.Y * height)).ToList();
        if (points.Count == 0)
            return mask;
        if (points.Count == 1)
            points.Add(points[0]);
        for (var i = 1; i < points.Count; i++)
            Capsule(mask, width, height, points[i - 1], points[i], r);
        return mask;
    }

    // Every pixel whose centre is within r of the line from a to b.
    private static void Capsule(byte[] mask, int width, int height, (double X, double Y) a, (double X, double Y) b, double r)
    {
        var left = Math.Max(0, (int)Math.Floor(Math.Min(a.X, b.X) - r));
        var right = Math.Min(width - 1, (int)Math.Ceiling(Math.Max(a.X, b.X) + r));
        var top = Math.Max(0, (int)Math.Floor(Math.Min(a.Y, b.Y) - r));
        var bottom = Math.Min(height - 1, (int)Math.Ceiling(Math.Max(a.Y, b.Y) + r));
        double dx = b.X - a.X, dy = b.Y - a.Y, length = dx * dx + dy * dy, r2 = r * r;
        for (var y = top; y <= bottom; y++)
        {
            for (var x = left; x <= right; x++)
            {
                double px = x + 0.5 - a.X, py = y + 0.5 - a.Y;
                var t = length > 0 ? Math.Clamp((px * dx + py * dy) / length, 0, 1) : 0;
                double ex = px - t * dx, ey = py - t * dy;
                if (ex * ex + ey * ey <= r2)
                    mask[y * width + x] = 255;
            }
        }
    }

    /// <summary>The smallest rectangle around the masked pixels, or null when nothing is masked.</summary>
    public static PixelRect? Bounds(byte[] mask, int width, int height)
    {
        int left = width, top = height, right = -1, bottom = -1;
        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                if (mask[row + x] == 0)
                    continue;
                left = Math.Min(left, x);
                right = Math.Max(right, x);
                top = Math.Min(top, y);
                bottom = Math.Max(bottom, y);
            }
        }
        return right < 0 ? null : new PixelRect(left, top, right - left + 1, bottom - top + 1);
    }

    /// <summary>
    /// The part of a picture of this size that applying the stroke may change (the brush plus the eraser's soft
    /// edge), so it can be kept to undo the stroke; null when the stroke lies outside the picture.
    /// </summary>
    public static PixelRect? Affected(RetouchStroke stroke, int width, int height)
    {
        if (Bounds(Mask(stroke, width, height, grow: 1), width, height) is not { } bounds)
            return null;
        var edge = 2 * Math.Max(2, (int)Math.Ceiling(2.0 * Math.Max(width, height) / EraseModel.Size)) + 2;
        return Grow(bounds, edge, width, height);
    }

    /// <summary>
    /// Heals the masked part like a healing brush: it finds the nearby patch whose surroundings look most like the
    /// spot's surroundings, takes its texture, and blends it in smoothly (a Poisson blend), so skin pores or sand
    /// grain carry on and the colour and light match at every edge. Needs no AI model and is instant for spots.
    /// </summary>
    public static void Heal(byte[] bgra, int width, int height, byte[] mask)
    {
        if (Bounds(mask, width, height) is not { } hole)
            return;
        var size = Math.Max(hole.Width, hole.Height);
        var pad = Math.Max(4, size / 3);
        var area = Grow(hole, pad, width, height);
        if (BestSource(bgra, width, height, mask, area, size) is not var (dx, dy))
            return;
        Blend(bgra, width, mask, area, dx, dy);
    }

    private static PixelRect Grow(PixelRect r, int by, int width, int height)
    {
        var left = Math.Max(0, r.X - by);
        var top = Math.Max(0, r.Y - by);
        var right = Math.Min(width, r.X + r.Width + by);
        var bottom = Math.Min(height, r.Y + r.Height + by);
        return new PixelRect(left, top, right - left, bottom - top);
    }

    // The shift to the source patch: of the candidates around the spot (a few distances, sixteen directions) that lie
    // inside the photo and keep clear of the spot, the one whose unmasked surroundings differ least, with a small
    // preference for near ones. Null when no candidate fits (a spot as large as the photo).
    private static (int Dx, int Dy)? BestSource(byte[] bgra, int width, int height, byte[] mask, PixelRect area, int size)
    {
        (int, int)? best = null;
        var bestScore = double.MaxValue;
        // Compare every pixel of small areas, every second or third of big ones; the result is the same.
        var step = Math.Max(1, Math.Max(area.Width, area.Height) / 48);
        foreach (var distance in new[] { 1.2, 1.7, 2.4, 3.4 })
        {
            for (var k = 0; k < 16; k++)
            {
                var angle = k * Math.PI / 8;
                var dx = (int)Math.Round(Math.Cos(angle) * distance * size);
                var dy = (int)Math.Round(Math.Sin(angle) * distance * size);
                if (area.X + dx < 0 || area.Y + dy < 0 || area.X + area.Width + dx > width || area.Y + area.Height + dy > height)
                    continue;
                double sum = 0;
                var count = 0;
                var clear = true;
                for (var y = area.Y; y < area.Y + area.Height && clear; y += step)
                {
                    for (var x = area.X; x < area.X + area.Width; x += step)
                    {
                        var source = (y + dy) * width + x + dx;
                        if (mask[source] != 0)
                        {
                            // The patch would take its texture from the spot itself.
                            clear = false;
                            break;
                        }
                        var target = y * width + x;
                        if (mask[target] != 0)
                            continue;
                        for (var c = 0; c < 3; c++)
                        {
                            var d = bgra[target * 4 + c] - bgra[source * 4 + c];
                            sum += d * d;
                        }
                        count++;
                    }
                }
                if (!clear || count == 0)
                    continue;
                var score = sum / count * (1 + 0.15 * distance);
                if (score < bestScore)
                    (bestScore, best) = (score, (dx, dy));
            }
        }
        return best;
    }

    // The source's texture plus a smooth correction that is zero inside nothing but meets the difference between the
    // target and the source at the spot's edge (Laplace's equation, solved by over-relaxation), so no seam shows.
    private static void Blend(byte[] bgra, int width, byte[] mask, PixelRect area, int dx, int dy)
    {
        int w = area.Width, h = area.Height;
        var inside = new bool[w * h];
        var correction = new double[3][];
        for (var c = 0; c < 3; c++)
            correction[c] = new double[w * h];
        var edgeSum = new double[3];
        var edgeCount = 0;
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var target = (area.Y + y) * width + area.X + x;
                var source = target + dy * width + dx;
                var i = y * w + x;
                inside[i] = mask[target] != 0;
                for (var c = 0; c < 3; c++)
                {
                    var difference = bgra[target * 4 + c] - bgra[source * 4 + c];
                    correction[c][i] = difference;
                    if (mask[target] == 0)
                        edgeSum[c] += difference;
                }
                if (mask[target] == 0)
                    edgeCount++;
            }
        }
        // Start from the average difference, which is already close, so few rounds are needed.
        for (var i = 0; i < w * h; i++)
        {
            if (!inside[i])
                continue;
            for (var c = 0; c < 3; c++)
                correction[c][i] = edgeCount > 0 ? edgeSum[c] / edgeCount : 0;
        }
        var rounds = Math.Clamp(Math.Max(w, h) * 3, 40, 800);
        const double over = 1.85;
        for (var round = 0; round < rounds; round++)
        {
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var i = y * w + x;
                    if (!inside[i])
                        continue;
                    // At the photo's edge only the neighbours that exist count.
                    var n = (x > 0 ? 1 : 0) + (x < w - 1 ? 1 : 0) + (y > 0 ? 1 : 0) + (y < h - 1 ? 1 : 0);
                    for (var c = 0; c < 3; c++)
                    {
                        var d = correction[c];
                        var sum = (x > 0 ? d[i - 1] : 0) + (x < w - 1 ? d[i + 1] : 0) + (y > 0 ? d[i - w] : 0) + (y < h - 1 ? d[i + w] : 0);
                        d[i] += over * (sum / n - d[i]);
                    }
                }
            }
        }
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = y * w + x;
                if (!inside[i])
                    continue;
                var target = ((area.Y + y) * width + area.X + x) * 4;
                var source = target + (dy * width + dx) * 4;
                for (var c = 0; c < 3; c++)
                    bgra[target + c] = (byte)Math.Clamp(Math.Round(bgra[source + c] + correction[c][i]), 0, 255);
            }
        }
    }

    /// <summary>
    /// Applies the strokes in order: spots are healed here, erased parts go to <paramref name="erase"/> (the AI
    /// model; strokes are skipped without one).
    /// </summary>
    public static void Apply(byte[] bgra, int width, int height, IEnumerable<RetouchStroke> strokes, Eraser? erase,
                             CancellationToken cancellationToken = default)
    {
        foreach (var stroke in strokes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (stroke.Tool == RetouchTool.Spot)
                Heal(bgra, width, height, Mask(stroke, width, height, grow: 1));
            else
                erase?.Erase(bgra, width, height, Mask(stroke, width, height));
        }
    }
}
