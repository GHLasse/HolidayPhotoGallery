namespace PhotoGallery.Core.Editing;

/// <summary>
/// Mixes the model's sharp picture (<see cref="Deblurrer"/>) into a photo by the Edit pane's "Remove blur" strength
/// (E-17). The model can make a photo that was sharp already look a little drawn, so the user picks how much is used.
/// </summary>
public static class BlurRemoval
{
    /// <summary>Strength in percent (0 to 100); 0 means no blur removal.</summary>
    public const int Max = 100;

    /// <summary>
    /// Adds <paramref name="strength"/> percent of what the model changed (<paramref name="sharp"/> against
    /// <paramref name="blurry"/>, the photo after and before it, the same size as <paramref name="picture"/>).
    /// <paramref name="picture"/> can already carry other changes, such as the noise reduction.
    /// </summary>
    public static void Apply(byte[] picture, byte[] blurry, byte[] sharp, int strength) =>
        NoiseReduction.Apply(picture, blurry, sharp, Math.Min(strength, Max));
}
