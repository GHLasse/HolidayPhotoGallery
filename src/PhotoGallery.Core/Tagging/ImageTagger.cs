using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace PhotoGallery.Core.Tagging;

/// <summary>
/// Scores how well words describe a photo with SigLIP 2, on the PC's own processor. The photo and
/// each word become points in the same space, and the closer they are, the better the word fits.
/// </summary>
public sealed class ImageTagger : IDisposable
{
    public const int ImageSize = 224;
    private const int TextLength = 64;
    private const int PadId = 0;

    // From the trained model (google/siglip2-base-patch16-224); the ONNX files leave them out.
    private const double LogitScale = 112.66889953613281;
    private const double LogitBias = -16.771724700927734;

    // Below this the model is sure the word does not fit (closeness 0.06), however it compares with
    // the other words in the list.
    private const double MinLogit = -10;

    private readonly InferenceSession _vision;
    private readonly InferenceSession _text;
    private readonly SentencePieceTokenizer _tokenizer;

    private ImageTagger(InferenceSession vision, InferenceSession text, SentencePieceTokenizer tokenizer)
    {
        _vision = vision;
        _text = text;
        _tokenizer = tokenizer;
    }

    /// <summary>Loads the model from <paramref name="folder"/> (see <see cref="TagModel"/>); takes a few seconds.</summary>
    public static ImageTagger Load(string folder)
    {
        using var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        // Leave a core free so the app stays responsive while a batch is tagged.
        options.IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount - 1);

