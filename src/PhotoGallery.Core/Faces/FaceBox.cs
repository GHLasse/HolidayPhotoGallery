using PhotoGallery.Core.Metadata;

namespace PhotoGallery.Core.Faces;

/// <summary>
/// A rectangle on a photo as fractions of its width and height (0 to 1), measured from the top left.
/// Depending on where it is used it is relative to the pixels as stored in the file or to the upright
/// photo as shown; <see cref="ToUpright"/> and <see cref="FromUpright"/> convert between the two.
/// </summary>
public readonly record struct FaceBox(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;

    public double CenterX => X + Width / 2;

    public double CenterY => Y + Height / 2;

    public double Area => Math.Max(0, Width) * Math.Max(0, Height);

    /// <summary>The box on the upright photo, for a box on the stored pixels of a photo with this EXIF orientation.</summary>
    public FaceBox ToUpright(int orientation) => Map(this, Orientation.Normalize(orientation));

    /// <summary>The box on the stored pixels, for a box on the upright photo with this EXIF orientation.</summary>
    public FaceBox FromUpright(int orientation) => Map(this, Inverse(Orientation.Normalize(orientation)));

    /// <summary>The share of the smaller box that the two boxes have in common, 0 to 1.</summary>
    public double Overlap(FaceBox other)
    {
        var smaller = Math.Min(Area, other.Area);
        return smaller <= 0 ? 0 : Intersection(other) / smaller;
    }

    /// <summary>Intersection over union, 0 to 1.</summary>
    public double IoU(FaceBox other)
    {
        var shared = Intersection(other);
        var union = Area + other.Area - shared;
        return union <= 0 ? 0 : shared / union;
    }

    /// <summary>The box clipped to the photo (0 to 1).</summary>
    public FaceBox Clamp()
    {
        var x = Math.Clamp(X, 0, 1);
        var y = Math.Clamp(Y, 0, 1);
        return new FaceBox(x, y, Math.Clamp(Right, 0, 1) - x, Math.Clamp(Bottom, 0, 1) - y);
    }

    /// <summary>
    /// The box on a photo that was cropped to <paramref name="crop"/> (both on the upright photo), or null
    /// when less than half of it is left.
    /// </summary>
    public FaceBox? AfterCrop(FaceBox crop)
    {
        if (crop.Width <= 0 || crop.Height <= 0)
            return null;
        var moved = new FaceBox((X - crop.X) / crop.Width, (Y - crop.Y) / crop.Height, Width / crop.Width, Height / crop.Height);
        var kept = moved.Clamp();
        return moved.Area > 0 && kept.Area / moved.Area >= 0.5 ? kept : null;
    }

    private double Intersection(FaceBox other)
    {
        var w = Math.Min(Right, other.Right) - Math.Max(X, other.X);
        var h = Math.Min(Bottom, other.Bottom) - Math.Max(Y, other.Y);
        return w <= 0 || h <= 0 ? 0 : w * h;
    }

    // Turning 90° one way is undone by turning the other way; every other orientation undoes itself.
    private static int Inverse(int orientation) => orientation switch
    {
        6 => 8,
        8 => 6,
        _ => orientation,
    };

    private static FaceBox Map(FaceBox box, int orientation)
    {
        var (x1, y1) = MapPoint(box.X, box.Y, orientation);
        var (x2, y2) = MapPoint(box.Right, box.Bottom, orientation);
        return new FaceBox(Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1), Math.Abs(y2 - y1));
    }

    // Where a point of the stored pixels ends up once the photo is shown upright.
    private static (double X, double Y) MapPoint(double x, double y, int orientation) => orientation switch
    {
        2 => (1 - x, y),
        3 => (1 - x, 1 - y),
        4 => (x, 1 - y),
        5 => (y, x),
        6 => (1 - y, x),
        7 => (1 - y, 1 - x),
        8 => (y, 1 - x),
        _ => (x, y),
    };
}

/// <summary>A person named on a photo, with where their face is on the stored pixels.</summary>
public sealed record PersonTag(string Name, FaceBox Box);
