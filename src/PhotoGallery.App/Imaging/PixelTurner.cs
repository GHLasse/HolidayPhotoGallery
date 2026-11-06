using System.Runtime.InteropServices.WindowsRuntime;
using PhotoGallery.Core.Editing;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using static PhotoGallery.Core.Localization.Strings;

namespace PhotoGallery.App.Imaging;

/// <summary>
/// Turns GIF pictures a quarter turn with Windows' own GIF codec, every frame of an animation with its
/// place, delay and disposal kept, so the animation plays as before. (PNG and HEIC go through
/// <see cref="ImageEncoder"/>, still WebP through <see cref="WebpRotation"/>.)
/// </summary>
public static class PixelTurner
{
    public static bool Handles(string path) => Path.GetExtension(path).Equals(".gif", StringComparison.OrdinalIgnoreCase);

    /// <summary>The size after the turn: the GIF's canvas, sideways.</summary>
    public static Task<(int Width, int Height)> TurnedSizeAsync(string path) => Task.Run(async () =>
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        using var stream = await file.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(BitmapDecoder.GifDecoderId, stream);
        var (width, height) = await CanvasAsync(decoder);
        return (height, width);
    });

    /// <summary>Writes the GIF turned a quarter to <paramref name="target"/>, a new file.</summary>
    public static Task TurnAsync(string source, string target, bool clockwise) => Task.Run(async () =>
    {
        var file = await StorageFile.GetFileFromPathAsync(source);
        using var input = await file.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(BitmapDecoder.GifDecoderId, input);
        var (canvasWidth, canvasHeight) = await CanvasAsync(decoder);

        using var memory = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.GifEncoderId, memory);
        var loops = await LoopDataAsync(decoder);
        for (uint i = 0; i < decoder.FrameCount; i++)
        {
            var frame = await decoder.GetFrameAsync(i);
            using var bitmap = await frame.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight);
            var (width, height) = (bitmap.PixelWidth, bitmap.PixelHeight);
            var pixels = new byte[width * height * 4];
            bitmap.CopyToBuffer(pixels.AsBuffer());
            var turned = ImageMath.Rotate90(pixels, width, height, clockwise);

            var read = await ReadAsync(frame.BitmapProperties, "/imgdesc/Left", "/imgdesc/Top", "/grctlext/Delay", "/grctlext/Disposal");
            var left = Number(read, "/imgdesc/Left");
            var top = Number(read, "/imgdesc/Top");
            // Where the frame lands on the turned canvas: its far corner becomes the near one.
            var (newLeft, newTop) = clockwise
                ? (canvasHeight - top - height, left)
                : (top, canvasWidth - left - width);

            if (i > 0)
                await encoder.GoToNextFrameAsync();
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, (uint)height, (uint)width, 96, 96, turned);

            var properties = new BitmapPropertySet
            {
                { "/imgdesc/Left", new BitmapTypedValue((ushort)Math.Max(0, newLeft), PropertyType.UInt16) },
                { "/imgdesc/Top", new BitmapTypedValue((ushort)Math.Max(0, newTop), PropertyType.UInt16) },
            };
            if (decoder.FrameCount > 1)
            {
                properties.Add("/grctlext/Delay", new BitmapTypedValue((ushort)Number(read, "/grctlext/Delay", 10), PropertyType.UInt16));
                properties.Add("/grctlext/Disposal", new BitmapTypedValue((byte)Number(read, "/grctlext/Disposal"), PropertyType.UInt8));
            }
            await encoder.BitmapProperties.SetPropertiesAsync(properties);
        }

        var container = new BitmapPropertySet
        {
            { "/logscrdesc/Width", new BitmapTypedValue((ushort)canvasHeight, PropertyType.UInt16) },
            { "/logscrdesc/Height", new BitmapTypedValue((ushort)canvasWidth, PropertyType.UInt16) },
        };
        if (decoder.FrameCount > 1)
        {
            container.Add("/appext/Application", new BitmapTypedValue("NETSCAPE2.0"u8.ToArray(), PropertyType.UInt8Array));
            container.Add("/appext/Data", new BitmapTypedValue(loops ?? [3, 1, 0, 0, 0], PropertyType.UInt8Array));
        }
        await encoder.BitmapContainerProperties.SetPropertiesAsync(container);
        await encoder.FlushAsync();

        memory.Seek(0);
        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write);
        await memory.AsStreamForRead().CopyToAsync(output);
    });

    private static async Task<(int Width, int Height)> CanvasAsync(BitmapDecoder decoder)
    {
        var read = await ReadAsync(decoder.BitmapContainerProperties, "/logscrdesc/Width", "/logscrdesc/Height");
        var width = Number(read, "/logscrdesc/Width");
        var height = Number(read, "/logscrdesc/Height");
        return width > 0 && height > 0 ? (width, height) : ((int)decoder.PixelWidth, (int)decoder.PixelHeight);
    }

    // How often the animation repeats (the NETSCAPE2.0 block), kept as it was.
    private static async Task<byte[]?> LoopDataAsync(BitmapDecoder decoder)
    {
        var read = await ReadAsync(decoder.BitmapContainerProperties, "/appext/Data");
        return read.TryGetValue("/appext/Data", out var value) && value.Value is byte[] { Length: >= 4 } data ? data : null;
    }

    // Each property on its own: a GIF without one (e.g. no delay) would otherwise fail the whole read.
    private static async Task<Dictionary<string, BitmapTypedValue>> ReadAsync(BitmapPropertiesView view, params string[] names)
    {
        var found = new Dictionary<string, BitmapTypedValue>();
        foreach (var name in names)
        {
            try
            {
                var set = await view.GetPropertiesAsync([name]);
                if (set.TryGetValue(name, out var value))
                    found[name] = value;
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException or NotSupportedException)
            {
            }
        }
        return found;
    }

    private static int Number(Dictionary<string, BitmapTypedValue> read, string name, int otherwise = 0) =>
        read.TryGetValue(name, out var value) && value.Value is { } v
            ? v switch { ushort u => u, byte b => b, uint u32 => (int)u32, int n => n, short s => s, _ => otherwise }
            : otherwise;

    /// <summary>The message for an animated WebP, whose rotation is turned off.</summary>
    public static string AnimatedWebpHint => Tr("Animated WebP pictures can't be rotated.");
}
