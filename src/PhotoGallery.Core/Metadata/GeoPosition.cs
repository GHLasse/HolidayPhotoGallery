namespace PhotoGallery.Core.Metadata;

/// <summary>A place on Earth in decimal degrees (WGS 84, as GPS and OpenStreetMap use).</summary>
public readonly record struct GeoPosition(double Latitude, double Longitude)
{
    public bool IsValid => Latitude is >= -90 and <= 90 && Longitude is >= -180 and <= 180;
}
