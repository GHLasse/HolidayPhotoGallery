using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace PhotoGallery.Core.Editing;

/// <summary>
/// The magic eraser (E-11): fills a masked part of a photo with matching background using the Big-LaMa model (see
/// <see cref="EraseModel"/>). Only a square around the mask is worked on, made 512 × 512 for the model and scaled
/// back, so any photo size works and the rest of the photo stays exactly as it was. Like the upscaler, it runs on
/// the graphics card through DirectML and on the processor when there is none or that fails.
/// </summary>
public sealed class Eraser : IDisposable
{
    private const int Size = EraseModel.Size;
    private const string ImageInput = "image";
    private const string MaskInput = "mask";

    private readonly string _modelPath;
    private readonly Lock _gate = new();
    private InferenceSession _session;

    private Eraser(string modelPath, InferenceSession session, bool onGraphicsCard)
    {
        _modelPath = modelPath;
        _session = session;
        OnGraphicsCard = onGraphicsCard;
    }

    /// <summary>True while it runs on the graphics card, false on the processor.</summary>
    public bool OnGraphicsCard { get; private set; }

    /// <summary>Loads the model; on the graphics card unless <paramref name="processorOnly"/> or that fails.</summary>
    public static Eraser Load(string modelPath, bool processorOnly = false)
    {
        if (!processorOnly)
        {
            try
            {
                return new Eraser(modelPath, NewSession(modelPath, onGraphicsCard: true), true);
            }
            catch (Exception ex) when (ex is OnnxRuntimeException or EntryPointNotFoundException or DllNotFoundException)
            {
                // No DirectML here (or no card that can run it): the processor does the work.
            }
        }
        return new Eraser(modelPath, NewSession(modelPath, onGraphicsCard: false), false);
    }

