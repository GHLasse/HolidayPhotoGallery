namespace PhotoGallery.Core.Editing;

/// <summary>
/// Mixes the model's grain-free picture (<see cref="Denoiser"/>) into a photo by the Edit pane's "Reduce noise"
/// strength (E-12). Full strength can make skin look waxy, so the user picks how much of the grain goes.
/// </summary>
public static class NoiseReduction
{
    /// <summary>Strength in percent (0 to 100); 0 means no noise reduction.</summary>
    public const int Max = 100;

    /// <summary>
    /// Takes away <paramref name="strength"/> percent of the grain, which is the difference between
    /// <paramref name="noisy"/> and <paramref name="clean"/> (the photo before and after the model, the same size as
    /// <paramref name="picture"/>). <paramref name="picture"/> is usually <paramref name="noisy"/> itself; it can also
    /// be the photo with other changes already made (the preview with retouch strokes), as the grain is the same.
    /// </summary>
    public static void Apply(byte[] picture, byte[] noisy, byte[] clean, int strength)
    {
        if (strength <= 0)
            return;
        if (noisy.Length != picture.Length || clean.Length != picture.Length)
            throw new ArgumentException("The pictures must have the same size.");
        var s = Math.Min(strength, Max) * 256 / Max; // in 1/256 steps
        for (var i = 0; i < picture.Length; i += 4)
        {
            for (var c = 0; c < 3; c++)
            {
                var grain = noisy[i + c] - clean[i + c];
                if (grain == 0)
                    continue;
                // Rounded half away from zero, so a small strength still takes away a little.
                var take = grain > 0 ? (grain * s + 128) >> 8 : -((-grain * s + 128) >> 8);
                picture[i + c] = (byte)Math.Clamp(picture[i + c] - take, 0, 255);
            }
        }
    }
}
