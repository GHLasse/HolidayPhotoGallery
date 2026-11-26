namespace PhotoGallery.Core.Editing;

/// <summary>
/// The Edit pane's "Sharpen" slider (E-17): a classic unsharp mask, the light option next to the AI blur removal. It
/// works on brightness only, so edges get no coloured fringes, and leaves the faintest differences alone, so grain in
/// smooth skies and walls is not made stronger.
/// </summary>
public static class Sharpen
{
    /// <summary>Strength in percent (0 to 100); 0 means no sharpening.</summary>
    public const int Max = 100;

    // At full strength an edge gets 1.5 times its own contrast added.
    private const double FullAmount = 1.5;

    // Differences up to this (in 1/255) are taken for grain and not sharpened; just above it they fade in.
    private const int Threshold = 2;

    /// <summary>
    /// The blur radius in pixels for a picture of this size: about one pixel on a 12 MP photo and the same look on the
    /// smaller preview, so the slider shows what Save will do.
    /// </summary>
    public static double Radius(int width, int height) => Math.Max(0.5, Math.Max(width, height) / 3000.0);

    /// <summary>Sharpens the 8-bit BGRA picture in place by <paramref name="strength"/> percent.</summary>
    public static void Apply(byte[] bgra, int width, int height, int strength)
    {
        if (strength <= 0 || width < 3 || height < 3)
            return;
        if (bgra.Length != width * height * 4)
            throw new ArgumentException("The picture does not have this size.");
        var amount = Math.Min(strength, Max) * FullAmount / Max;
        var count = width * height;

        // Brightness (Rec. 601 weights, which follow what the eye sees well enough for this).
        var luma = new float[count];
        for (var p = 0; p < count; p++)
        {
            var i = p * 4;
            luma[p] = 0.114f * bgra[i] + 0.587f * bgra[i + 1] + 0.299f * bgra[i + 2];
        }
        var blurred = GaussianBlur(luma, width, height, Radius(width, height));

        Parallel.For(0, height, y =>
        {
            for (int x = 0, p = y * width; x < width; x++, p++)
            {
                var detail = luma[p] - blurred[p];
                var size = Math.Abs(detail);
                if (size <= Threshold)
                    continue;
                // Fades in over the next few levels, so there is no hard step between sharpened and left alone.
                var fade = Math.Min(1f, (size - Threshold) / (2f * Threshold));
                var add = (float)(detail * amount * fade);
                var i = p * 4;
                for (var c = 0; c < 3; c++)
                    bgra[i + c] = (byte)Math.Clamp(MathF.Round(bgra[i + c] + add), 0f, 255f);
            }
        });
    }

    private static float[] GaussianBlur(float[] source, int width, int height, double sigma)
    {
        var reach = Math.Max(1, (int)Math.Ceiling(sigma * 3));
        var kernel = new float[2 * reach + 1];
        double sum = 0;
        for (var k = -reach; k <= reach; k++)
            sum += kernel[k + reach] = (float)Math.Exp(-k * k / (2 * sigma * sigma));
        for (var k = 0; k < kernel.Length; k++)
            kernel[k] /= (float)sum;

        var across = new float[source.Length];
        Parallel.For(0, height, y =>
        {
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                float v = 0;
                for (var k = -reach; k <= reach; k++)
                    v += kernel[k + reach] * source[row + Math.Clamp(x + k, 0, width - 1)];
                across[row + x] = v;
            }
        });
        var result = new float[source.Length];
        Parallel.For(0, height, y =>
        {
            for (var x = 0; x < width; x++)
            {
                float v = 0;
                for (var k = -reach; k <= reach; k++)
                    v += kernel[k + reach] * across[Math.Clamp(y + k, 0, height - 1) * width + x];
                result[y * width + x] = v;
            }
        });
        return result;
    }
}
