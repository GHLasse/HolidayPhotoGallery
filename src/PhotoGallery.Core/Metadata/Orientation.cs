namespace PhotoGallery.Core.Metadata;

/// <summary>
/// EXIF orientation arithmetic. Rotating a photo only changes this tag, so the pixels are never re-encoded.
/// </summary>
public static class Orientation
{
    // Index = current orientation (1-8), value = orientation after a further 90° clockwise turn.
    private static readonly int[] ClockwiseMap = [0, 6, 7, 8, 5, 2, 3, 4, 1];

    public static int Normalize(int orientation) => orientation is >= 1 and <= 8 ? orientation : 1;

    public static int RotateClockwise(int orientation) => ClockwiseMap[Normalize(orientation)];

    public static int RotateCounterClockwise(int orientation) =>
        RotateClockwise(RotateClockwise(RotateClockwise(orientation)));

    /// <summary>True when the displayed image is the stored one turned on its side (width and height swap).</summary>
    public static bool SwapsDimensions(int orientation) => Normalize(orientation) >= 5;
}