        SentencePieceTokenizer tokenizer;
        // SigLIP 2 texts end with an end mark and have no start mark.
        using (var stream = File.OpenRead(Path.Combine(folder, TagModel.TokenizerFile)))
            tokenizer = SentencePieceTokenizer.Create(stream, false, true);
        var vision = new InferenceSession(Path.Combine(folder, TagModel.VisionFile), options);
        try
        {
            return new ImageTagger(vision, new InferenceSession(Path.Combine(folder, TagModel.TextFile), options), tokenizer);
        }
        catch
        {
            vision.Dispose();
            throw;
        }
    }

    /// <summary>The model's token ids for a text: lower case, ending with the end mark, padded to 64.</summary>
    public long[] Tokenize(string text)
    {
        var ids = _tokenizer.EncodeToIds(text.ToLowerInvariant());
        var result = new long[TextLength];
        Array.Fill(result, PadId);
        var count = Math.Min(ids.Count, TextLength);
        for (var i = 0; i < count; i++)
            result[i] = ids[i];
        // A text too long for the model still has to end with the end mark.
        if (ids.Count > TextLength)
            result[TextLength - 1] = _tokenizer.EndOfSentenceId;
        return result;
    }

    /// <summary>One unit-length vector per text.</summary>
    public float[][] EmbedTexts(IReadOnlyList<string> texts)
    {
        if (texts.Count == 0)
            return [];
        var ids = new long[texts.Count * TextLength];
        for (var i = 0; i < texts.Count; i++)
            Tokenize(texts[i]).CopyTo(ids, i * TextLength);

        var input = new DenseTensor<long>(ids, [texts.Count, TextLength]);
        using var results = _text.Run([NamedOnnxValue.CreateFromTensor("input_ids", input)], ["pooler_output"]);
        return Split(ToFloats(results[0]), texts.Count);
    }

    /// <summary>A unit-length vector for a photo, given as 8-bit BGRA pixels of any size.</summary>
    public float[] EmbedImage(byte[] bgra, int width, int height)
    {
        var input = new DenseTensor<float>(ToPixelValues(bgra, width, height), [1, 3, ImageSize, ImageSize]);
        using var results = _vision.Run([NamedOnnxValue.CreateFromTensor("pixel_values", input)], ["pooler_output"]);
        return Split(ToFloats(results[0]), 1)[0];
    }

    /// <summary>
    /// How much each label stands out for the photo, from 0 to 1, summing to 1 over the list. On its
    /// own the model's yes/no probability is tiny for most everyday words (a cat photo scores 0.6 %
    /// for "cat"), so comparing the labels with each other separates them far better: in tests the
    /// right tags got 15 to 95 %, the wrong ones a few percent. Labels the model rules out get 0.
    /// </summary>
    public static double[] Scores(float[] image, IReadOnlyList<float[]> labels)
    {
        var logits = labels.Select(text =>
        {
            double cosine = 0;
            for (var i = 0; i < image.Length; i++)
                cosine += image[i] * text[i];
            return LogitScale * cosine + LogitBias;
        }).ToArray();
        if (logits.Length == 0)
            return [];

        var max = logits.Max();
        var weights = logits.Select(l => Math.Exp(l - max)).ToArray();
        var total = weights.Sum();
        return logits.Select((l, i) => l < MinLogit ? 0 : weights[i] / total).ToArray();
    }

    // The half-precision models may hand back half-precision numbers.
    private static float[] ToFloats(DisposableNamedOnnxValue value) =>
        value.ElementType == TensorElementType.Float16
            ? value.AsTensor<Float16>().Select(h => (float)h).ToArray()
            : value.AsTensor<float>().ToArray();

    private static float[][] Split(float[] flat, int rows)
    {
        var size = flat.Length / rows;
        var result = new float[rows][];
        for (var r = 0; r < rows; r++)
        {
            var v = flat.AsSpan(r * size, size).ToArray();
            double sum = 0;
            foreach (var x in v)
                sum += x * x;
            var scale = (float)(1 / Math.Sqrt(sum));
            for (var i = 0; i < v.Length; i++)
                v[i] *= scale;
            result[r] = v;
        }
        return result;
    }

    /// <summary>
    /// What the model expects: the photo squeezed to 224 × 224 (as SigLIP was trained, without keeping
    /// its shape), red, green and blue planes, each value scaled to -1 … 1. The resize averages over
    /// all pixels it covers, like the bilinear filter of the Python image library the model was made
    /// with, so a big photo is not reduced to a few sampled pixels.
    /// </summary>
    internal static float[] ToPixelValues(byte[] bgra, int width, int height)
    {
        var (xIndex, xWeights) = ResampleWeights(width, ImageSize);
        var (yIndex, yWeights) = ResampleWeights(height, ImageSize);

        // Rows first: width → 224 for every source row, keeping the three colours.
        var temp = new float[height * ImageSize * 3];
        for (var y = 0; y < height; y++)
        {
            var row = y * width * 4;
            for (var x = 0; x < ImageSize; x++)
            {
                float b = 0, g = 0, r = 0;
                var weights = xWeights[x];
                for (var k = 0; k < weights.Length; k++)
                {
                    var o = row + (xIndex[x] + k) * 4;
                    b += bgra[o] * weights[k];
                    g += bgra[o + 1] * weights[k];
                    r += bgra[o + 2] * weights[k];
                }
                var t = (y * ImageSize + x) * 3;
                temp[t] = r;
                temp[t + 1] = g;
                temp[t + 2] = b;
            }
        }

        // Then columns: height → 224, into separate colour planes scaled from 0 … 255 to -1 … 1.
        const int plane = ImageSize * ImageSize;
        var result = new float[3 * plane];
        for (var y = 0; y < ImageSize; y++)
        {
            var weights = yWeights[y];
            for (var x = 0; x < ImageSize; x++)
            {
                float r = 0, g = 0, b = 0;
                for (var k = 0; k < weights.Length; k++)
                {
                    var t = ((yIndex[y] + k) * ImageSize + x) * 3;
                    r += temp[t] * weights[k];
                    g += temp[t + 1] * weights[k];
                    b += temp[t + 2] * weights[k];
                }
                var p = y * ImageSize + x;
                result[p] = Normalize(r);
                result[plane + p] = Normalize(g);
                result[2 * plane + p] = Normalize(b);
            }
        }
        return result;

        static float Normalize(float value) => Math.Clamp(MathF.Round(value), 0, 255) / 127.5f - 1;
    }

    // For each output pixel, the first source pixel it uses and the weight of each source pixel from
    // there on: a triangle as wide as the scale factor when shrinking, so every source pixel counts.
    private static (int[] First, float[][] Weights) ResampleWeights(int inSize, int outSize)
    {
        var scale = (double)inSize / outSize;
        var support = Math.Max(1.0, scale);
        var first = new int[outSize];
        var weights = new float[outSize][];
        for (var i = 0; i < outSize; i++)
        {
            var center = (i + 0.5) * scale;
            var start = Math.Max(0, (int)Math.Floor(center - support + 0.5));
            var end = Math.Min(inSize, (int)Math.Floor(center + support + 0.5));
            var w = new double[Math.Max(1, end - start)];
            double total = 0;
            for (var j = 0; j < w.Length; j++)
            {
                var distance = Math.Abs((start + j + 0.5 - center) / support);
                w[j] = Math.Max(0, 1 - distance);
                total += w[j];
            }
            first[i] = start;
            weights[i] = w.Select(x => (float)(total > 0 ? x / total : 1.0 / w.Length)).ToArray();
        }
        return (first, weights);
    }

    public void Dispose()
    {
        _vision.Dispose();
        _text.Dispose();
    }
}
