using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace PhotoGallery.Core.Faces;

/// <summary>A face found on a photo.</summary>
/// <param name="Box">Where it is on the upright photo, as fractions of its width and height.</param>
/// <param name="Score">How sure the detector is that it is a face, 0 to 1.</param>
/// <param name="Embedding">A unit-length vector; faces of the same person point the same way (see <see cref="FaceAnalyzer.Similarity"/>).</param>
public sealed record DetectedFace(FaceBox Box, float Score, float[] Embedding);

/// <summary>
/// Finds faces with YuNet and describes each with SFace, on the PC's own processor. The photo is
/// looked at whole and, when it is big enough, in four overlapping quarters, so faces of people
/// further away are found too.
/// </summary>
public sealed class FaceAnalyzer : IDisposable
{
    /// <summary>The longest side photos should be decoded at before <see cref="Analyze"/>.</summary>
    public const int ImageSize = 1280;

    /// <summary>
    /// Faces at least this similar are very likely the same person. SFace's published threshold is 0.363;
    /// a little above it gives fewer wrong suggestions.
    /// </summary>
    public const double SamePerson = 0.4;

    private const int DetectorSize = 640;
    private const int AlignedSize = 112;
    private const float MinScore = 0.8f;
    // Smaller faces (in the decoded photo) are too blurry to recognise reliably.
    private const int MinFacePixels = 28;

    // Where the eyes, nose tip and mouth corners sit on the 112 × 112 face SFace was trained on.
    private static readonly double[,] Template =
    {
        { 38.2946, 51.6963 }, { 73.5318, 51.5014 }, { 56.0252, 71.7366 }, { 41.5493, 92.3655 }, { 70.7299, 92.2041 },
    };

    private readonly InferenceSession _detector;
    private readonly InferenceSession _recognizer;

    private FaceAnalyzer(InferenceSession detector, InferenceSession recognizer)
    {
        _detector = detector;
        _recognizer = recognizer;
    }