    private static InferenceSession NewSession(string modelPath, bool onGraphicsCard)
    {
        using var options = new SessionOptions { LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR };
        if (onGraphicsCard)
        {
            options.EnableMemoryPattern = false;
            options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
            options.AppendExecutionProvider_DML(0);
        }
        else
            options.IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount - 1);
        return new InferenceSession(modelPath, options);
    }

    /// <summary>
    /// The square worked on for a mask: the mask with a border of half its size (at least 96 pixels) all round, so
    /// the model sees enough of the surroundings without the square getting so big that the fill loses detail when
    /// it is made 512 × 512, and at least 512 pixels (or the whole photo when it is smaller).
    /// </summary>
    public static PixelRect Area(PixelRect hole, int width, int height)
    {
        var side = Math.Max(hole.Width, hole.Height);
        var size = Math.Max(side + Math.Max(side / 2, 192), Size);
        var w = Math.Min(size, width);
        var h = Math.Min(size, height);
        var left = Math.Clamp(hole.X + hole.Width / 2 - w / 2, 0, width - w);
        var top = Math.Clamp(hole.Y + hole.Height / 2 - h / 2, 0, height - h);
        return new PixelRect(left, top, w, h);
    }

    /// <summary>Fills the masked pixels (one byte per pixel, not 0 = remove) of the 8-bit BGRA picture in place.</summary>
    public void Erase(byte[] bgra, int width, int height, byte[] mask)
    {
        if (Retouch.Bounds(mask, width, height) is not { } hole)
            return;
        var area = Area(hole, width, height);
        var crop = ImageMath.Crop(bgra, width, height, area);
        var small = area.Width == Size && area.Height == Size ? crop : ImageMath.Resize(crop, area.Width, area.Height, Size, Size);
        var cropMask = CropMask(mask, width, area);
        // The model is given a little more than was painted, so it does not copy the object's own edge back in.
        var smallMask = Dilate(Shrink(cropMask, area.Width, area.Height, Size, Size), Size, Size, 3);

        var image = new DenseTensor<float>([1, 3, Size, Size]);
        var maskTensor = new DenseTensor<float>([1, 1, Size, Size]);
        var pixels = image.Buffer.Span;
        var holes = maskTensor.Buffer.Span;
        const int plane = Size * Size;
        for (var i = 0; i < plane; i++)
        {
            var masked = smallMask[i] != 0;
            holes[i] = masked ? 1 : 0;
            // What is under the mask must not leak into the fill.
            pixels[i] = masked ? 0 : small[i * 4 + 2] / 255f;
            pixels[plane + i] = masked ? 0 : small[i * 4 + 1] / 255f;
            pixels[2 * plane + i] = masked ? 0 : small[i * 4] / 255f;
        }
        var output = RunSafely(image, maskTensor);

        var filled = new byte[plane * 4];
        for (var i = 0; i < plane; i++)
        {
            filled[i * 4] = ToByte(output[2 * plane + i]);
            filled[i * 4 + 1] = ToByte(output[plane + i]);
            filled[i * 4 + 2] = ToByte(output[i]);
            filled[i * 4 + 3] = 255;
        }
        var back = area.Width == Size && area.Height == Size ? filled : ImageMath.Resize(filled, Size, Size, area.Width, area.Height);

        // The model works at 512 pixels, so on a bigger area its fill comes back softer than the photo around it:
        // it gets the photo's own fine grain back, so it does not look smeared.
        var scale = Math.Max(area.Width, area.Height) / (double)Size;
        if (scale > 1.2)
            AddGrain(back, crop, cropMask, area.Width, area.Height, hole.X * 31 + hole.Y);

        // Only the painted part (and a soft edge around it) changes; the rest keeps its full sharpness.
        var reach = Math.Max(2, (int)Math.Ceiling(2 * scale));
        var alpha = Soften(Dilate(cropMask, area.Width, area.Height, reach), area.Width, area.Height, reach);
        for (var y = 0; y < area.Height; y++)
        {
            for (var x = 0; x < area.Width; x++)
            {
                var a = alpha[y * area.Width + x];
                if (a <= 0)
                    continue;
                var o = ((area.Y + y) * width + area.X + x) * 4;
                var f = (y * area.Width + x) * 4;
                for (var c = 0; c < 3; c++)
                    bgra[o + c] = (byte)Math.Round(bgra[o + c] * (1 - a) + back[f + c] * a);
            }
        }
    }

    /// <summary>
    /// Adds fine grain to the masked part of <paramref name="fill"/> until it is as grainy as the unmasked
    /// surroundings in <paramref name="photo"/> (both the same size, 8-bit BGRA). Grain is measured as how much each
    /// pixel's brightness differs from its 3 × 3 neighbourhood. The grain is the same in all three colours, like a
    /// camera's, and always the same for the same <paramref name="seed"/>.
    /// </summary>
    internal static void AddGrain(byte[] fill, byte[] photo, byte[] mask, int width, int height, int seed)
    {
        // The surroundings: unmasked pixels within a band around the mask. The typical (median) difference is used,
        // so a few strong edges (a horizon, a wall) do not count as grain.
        var band = Dilate(mask, width, height, Math.Max(8, Math.Min(width, height) / 16));
        var around = new int[256];
        var inside = new int[256];
        for (var y = 1; y < height - 1; y++)
        {
            for (var x = 1; x < width - 1; x++)
            {
                var i = y * width + x;
                if (mask[i] != 0)
                    inside[Bin(Detail(fill, width, x, y))]++;
                else if (band[i] != 0)
                    around[Bin(Detail(photo, width, x, y))]++;
            }
        }
        if (around.Sum() < 16 || inside.Sum() == 0)
            return;
        // For normal noise the median difference is about two thirds of its spread.
        var target = Math.Min(Median(around) * 1.48, 12);
        var have = Median(inside) * 1.48;
        if (target <= have + 0.3)
            return;
        // White noise loses about a tenth of its strength to the 3 × 3 measure.
        var strength = Math.Sqrt(target * target - have * have) / 0.94;
        var random = new Random(seed);
        for (var i = 0; i < mask.Length; i++)
        {
            if (mask[i] == 0)
                continue;
            // A sum of uniforms: near-normal grain without the cost of a true Gaussian.
            var noise = (random.NextDouble() + random.NextDouble() + random.NextDouble() - 1.5) * 2 * strength;
            for (var c = 0; c < 3; c++)
                fill[i * 4 + c] = (byte)Math.Clamp(Math.Round(fill[i * 4 + c] + noise), 0, 255);
        }
    }

    private static int Bin(double detail) => Math.Min(255, (int)(Math.Abs(detail) * 4));

    private static double Median(int[] bins)
    {
        var half = bins.Sum() / 2;
        var count = 0;
        for (var i = 0; i < bins.Length; i++)
        {
            count += bins[i];
            if (count > half)
                return (i + 0.5) / 4;
        }
        return bins.Length / 4.0;
    }

    // How much a pixel's brightness differs from the average of its 3 × 3 neighbourhood.
    private static double Detail(byte[] bgra, int width, int x, int y)
    {
        double sum = 0;
        for (var j = -1; j <= 1; j++)
            for (var i = -1; i <= 1; i++)
                sum += Brightness(bgra, ((y + j) * width + x + i) * 4);
        return Brightness(bgra, (y * width + x) * 4) - sum / 9;
    }

    private static double Brightness(byte[] bgra, int o) => 0.114 * bgra[o] + 0.587 * bgra[o + 1] + 0.299 * bgra[o + 2];

    private static byte[] CropMask(byte[] mask, int width, PixelRect area)
    {
        var result = new byte[area.Width * area.Height];
        for (var y = 0; y < area.Height; y++)
            Array.Copy(mask, (area.Y + y) * width + area.X, result, y * area.Width, area.Width);
        return result;
    }

    // Smaller (or larger) with any covered pixel making its new pixel covered, so thin strokes are never lost.
    internal static byte[] Shrink(byte[] mask, int width, int height, int newWidth, int newHeight)
    {
        var result = new byte[newWidth * newHeight];
        for (var y = 0; y < height; y++)
        {
            var ny = Math.Min(newHeight - 1, y * newHeight / height);
            for (var x = 0; x < width; x++)
            {
                if (mask[y * width + x] != 0)
                    result[ny * newWidth + Math.Min(newWidth - 1, x * newWidth / width)] = 255;
            }
        }
        if (newWidth > width || newHeight > height)
        {
            // Scaled up: each new pixel looks at the pixel it came from.
            for (var y = 0; y < newHeight; y++)
                for (var x = 0; x < newWidth; x++)
                    if (mask[Math.Min(height - 1, y * height / newHeight) * width + Math.Min(width - 1, x * width / newWidth)] != 0)
                        result[y * newWidth + x] = 255;
        }
        return result;
    }

    // Every pixel within `by` pixels (a square) of a masked one; done as rows, then columns.
    internal static byte[] Dilate(byte[] mask, int width, int height, int by)
    {
        var rows = new byte[mask.Length];
        for (var y = 0; y < height; y++)
        {
            var last = int.MinValue / 2;
            for (var x = 0; x < width; x++)
            {
                if (mask[y * width + x] != 0)
                    last = x;
                if (x - last <= by)
                    rows[y * width + x] = 255;
            }
            last = int.MaxValue / 2;
            for (var x = width - 1; x >= 0; x--)
            {
                if (mask[y * width + x] != 0)
                    last = x;
                if (last - x <= by)
                    rows[y * width + x] = 255;
            }
        }
        var result = new byte[mask.Length];
        for (var x = 0; x < width; x++)
        {
            var last = int.MinValue / 2;
            for (var y = 0; y < height; y++)
            {
                if (rows[y * width + x] != 0)
                    last = y;
                if (y - last <= by)
                    result[y * width + x] = 255;
            }
            last = int.MaxValue / 2;
            for (var y = height - 1; y >= 0; y--)
            {
                if (rows[y * width + x] != 0)
                    last = y;
                if (last - y <= by)
                    result[y * width + x] = 255;
            }
        }
        return result;
    }

    // The mask blurred (a box blur, rows then columns) into 0-1 weights, but full strength wherever it was set, so
    // what was painted is replaced completely and only the edge outside it blends.
    private static float[] Soften(byte[] mask, int width, int height, int radius)
    {
        var rows = new float[mask.Length];
        for (var y = 0; y < height; y++)
        {
            float sum = 0;
            var row = y * width;
            for (var x = -radius; x < width + radius; x++)
            {
                if (x + radius < width && x + radius >= 0)
                    sum += mask[row + x + radius];
                if (x - radius - 1 >= 0 && x - radius - 1 < width)
                    sum -= mask[row + x - radius - 1];
                if (x >= 0 && x < width)
                    rows[row + x] = sum;
            }
        }
        var result = new float[mask.Length];
        var span = (2 * radius + 1f) * (2 * radius + 1f) * 255f;
        for (var x = 0; x < width; x++)
        {
            float sum = 0;
            for (var y = -radius; y < height + radius; y++)
            {
                if (y + radius < height && y + radius >= 0)
                    sum += rows[(y + radius) * width + x];
                if (y - radius - 1 >= 0 && y - radius - 1 < height)
                    sum -= rows[(y - radius - 1) * width + x];
                if (y >= 0 && y < height)
                    result[y * width + x] = Math.Min(1, sum / span * 2);
            }
        }
        return result;
    }

    // The model gives 0-255.
    private static byte ToByte(float value) => (byte)Math.Clamp(MathF.Round(value), 0f, 255f);

    // A graphics card can fail mid-way (an old driver, out of memory); the processor then takes over.
    private float[] RunSafely(DenseTensor<float> image, DenseTensor<float> mask)
    {
        // The preview and Save may erase at the same time; the model takes one picture at a time.
        lock (_gate)
            return RunOrFallBack(image, mask);
    }

    private float[] RunOrFallBack(DenseTensor<float> image, DenseTensor<float> mask)
    {
        try
        {
            return Run(image, mask);
        }
        catch (OnnxRuntimeException) when (OnGraphicsCard)
        {
            var session = NewSession(_modelPath, onGraphicsCard: false);
            _session.Dispose();
            _session = session;
            OnGraphicsCard = false;
            return Run(image, mask);
        }
    }

    private float[] Run(DenseTensor<float> image, DenseTensor<float> mask)
    {
        using var results = _session.Run([NamedOnnxValue.CreateFromTensor(ImageInput, image), NamedOnnxValue.CreateFromTensor(MaskInput, mask)]);
        return results[0].AsTensor<float>().ToArray();
    }

    public void Dispose() => _session.Dispose();
}
