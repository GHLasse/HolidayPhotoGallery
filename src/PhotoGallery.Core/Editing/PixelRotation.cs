using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.Processing;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.Core.Editing;

/// <summary>
/// Turns PNG, GIF and WebP photos a quarter turn by turning their pixels: these formats have no rotation
/// tag that viewers honour. PNG, GIF and lossless WebP lose nothing; lossy WebP is saved again at high
/// quality. Every frame of an animation is turned.
/// </summary>
public static class PixelRotation
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".png", ".gif", ".webp" };

    public static bool Handles(string path) => Extensions.Contains(Path.GetExtension(path));

    /// <summary>The photo's width and height after the turn, upright.</summary>
    public static async Task<(int Width, int Height)> TurnedSizeAsync(string path, CancellationToken cancellationToken = default)
    {
        var info = await Image.IdentifyAsync(path, cancellationToken).ConfigureAwait(false);
        var sideways = info.Metadata.ExifProfile?.TryGetValue(ExifTag.Orientation, out var orientation) == true && orientation.Value >= 5;
        // Upright first (sideways photos swap), then a quarter turn swaps again.
        return sideways ? (info.Width, info.Height) : (info.Height, info.Width);
    }

    /// <summary>Writes the photo, upright and turned a quarter, to <paramref name="target"/> in the same format.</summary>
    public static async Task TurnAsync(string source, string target, bool clockwise, CancellationToken cancellationToken = default)
    {
        using var image = await Image.LoadAsync(source, cancellationToken).ConfigureAwait(false);
        image.Mutate(x => x.AutoOrient().Rotate(clockwise ? RotateMode.Rotate90 : RotateMode.Rotate270));
        IImageEncoder encoder = Path.GetExtension(source).ToLowerInvariant() switch
        {
            ".png" => new PngEncoder(),
            ".gif" => new GifEncoder(),
            ".webp" => new WebpEncoder { FileFormat = image.Metadata.GetWebpMetadata().FileFormat, Quality = 92 },
            var other => throw new NotSupportedException(Tr("Turning {0} files is not supported.", other)),
        };
        await image.SaveAsync(target, encoder, cancellationToken).ConfigureAwait(false);
    }
}