    /// <summary>Loads the models from <paramref name="folder"/> (see <see cref="FaceModel"/>).</summary>
    public static FaceAnalyzer Load(string folder)
    {
        using var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_BASIC };
        // Leave a core free so the app stays responsive while a batch is analysed.
        options.IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount - 1);
        options.LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR;
        var detector = new InferenceSession(Path.Combine(folder, FaceModel.DetectorFile), options);
        try
        {
            return new FaceAnalyzer(detector, new InferenceSession(Path.Combine(folder, FaceModel.RecognizerFile), options));
        }
        catch
        {
            detector.Dispose();
            throw;
        }
    }

    /// <summary>How alike two faces are, from about -1 to 1; see <see cref="SamePerson"/>.</summary>
    public static double Similarity(float[] a, float[] b)
    {
        double sum = 0;
        for (var i = 0; i < a.Length && i < b.Length; i++)
            sum += a[i] * b[i];
        return sum;
    }

    /// <summary>Finds and describes the faces on an upright photo given as 8-bit BGRA pixels.</summary>
    public IReadOnlyList<DetectedFace> Analyze(byte[] bgra, int width, int height)
    {
        var found = Detect(bgra, width, height, 0, 0, width, height);
        if (Math.Max(width, height) > DetectorSize * 1.4)
        {
            // Four quarters, each 60 % of the photo's width and height, so a face on a seam is whole in one of them.
            var tileW = (int)(width * 0.6);
            var tileH = (int)(height * 0.6);
            foreach (var (x, y) in new[] { (0, 0), (width - tileW, 0), (0, height - tileH), (width - tileW, height - tileH) })
                found.AddRange(Detect(bgra, width, height, x, y, tileW, tileH));
        }

        var faces = new List<DetectedFace>();
        foreach (var face in Suppress(found))
        {
            if (face.Width < MinFacePixels || face.Height < MinFacePixels)
                continue;
            var box = new FaceBox((double)face.X / width, (double)face.Y / height, (double)face.Width / width, (double)face.Height / height).Clamp();
            faces.Add(new DetectedFace(box, face.Score, Embed(bgra, width, height, face.Landmarks)));
        }
        return faces;
    }

    internal sealed record Candidate(float Score, float X, float Y, float Width, float Height, float[] Landmarks)
    {
        public FaceBox Box => new(X, Y, Width, Height);
    }

    /// <summary>Runs the detector on one part of the photo; boxes and landmarks are in the photo's pixels.</summary>
    private List<Candidate> Detect(byte[] bgra, int width, int height, int left, int top, int partW, int partH)
    {
        var scale = (float)DetectorSize / Math.Max(partW, partH);
        // YuNet takes BGR values from 0 to 255; the part is scaled to fit and the rest left black.
        var input = new float[3 * DetectorSize * DetectorSize];
        var outW = Math.Min(DetectorSize, (int)Math.Round(partW * scale));
        var outH = Math.Min(DetectorSize, (int)Math.Round(partH * scale));
        const int plane = DetectorSize * DetectorSize;
        for (var y = 0; y < outH; y++)
        {
            for (var x = 0; x < outW; x++)
            {
                var (b, g, r) = Sample(bgra, width, height, left + (x + 0.5f) / scale - 0.5f, top + (y + 0.5f) / scale - 0.5f, 1 / scale);
                var i = y * DetectorSize + x;
                input[i] = b;
                input[plane + i] = g;
                input[2 * plane + i] = r;
            }
        }

        using var results = _detector.Run([NamedOnnxValue.CreateFromTensor("input", new DenseTensor<float>(input, [1, 3, DetectorSize, DetectorSize]))]);
        var outputs = results.ToDictionary(r => r.Name, r => r.AsTensor<float>().ToArray());

        var found = new List<Candidate>();
        foreach (var stride in new[] { 8, 16, 32 })
        {
            var cls = outputs[$"cls_{stride}"];
            var obj = outputs[$"obj_{stride}"];
            var bbox = outputs[$"bbox_{stride}"];
            var kps = outputs[$"kps_{stride}"];
            var cols = DetectorSize / stride;
            for (var i = 0; i < cls.Length; i++)
            {
                var score = MathF.Sqrt(Math.Clamp(cls[i], 0, 1) * Math.Clamp(obj[i], 0, 1));
                if (score < MinScore)
                    continue;
                var row = i / cols;
                var col = i % cols;
                var cx = (col + bbox[i * 4]) * stride;
                var cy = (row + bbox[i * 4 + 1]) * stride;
                var w = MathF.Exp(bbox[i * 4 + 2]) * stride;
                var h = MathF.Exp(bbox[i * 4 + 3]) * stride;
                var landmarks = new float[10];
                for (var n = 0; n < 5; n++)
                {
                    landmarks[2 * n] = left + (col + kps[i * 10 + 2 * n]) * stride / scale;
                    landmarks[2 * n + 1] = top + (row + kps[i * 10 + 2 * n + 1]) * stride / scale;
                }
                found.Add(new Candidate(score, left + (cx - w / 2) / scale, top + (cy - h / 2) / scale, w / scale, h / scale, landmarks));
            }
        }
        return found;
    }

    /// <summary>Keeps the surest of overlapping detections, including a part of a face found inside a whole one.</summary>
    internal static List<Candidate> Suppress(IEnumerable<Candidate> candidates)
    {
        var kept = new List<Candidate>();
        foreach (var c in candidates.OrderByDescending(c => c.Score))
        {
            if (!kept.Any(k => k.Box.IoU(c.Box) > 0.3 || k.Box.Overlap(c.Box) > 0.6))
                kept.Add(c);
        }
        return kept;
    }

    /// <summary>Turns the face upright and to the standard size with its landmarks, then describes it with SFace.</summary>
    private float[] Embed(byte[] bgra, int width, int height, float[] landmarks)
    {
        var (a, b, tx, ty) = SimilarityTransform(landmarks);
        // The inverse maps each pixel of the 112 × 112 face back onto the photo.
        var det = a * a + b * b;
        var ia = a / det;
        var ib = -b / det;
        var shrink = Math.Sqrt(det);

        const int plane = AlignedSize * AlignedSize;
        var input = new float[3 * plane];
        for (var y = 0; y < AlignedSize; y++)
        {
            for (var x = 0; x < AlignedSize; x++)
            {
                var dx = x - tx;
                var dy = y - ty;
                var sx = ia * dx - ib * dy;
                var sy = ib * dx + ia * dy;
                // SFace takes RGB values from 0 to 255.
                var (bl, g, r) = Sample(bgra, width, height, (float)sx, (float)sy, (float)(1 / shrink));
                var i = y * AlignedSize + x;
                input[i] = r;
                input[plane + i] = g;
                input[2 * plane + i] = bl;
            }
        }

        using var results = _recognizer.Run([NamedOnnxValue.CreateFromTensor("data", new DenseTensor<float>(input, [1, 3, AlignedSize, AlignedSize]))]);
        var vector = results[0].AsTensor<float>().ToArray();
        var length = MathF.Sqrt(vector.Sum(v => v * v));
        for (var i = 0; i < vector.Length; i++)
            vector[i] /= length;
        return vector;
    }

    /// <summary>
    /// The rotation, scale and shift that best moves the five landmarks onto <see cref="Template"/>
    /// (Umeyama's method): x' = a·x − b·y + tx, y' = b·x + a·y + ty.
    /// </summary>
    internal static (double A, double B, double Tx, double Ty) SimilarityTransform(float[] landmarks)
    {
        double sx = 0, sy = 0, dx = 0, dy = 0;
        for (var n = 0; n < 5; n++)
        {
            sx += landmarks[2 * n];
            sy += landmarks[2 * n + 1];
            dx += Template[n, 0];
            dy += Template[n, 1];
        }
        sx /= 5;
        sy /= 5;
        dx /= 5;
        dy /= 5;

        // For a rotation and uniform scale the least-squares answer has a closed form.
        double dot = 0, cross = 0, norm = 0;
        for (var n = 0; n < 5; n++)
        {
            var px = landmarks[2 * n] - sx;
            var py = landmarks[2 * n + 1] - sy;
            var qx = Template[n, 0] - dx;
            var qy = Template[n, 1] - dy;
            dot += px * qx + py * qy;
            cross += px * qy - py * qx;
            norm += px * px + py * py;
        }
        var a = dot / norm;
        var b = cross / norm;
        return (a, b, dx - (a * sx - b * sy), dy - (b * sx + a * sy));
    }

    /// <summary>
    /// The colour at a point of the photo, averaged over about <paramref name="footprint"/> pixels when the
    /// photo is being shrunk, so small copies are smooth rather than jagged. Outside the photo is black.
    /// </summary>
    private static (float B, float G, float R) Sample(byte[] bgra, int width, int height, float x, float y, float footprint)
    {
        if (footprint <= 1.5f)
            return Bilinear(bgra, width, height, x, y);
        // Average a small grid of bilinear samples across the footprint.
        var steps = Math.Min(4, (int)MathF.Ceiling(footprint));
        float b = 0, g = 0, r = 0;
        for (var j = 0; j < steps; j++)
        {
            for (var i = 0; i < steps; i++)
            {
                var (sb, sg, sr) = Bilinear(bgra, width, height,
                    x + ((i + 0.5f) / steps - 0.5f) * footprint, y + ((j + 0.5f) / steps - 0.5f) * footprint);
                b += sb;
                g += sg;
                r += sr;
            }
        }
        var n = steps * steps;
        return (b / n, g / n, r / n);
    }

    private static (float B, float G, float R) Bilinear(byte[] bgra, int width, int height, float x, float y)
    {
        if (x < -0.5f || y < -0.5f || x > width - 0.5f || y > height - 0.5f)
            return (0, 0, 0);
        x = Math.Clamp(x, 0, width - 1);
        y = Math.Clamp(y, 0, height - 1);
        var x0 = (int)x;
        var y0 = (int)y;
        var x1 = Math.Min(x0 + 1, width - 1);
        var y1 = Math.Min(y0 + 1, height - 1);
        var fx = x - x0;
        var fy = y - y0;
        float Channel(int c) =>
            (bgra[(y0 * width + x0) * 4 + c] * (1 - fx) + bgra[(y0 * width + x1) * 4 + c] * fx) * (1 - fy) +
            (bgra[(y1 * width + x0) * 4 + c] * (1 - fx) + bgra[(y1 * width + x1) * 4 + c] * fx) * fy;
        return (Channel(0), Channel(1), Channel(2));
    }

    public void Dispose()
    {
        _detector.Dispose();
        _recognizer.Dispose();
    }
}
