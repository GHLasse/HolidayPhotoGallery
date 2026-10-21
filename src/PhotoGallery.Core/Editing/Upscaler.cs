using System.Diagnostics;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace PhotoGallery.Core.Editing;

/// <summary>
/// Scales a picture up 4× with a Real-ESRGAN model (see <see cref="UpscaleModel"/>), which draws edges and
/// textures instead of blurring them. It runs on the graphics card through DirectML (any DirectX 12 card)
/// and on the processor when there is no such card, it fails, or the user asked for that. The picture is
/// done in overlapping tiles, so any size works and memory stays small.
/// </summary>
public sealed class Upscaler : IDisposable
{
    private const int Overlap = 8;                                // each side; tiles join without seams
    private const int Step = UpscaleModel.Tile - 2 * Overlap;     // input pixels each tile adds

    private readonly string _modelPath;
    private InferenceSession _session;
    private readonly string _input;

    private Upscaler(string modelPath, InferenceSession session, bool onGraphicsCard)
    {
        _modelPath = modelPath;
        _session = session;
        _input = session.InputMetadata.Keys.First();
        OnGraphicsCard = onGraphicsCard;
    }

    /// <summary>True while it runs on the graphics card, false on the processor.</summary>
    public bool OnGraphicsCard { get; private set; }

    /// <summary>Loads the model; on the graphics card unless <paramref name="processorOnly"/> or that fails.</summary>
    public static Upscaler Load(string modelPath, bool processorOnly = false)
    {
        if (!processorOnly)
        {
            try
            {
                return new Upscaler(modelPath, NewSession(modelPath, onGraphicsCard: true), true);
            }
            catch (Exception ex) when (ex is OnnxRuntimeException or EntryPointNotFoundException or DllNotFoundException)
            {
                // No DirectML here (or no card that can run it): the processor does the work.
            }
        }
        return new Upscaler(modelPath, NewSession(modelPath, onGraphicsCard: false), false);
    }

    private static InferenceSession NewSession(string modelPath, bool onGraphicsCard)
    {
        using var options = new SessionOptions { LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR };
        if (onGraphicsCard)
        {
            // DirectML needs these two.
            options.EnableMemoryPattern = false;
            options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
            options.AppendExecutionProvider_DML(0);
        }
        else
        {
            // Leave a core free so the app stays responsive.
            options.IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount - 1);
        }
        return new InferenceSession(modelPath, options);
    }

    /// <summary>How many tiles a picture of this size takes.</summary>
    public static int TileCount(int width, int height) => Tiles(width) * Tiles(height);

    private static int Tiles(int length) => (length + Step - 1) / Step;

    /// <summary>
    /// How long one tile takes here, measured on a second run (the first one sets the card up). Multiply with
    /// <see cref="TileCount"/> for how long a picture takes.
    /// </summary>
    public TimeSpan TimeTile()
    {
        var tile = new DenseTensor<float>([1, 3, UpscaleModel.Tile, UpscaleModel.Tile]);
        RunSafely(tile);
        var watch = Stopwatch.StartNew();
        RunSafely(tile);
        return watch.Elapsed;
    }

    /// <summary>The picture (8-bit BGRA) 4× as wide and high. Progress is a fraction from 0 to 1.</summary>
    public byte[] Upscale(byte[] bgra, int width, int height, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        const int tile = UpscaleModel.Tile, scale = UpscaleModel.Scale;
        var (outWidth, outHeight) = (width * scale, height * scale);
        var result = new byte[outWidth * outHeight * 4];
        var input = new DenseTensor<float>([1, 3, tile, tile]);
        var buffer = input.Buffer.Span;
        var plane = tile * tile;
        var (count, done) = (TileCount(width, height), 0);

        for (var top = 0; top < height; top += Step)
        {
            for (var left = 0; left < width; left += Step)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // The tile reaches Overlap pixels past its part on each side; outside the picture the edge repeats.
                for (var y = 0; y < tile; y++)
                {
                    var row = Math.Clamp(top - Overlap + y, 0, height - 1) * width;
                    for (var x = 0; x < tile; x++)
                    {
                        var i = (row + Math.Clamp(left - Overlap + x, 0, width - 1)) * 4;
                        var o = y * tile + x;
                        buffer[o] = bgra[i + 2] / 255f;
                        buffer[plane + o] = bgra[i + 1] / 255f;
                        buffer[2 * plane + o] = bgra[i] / 255f;
                    }
                }
                var output = RunSafely(input);

                // Only the tile's own part is kept; the overlap was there so its edges look right.
                var outTile = tile * scale;
                var outPlane = outTile * outTile;
                var keepWidth = Math.Min(Step, width - left) * scale;
                var keepHeight = Math.Min(Step, height - top) * scale;
                for (var y = 0; y < keepHeight; y++)
                {
                    var from = (Overlap * scale + y) * outTile + Overlap * scale;
                    var to = ((top * scale + y) * outWidth + left * scale) * 4;
                    for (var x = 0; x < keepWidth; x++, from++, to += 4)
                    {
                        result[to] = ToByte(output[2 * outPlane + from]);
                        result[to + 1] = ToByte(output[outPlane + from]);
                        result[to + 2] = ToByte(output[from]);
                        result[to + 3] = 255;
                    }
                }
                progress?.Report((double)++done / count);
            }
        }
        return result;
    }

    private static byte ToByte(float value) => (byte)Math.Clamp(MathF.Round(value * 255f), 0f, 255f);

    // A graphics card can fail mid-way (an old driver, out of memory); the processor then takes over.
    private float[] RunSafely(DenseTensor<float> input)
    {
        try
        {
            return Run(input);
        }
        catch (OnnxRuntimeException) when (OnGraphicsCard)
        {
            var session = NewSession(_modelPath, onGraphicsCard: false);
            _session.Dispose();
            _session = session;
            OnGraphicsCard = false;
            return Run(input);
        }
    }

    private float[] Run(DenseTensor<float> input)
    {
        using var results = _session.Run([NamedOnnxValue.CreateFromTensor(_input, input)]);
        return results[0].AsTensor<float>() is DenseTensor<float> dense ? dense.Buffer.ToArray() : results[0].AsTensor<float>().ToArray();
    }

    public void Dispose() => _session.Dispose();
}
