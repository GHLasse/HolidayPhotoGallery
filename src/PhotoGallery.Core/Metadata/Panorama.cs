using System.Globalization;
using System.Text.Json;
using PhotoGallery.Core.ExifTool;

namespace PhotoGallery.Core.Metadata;

/// <summary>
/// Where a 360° photo's picture sits on the whole sphere (L-9). A phone's photo sphere is the whole sphere, twice as
/// wide as high; a partial one is a cut-out of it. All values are parts of the whole (0 to 1): 0 on the left is
/// straight behind, 0 at the top is straight up.
/// </summary>
/// <param name="StartHeading">Which way to look first, in degrees from the middle of the picture.</param>
public sealed record PanoramaLayout(double Left, double Top, double Width, double Height, double StartHeading = 0)
{
    public static readonly PanoramaLayout Sphere = new(0, 0, 1, 1);

    /// <summary>The picture goes all the way round, so its left and right edges meet.</summary>
    public bool WrapsAround => Width >= 0.999;
}

/// <summary>360° photos (L-9): photo spheres from phones (Google Camera, Samsung) and 360° cameras (Ricoh Theta, Insta360).</summary>
public static class Panorama
{
    private static readonly string[] Tags =
    [
        "-XMP-GPano:ProjectionType", "-XMP-GPano:FullPanoWidthPixels", "-XMP-GPano:FullPanoHeightPixels",
        "-XMP-GPano:CroppedAreaLeftPixels", "-XMP-GPano:CroppedAreaTopPixels",
        "-XMP-GPano:CroppedAreaImageWidthPixels", "-XMP-GPano:CroppedAreaImageHeightPixels",
        "-XMP-GPano:InitialViewHeadingDegrees", "-XMP-GPano:PoseHeadingDegrees",
    ];

    /// <summary>Whether XMP GPano's projection type says the photo is wrapped round a sphere.</summary>
    public static bool IsSphere(string? projectionType) =>
        string.Equals(projectionType?.Trim(), "equirectangular", StringComparison.OrdinalIgnoreCase);

    /// <summary>Where the photo's picture sits on the sphere; the whole sphere when the photo doesn't say.</summary>
    public static async Task<PanoramaLayout> ReadLayoutAsync(ExifToolProcess exifTool, string path, CancellationToken cancellationToken = default)
    {
        var output = await exifTool.RunAsync(["-json", "-G1", "-n", .. Tags, path], cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(output.Output) ? PanoramaLayout.Sphere : ParseLayout(output.Output);
    }

    /// <summary>Reads ExifTool's JSON (with -G1 -n) for one photo.</summary>
    public static PanoramaLayout ParseLayout(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
                return PanoramaLayout.Sphere;
            var item = doc.RootElement[0];
            double? Number(string name) =>
                item.TryGetProperty("XMP-GPano:" + name, out var v)
                    ? v.ValueKind == JsonValueKind.Number ? v.GetDouble()
                    : v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d
                    : null
                    : null;

            // Where the middle of the picture faces, against the way the camera faced; GPano measures both from north.
            var start = (Number("InitialViewHeadingDegrees") is { } view ? view - (Number("PoseHeadingDegrees") ?? 0) : 0) % 360;
            if (Number("FullPanoWidthPixels") is not ({ } fullWidth and > 0) || Number("FullPanoHeightPixels") is not ({ } fullHeight and > 0) ||
                Number("CroppedAreaImageWidthPixels") is not ({ } width and > 0) || Number("CroppedAreaImageHeightPixels") is not ({ } height and > 0))
                return PanoramaLayout.Sphere with { StartHeading = start };
            var left = Math.Clamp((Number("CroppedAreaLeftPixels") ?? 0) / fullWidth, 0, 1);
            var top = Math.Clamp((Number("CroppedAreaTopPixels") ?? 0) / fullHeight, 0, 1);
            return new PanoramaLayout(left, top, Math.Clamp(width / fullWidth, 0.01, 1), Math.Clamp(height / fullHeight, 0.01, 1 - top), start);
        }
        catch (JsonException)
        {
            return PanoramaLayout.Sphere;
        }
    }
}
