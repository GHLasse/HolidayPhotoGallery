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

    /// <summary>
    /// True when a ready-made preview (Windows' cached thumbnail or the file's embedded one) has the wrong shape
    /// for the photo: landscape where the upright photo is portrait, or the other way round. That happens when
    /// Windows still holds the thumbnail from before a rotation, or another program turned the photo without
    /// renewing its embedded preview. Nearly square photos are never judged, so a slightly different crop of the
    /// preview doesn't count.
    /// </summary>
    /// <param name="storedWidth">The photo's width as stored in the file, before its orientation is applied.</param>
    /// <param name="storedHeight">The photo's height as stored in the file, before its orientation is applied.</param>
    public static bool PreviewLooksTurned(int previewWidth, int previewHeight, int? storedWidth, int? storedHeight, int orientation)
    {
        if (previewWidth <= 0 || previewHeight <= 0 || storedWidth is not > 0 || storedHeight is not > 0)
            return false;
        var (width, height) = SwapsDimensions(orientation) ? (storedHeight.Value, storedWidth.Value) : (storedWidth.Value, storedHeight.Value);
        return Shape(previewWidth, previewHeight) * Shape(width, height) < 0;
    }

    // 1 for clearly landscape, -1 for clearly portrait, 0 for about square (within 10 %).
    private static int Shape(int width, int height) =>
        width > height * 1.1 ? 1 : height > width * 1.1 ? -1 : 0;
}
