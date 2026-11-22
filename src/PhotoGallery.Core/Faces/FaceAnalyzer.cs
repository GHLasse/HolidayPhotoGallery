using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace PhotoGallery.Core.Faces;

/// <summary>A face found on a photo.</summary>
/// <param name="Box">Where it is on the upright photo, as fractions of its width and height.</param>
/// <param name="Score">How sure the detector is that it is a face, 0 to 1.</param>
/// <param name="Embedding">A unit-length vector; faces of the same person point the same way (see <see cref="FaceAnalyzer.Similarity"/>).</param>
public sealed record DetectedFace(FaceBox Box, float Score, float[] Embedding)
{
    /// <summary>
    /// The eyes, the nose tip and the mouth corners as fractions of the photo's width and height, in that order and
    /// from left to right as seen on the photo.
    /// </summary>
    public IReadOnlyList<(double X, double Y)> Points { get; init; } = [];
}

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
    // Faces the detector is this sure of are kept as they are.
    internal const float SureScore = 0.8f;
    // Blurry faces and closed eyes often score lower (Lasse, 2026-11-21). They are kept from here on when a
    // second look (the photo mirrored, or a quarter of it) finds the same face, the eyes, nose and mouth sit
    // where a face has them, and the face is not tiny, so stray patterns don't become faces.
    internal const float LikelyScore = 0.6f;
    private const float SecondLookScore = 0.45f;
    private const int MinLikelyFacePixels = 40;
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
        // Each part is also looked at mirrored: the detector is not symmetric, so a doubtful face gets a
        // second chance, and a face found both ways is very likely real.
        var found = new List<Candidate>();
        var pass = 0;
        found.AddRange(Detect(bgra, width, height, 0, 0, width, height, false, pass++));
        found.AddRange(Detect(bgra, width, height, 0, 0, width, height, true, pass++));
        if (Math.Max(width, height) > DetectorSize * 1.4)
        {
            // Four quarters, each 60 % of the photo's width and height, so a face on a seam is whole in one of them.
            var tileW = (int)(width * 0.6);
            var tileH = (int)(height * 0.6);
            foreach (var (x, y) in new[] { (0, 0), (width - tileW, 0), (0, height - tileH), (width - tileW, height - tileH) })
            {
                found.AddRange(Detect(bgra, width, height, x, y, tileW, tileH, false, pass++));
                found.AddRange(Detect(bgra, width, height, x, y, tileW, tileH, true, pass++));
            }
        }

        var faces = new List<DetectedFace>();
        foreach (var face in Suppress(Believable(found)))
        {
            if (face.Width < MinFacePixels || face.Height < MinFacePixels)
                continue;
            var box = new FaceBox((double)face.X / width, (double)face.Y / height, (double)face.Width / width, (double)face.Height / height).Clamp();
            var points = Enumerable.Range(0, 5).Select(n => ((double)face.Landmarks[2 * n] / width, (double)face.Landmarks[2 * n + 1] / height)).ToList();
            faces.Add(new DetectedFace(box, face.Score, Embed(bgra, width, height, face.Landmarks)) { Points = points });
        }
        return faces;
    }

    /// <summary>
    /// Finds the eyes, nose and mouth of faces that are already known, for example from the People scan, by looking
    /// only around each box once. Much quicker than <see cref="Analyze"/>, which searches the whole photo several
    /// times and describes each face; the faces come back without a description (an empty embedding).
    /// </summary>
    public IReadOnlyList<DetectedFace> Locate(byte[] bgra, int width, int height, IEnumerable<FaceBox> known)
    {
        var faces = new List<DetectedFace>();
        foreach (var box in known)
        {
            var (cx, cy) = ((box.X + box.Width / 2) * width, (box.Y + box.Height / 2) * height);
            // Room around the face, so a loose or tight box from another program still holds the whole face.
            var side = Math.Max(64, Math.Max(box.Width * width, box.Height * height) * 2.2);
            var left = (int)Math.Clamp(cx - side / 2, 0, Math.Max(0, width - 1));
            var top = (int)Math.Clamp(cy - side / 2, 0, Math.Max(0, height - 1));
            var (partW, partH) = (Math.Min((int)side, width - left), Math.Min((int)side, height - top));
            if (partW < MinFacePixels || partH < MinFacePixels)
                continue;
            var best = Detect(bgra, width, height, left, top, partW, partH, false, 0)
                .Where(c => c.Score >= LikelyScore)
                .OrderBy(c => Math.Pow(c.X + c.Width / 2 - cx, 2) + Math.Pow(c.Y + c.Height / 2 - cy, 2))
                .FirstOrDefault();
            if (best is null)
                continue;
            var found = new FaceBox((double)best.X / width, (double)best.Y / height, (double)best.Width / width, (double)best.Height / height).Clamp();
            var points = Enumerable.Range(0, 5).Select(n => ((double)best.Landmarks[2 * n] / width, (double)best.Landmarks[2 * n + 1] / height)).ToList();
            faces.Add(new DetectedFace(found, best.Score, []) { Points = points });
        }
        return faces;
    }

    /// <param name="Pass">Which look at the photo found it (a part, straight or mirrored).</param>
    internal sealed record Candidate(float Score, float X, float Y, float Width, float Height, float[] Landmarks, int Pass = 0)
    {
        public FaceBox Box => new(X, Y, Width, Height);
    }

    /// <summary>Runs the detector on one part of the photo; boxes and landmarks are in the photo's pixels.</summary>
    private List<Candidate> Detect(byte[] bgra, int width, int height, int left, int top, int partW, int partH, bool mirrored, int pass)
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
                var sx = mirrored ? outW - 1 - x : x;
                var (b, g, r) = Sample(bgra, width, height, left + (sx + 0.5f) / scale - 0.5f, top + (y + 0.5f) / scale - 0.5f, 1 / scale);
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
                if (score < SecondLookScore)
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
                var boxX = cx - w / 2;
                if (mirrored)
                {
                    // Back to the photo's side; the mirrored right eye is the left one, and so on.
                    boxX = outW - boxX - w;
                    for (var n = 0; n < 5; n++)
                        landmarks[2 * n] = left + (outW - (col + kps[i * 10 + 2 * n]) * stride) / scale;
                    (landmarks[0], landmarks[2]) = (landmarks[2], landmarks[0]);
                    (landmarks[1], landmarks[3]) = (landmarks[3], landmarks[1]);
                    (landmarks[6], landmarks[8]) = (landmarks[8], landmarks[6]);
                    (landmarks[7], landmarks[9]) = (landmarks[9], landmarks[7]);
                }
                found.Add(new Candidate(score, left + boxX / scale, top + (cy - h / 2) / scale, w / scale, h / scale, landmarks, pass));
            }
        }
        return found;
    }

    /// <summary>
    /// The detections that are believable faces: sure ones, and likely ones (blurry, eyes closed) that another
    /// look at the photo found too, have a face's layout and are not tiny. Weaker ones only serve as that
    /// second look.
    /// </summary>
    internal static List<Candidate> Believable(IReadOnlyList<Candidate> candidates) =>
        candidates.Where(c => c.Score >= SureScore ||
                              (c.Score >= LikelyScore && c.Width >= MinLikelyFacePixels && c.Height >= MinLikelyFacePixels &&
                               HasFaceLayout(c) &&
                               candidates.Any(o => o.Pass != c.Pass && o.Box.IoU(c.Box) > 0.4)))
                  .ToList();

    /// <summary>Whether the eyes, nose and mouth sit where they do on a face, roughly upright or tilted.</summary>
    internal static bool HasFaceLayout(Candidate c)
    {
        var l = c.Landmarks;
        double eyeX = l[2] - l[0], eyeY = l[3] - l[1];
        var eyes = Math.Sqrt(eyeX * eyeX + eyeY * eyeY);
        if (eyes < 0.2 * c.Width || eyes > 0.75 * c.Width)
            return false;
        // Along the face, from between the eyes towards the mouth.
        double midX = (l[0] + l[2]) / 2, midY = (l[1] + l[3]) / 2;
        double mouthX = (l[6] + l[8]) / 2 - midX, mouthY = (l[7] + l[9]) / 2 - midY;
        var down = Math.Sqrt(mouthX * mouthX + mouthY * mouthY);
        if (down < 0.5 * eyes || down > 2 * eyes)
            return false;
        // The face's down direction is about at right angles to the eyes, and the nose lies between eyes and mouth.
        if (Math.Abs(eyeX * mouthX + eyeY * mouthY) > 0.5 * eyes * down)
            return false;
        var nose = ((l[4] - midX) * mouthX + (l[5] - midY) * mouthY) / (down * down);
        return nose > 0.1 && nose < 1;
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
