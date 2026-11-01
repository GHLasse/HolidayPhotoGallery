using SkiaSharp;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.Core.Editing;

/// <summary>What the start of a WebP file says: whether its picture is stored losslessly, and whether it is an animation.</summary>
public sealed record WebpInfo(bool Lossless, bool Animated)
{
    /// <summary>Reads the file's chunk list (RIFF), or null when it isn't a WebP file.</summary>
    public static WebpInfo? Read(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Read(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static WebpInfo? Read(Stream stream)
    {
        Span<byte> head = stackalloc byte[12];
        if (stream.ReadAtLeast(head, 12, throwOnEndOfStream: false) < 12 ||
            !head[..4].SequenceEqual("RIFF"u8) || !head[8..12].SequenceEqual("WEBP"u8))
            return null;
        bool lossless = false, animated = false;
        Span<byte> chunk = stackalloc byte[8];
        // The chunks that say what the picture is come first; 64 is far more than any file has before them.
        for (var i = 0; i < 64 && stream.ReadAtLeast(chunk, 8, throwOnEndOfStream: false) == 8; i++)
        {
            var size = (long)BitConverter.ToUInt32(chunk[4..8]);
            var fourCc = chunk[..4];
            if (fourCc.SequenceEqual("VP8L"u8))
                lossless = true;
            else if (fourCc.SequenceEqual("ANIM"u8) || fourCc.SequenceEqual("ANMF"u8))
                animated = true;
            else if (fourCc.SequenceEqual("VP8X"u8) && size >= 1)
            {
                var flags = stream.ReadByte();
                if (flags < 0)
                    break;
                animated |= (flags & 0x02) != 0;
                size -= 1;
            }
            if (fourCc.SequenceEqual("VP8 "u8) || fourCc.SequenceEqual("VP8L"u8))
                break; // the picture itself: nothing after it changes the answer
            // Chunks are padded to an even size.
            var skip = size + (size & 1);
            if (stream.CanSeek)
            {
                if (stream.Position + skip > stream.Length)
                    break;
                stream.Seek(skip, SeekOrigin.Current);
            }
            else
            {
                break;
            }
        }
        return new WebpInfo(lossless, animated);
    }
}

/// <summary>
/// Turns still WebP photos a quarter turn by turning their pixels: WebP has no rotation tag that viewers honour,
/// and Windows has no WebP encoder. Lossless WebP stays lossless; lossy WebP is saved again at high quality.
/// Animated WebP is not turned (<see cref="CanTurn"/>). Decoding and encoding is Google's libwebp inside SkiaSharp.
/// </summary>
public static class WebpRotation
{
    public static bool Handles(string path) => Path.GetExtension(path).Equals(".webp", StringComparison.OrdinalIgnoreCase);

    /// <summary>A still WebP can be turned; an animation can't (only its first frame would be kept).</summary>
    public static bool CanTurn(string path) => WebpInfo.Read(path) is { Animated: false };

    /// <summary>The photo's width and height after the turn, upright.</summary>
    /// <exception cref="IOException">The file can't be read as a WebP picture.</exception>
    public static (int Width, int Height) TurnedSize(string path)
    {
        using var codec = Open(path);
        var sideways = (int)codec.EncodedOrigin >= 5;
        // Upright first (sideways photos swap), then a quarter turn swaps again.
        return sideways ? (codec.Info.Width, codec.Info.Height) : (codec.Info.Height, codec.Info.Width);
    }

    /// <summary>Writes the photo, upright and turned a quarter, to <paramref name="target"/> as WebP.</summary>
    /// <exception cref="IOException">The file can't be read, is an animation, or can't be saved.</exception>
    public static void Turn(string source, string target, bool clockwise)
    {
        var info = WebpInfo.Read(source) ?? throw new IOException(Tr("{0} is not a WebP picture.", Path.GetFileName(source)));
        if (info.Animated)
            throw new IOException(Tr("Animated WebP pictures can't be rotated."));

        byte[] pixels;
        int width, height, orientation;
        using (var codec = Open(source))
        {
            width = codec.Info.Width;
            height = codec.Info.Height;
            orientation = (int)codec.EncodedOrigin;
            var decodeInfo = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
            var result = codec.GetPixels(decodeInfo, out pixels);
            if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
                throw new IOException(Tr("{0} could not be read ({1}).", Path.GetFileName(source), result));
        }

        (pixels, width, height) = ImageMath.Upright(pixels, width, height, orientation);
        pixels = ImageMath.Rotate90(pixels, width, height, clockwise);
        (width, height) = (height, width);

        var options = info.Lossless
            ? new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossless, 100)
            : new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossy, 92);
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
        using var pixmap = bitmap.PeekPixels();
        using var data = pixmap.Encode(options) ?? throw new IOException(Tr("{0} could not be saved.", Path.GetFileName(target)));
        using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write);
        data.SaveTo(output);
    }

    private static SKCodec Open(string path) =>
        SKCodec.Create(path) ?? throw new IOException(Tr("{0} could not be read.", Path.GetFileName(path)));
}
