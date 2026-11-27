namespace PhotoGallery.Core.Editing;

/// <summary>
/// Checks that a picture model gives sensible tiles on the graphics card. Some DirectML drivers run a model without an
/// error and still give back a wrong picture (Lasse's night shot came back unchanged except for a square of coloured
/// noise), so the first tile of a run is worked out on the processor too and compared, and every tile is looked at for
/// values that no photo restoration gives.
/// </summary>
public static class ModelTileCheck
{
    /// <summary>
    /// True when the graphics card's tile agrees with the processor's: on average within 1.5 levels (of 255), or within
    /// a third of what the model changed, whichever is more. Tiles are planar RGB from 0 to 1.
    /// </summary>
    public static bool Agrees(ReadOnlySpan<float> input, ReadOnlySpan<float> onCard, ReadOnlySpan<float> onProcessor)
    {
        if (onCard.Length != onProcessor.Length || onCard.Length != input.Length)
            return false;
        double apart = 0, changed = 0;
        for (var i = 0; i < input.Length; i++)
        {
            if (!float.IsFinite(onCard[i]))
                return false;
            apart += Math.Abs(onCard[i] - onProcessor[i]);
            changed += Math.Abs(onProcessor[i] - input[i]);
        }
        apart /= input.Length;
        changed /= input.Length;
        return apart <= Math.Max(1.5 / 255, changed / 3);
    }

    /// <summary>
    /// False when a tile can't be right: a value that isn't a number, or on average more than 40 levels (of 255) away
    /// from the picture that went in, which no noise or blur removal does.
    /// </summary>
    public static bool LooksSane(ReadOnlySpan<float> input, ReadOnlySpan<float> output)
    {
        if (output.Length != input.Length)
            return false;
        double apart = 0;
        for (var i = 0; i < input.Length; i++)
        {
            if (!float.IsFinite(output[i]))
                return false;
            apart += Math.Abs(output[i] - input[i]);
        }
        return apart / input.Length <= 40.0 / 255;
    }

    /// <summary>
    /// Puts the input back wherever the model blew up. The blur model can run away on some patches of a photo (on
    /// Lasse's 48 MP night shot a whole tile turned into a grid of coloured dots, on the processor as well), giving
    /// values far outside 0 to 1. Each <paramref name="block"/>-pixel square with such a value, and the squares around
    /// it (the run-away spreads out softly), gets the picture that went in. Returns how many squares were repaired.
    /// Tiles are planar RGB from 0 to 1, <paramref name="size"/> pixels each way.
    /// </summary>
    public static int RepairBlowUps(Span<float> output, ReadOnlySpan<float> input, int size, int block = 16)
    {
        var plane = size * size;
        var blocks = (size + block - 1) / block;
        var bad = new bool[blocks * blocks];
        var any = false;
        for (var c = 0; c < 3; c++)
        {
            for (var i = 0; i < plane; i++)
            {
                var v = output[c * plane + i];
                if (float.IsFinite(v) && v >= -0.35f && v <= 1.35f)
                    continue;
                bad[i / size / block * blocks + i % size / block] = true;
                any = true;
            }
        }
        if (!any)
            return 0;

        var repaired = 0;
        for (var by = 0; by < blocks; by++)
        {
            for (var bx = 0; bx < blocks; bx++)
            {
                var near = false;
                for (var dy = -1; dy <= 1 && !near; dy++)
                    for (var dx = -1; dx <= 1 && !near; dx++)
                    {
                        var (ny, nx) = (by + dy, bx + dx);
                        near = ny >= 0 && ny < blocks && nx >= 0 && nx < blocks && bad[ny * blocks + nx];
                    }
                if (!near)
                    continue;
                repaired++;
                for (var y = by * block; y < Math.Min(size, (by + 1) * block); y++)
                    for (var x = bx * block; x < Math.Min(size, (bx + 1) * block); x++)
                        for (var c = 0; c < 3; c++)
                            output[c * plane + y * size + x] = input[c * plane + y * size + x];
            }
        }
        return repaired;
    }

    /// <summary>
    /// The top left corner of the tile with the most detail (the largest differences between neighbours), on a grid
    /// of tiles <paramref name="step"/> apart: the tile where a wrong result shows best.
    /// </summary>
    public static (int Top, int Left) MostDetailed(byte[] bgra, int width, int height, int step)
    {
        var best = (Top: 0, Left: 0);
        long bestDetail = -1;
        for (var top = 0; top < height; top += step)
        {
            for (var left = 0; left < width; left += step)
            {
                long detail = 0;
                // Every other row and column is enough to judge.
                for (var y = top; y < Math.Min(height, top + step); y += 2)
                {
                    for (var x = left + 1; x < Math.Min(width, left + step); x += 2)
                    {
                        var i = (y * width + x) * 4 + 1;
                        detail += Math.Abs(bgra[i] - bgra[i - 4]);
                    }
                }
                if (detail > bestDetail)
                    (best, bestDetail) = ((top, left), detail);
            }
        }
        return best;
    }
}
