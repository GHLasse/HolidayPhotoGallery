using PhotoGallery.Core.Faces;
using PhotoGallery.Core.Editing;

namespace PhotoGallery.Core.Tests;

/// <summary>
/// A test that needs the downloaded face models and sample portraits; the folder is given in
/// PHOTOGALLERY_FACE_MODEL (CI downloads them, see .github/workflows/build.yml). Skipped otherwise.
/// </summary>
public sealed class FaceModelFactAttribute : FactAttribute
{
    public static string? Folder => Environment.GetEnvironmentVariable("PHOTOGALLERY_FACE_MODEL");

    public FaceModelFactAttribute()
    {
        if (Folder is null || !FaceModel.IsInstalled(Folder))
            Skip = "The face models are not downloaded (set PHOTOGALLERY_FACE_MODEL to their folder).";
    }
}

public sealed class FaceAnalyzerTests
{
    [Fact]
    public void Faces_already_in_place_need_no_transform()
    {
        float[] template = [38.2946f, 51.6963f, 73.5318f, 51.5014f, 56.0252f, 71.7366f, 41.5493f, 92.3655f, 70.7299f, 92.2041f];
        var (a, b, tx, ty) = FaceAnalyzer.SimilarityTransform(template);
        Assert.Equal(1, a, 4);
        Assert.Equal(0, b, 4);
        Assert.Equal(0, tx, 3);
        Assert.Equal(0, ty, 3);

        // The same face twice as big and shifted is shrunk back.
        var moved = template.Select((v, i) => v * 2 + (i % 2 == 0 ? 100 : 50)).ToArray();
        (a, b, _, _) = FaceAnalyzer.SimilarityTransform(moved);
        Assert.Equal(0.5, a, 4);
        Assert.Equal(0, b, 4);
    }

    [Fact]
    public void Keeps_the_surest_of_overlapping_detections()
    {
        var kept = FaceAnalyzer.Suppress(
        [
            new(0.85f, 10, 10, 100, 100, new float[10]),
            new(0.95f, 15, 12, 100, 100, new float[10]),
            new(0.9f, 40, 40, 30, 30, new float[10]),
            new(0.9f, 300, 10, 100, 100, new float[10]),
        ]);
        Assert.Equal([0.95f, 0.9f], kept.Select(k => k.Score));
        Assert.Equal(300, kept[1].X);
    }

    // Eyes, nose and mouth of an upright 100 px face at (0, 0).
    private static readonly float[] Upright = [30, 40, 70, 40, 50, 60, 35, 80, 65, 80];

    [Fact]
    public void Knows_the_layout_of_a_face()
    {
        Assert.True(FaceAnalyzer.HasFaceLayout(new(0.7f, 0, 0, 100, 100, Upright)));
        // Tilted by 90 degrees (someone lying down) is still a face.
        float[] tilted = [.. Enumerable.Range(0, 5).SelectMany(n => new[] { 100 - Upright[2 * n + 1], Upright[2 * n] })];
        Assert.True(FaceAnalyzer.HasFaceLayout(new(0.7f, 0, 0, 100, 100, tilted)));
        // Nose below the mouth, eyes on top of each other, mouth beside the eyes: not a face.
        Assert.False(FaceAnalyzer.HasFaceLayout(new(0.7f, 0, 0, 100, 100, [30, 40, 70, 40, 50, 95, 35, 80, 65, 80])));
        Assert.False(FaceAnalyzer.HasFaceLayout(new(0.7f, 0, 0, 100, 100, [50, 30, 52, 40, 50, 60, 35, 80, 65, 80])));
        Assert.False(FaceAnalyzer.HasFaceLayout(new(0.7f, 0, 0, 100, 100, [30, 40, 70, 40, 75, 42, 85, 38, 90, 45])));
    }

    [Fact]
    public void Keeps_a_doubtful_face_only_when_a_second_look_finds_it_too()
    {
        // Sure: kept on its own. Likely (blurry, eyes closed): only with a second look from another pass.
        var sure = new FaceAnalyzer.Candidate(0.85f, 0, 0, 100, 100, Upright, 0);
        var alone = new FaceAnalyzer.Candidate(0.7f, 300, 0, 100, 100, Upright.Select((v, i) => i % 2 == 0 ? v + 300 : v).ToArray(), 0);
        var seenTwice = new FaceAnalyzer.Candidate(0.7f, 600, 0, 100, 100, Upright.Select((v, i) => i % 2 == 0 ? v + 600 : v).ToArray(), 0);
        var mirrored = seenTwice with { Score = 0.5f, X = 605, Pass = 1 };
        var samePass = alone with { Score = 0.5f, X = 305 };
        var tiny = new FaceAnalyzer.Candidate(0.7f, 900, 0, 30, 30, [909, 12, 921, 12, 915, 18, 910, 24, 920, 24], 0);
        var tinyAgain = tiny with { Pass = 1 };

        var kept = FaceAnalyzer.Believable([sure, alone, seenTwice, mirrored, samePass, tiny, tinyAgain]);

        Assert.Equal([sure, seenTwice], kept);
    }

    // Portraits from the face_recognition project's examples, downloaded next to the models by CI.
    [FaceModelFact]
    public void Tells_people_apart_and_recognises_them_in_other_photos()
    {
        using var analyzer = FaceAnalyzer.Load(FaceModelFactAttribute.Folder!);
        var obama = Assert.Single(Analyze(analyzer, "obama.jpg"));
        var obama2 = Assert.Single(Analyze(analyzer, "obama2.jpg"));
        var biden = Assert.Single(Analyze(analyzer, "biden.jpg"));
        var both = Analyze(analyzer, "two_people.jpg").OrderBy(f => f.Box.X).ToList();

        Assert.Equal(2, both.Count);
        Assert.True(both[0].Box.Right < both[1].Box.X);
        Assert.InRange(FaceAnalyzer.Similarity(obama.Embedding, obama2.Embedding), FaceAnalyzer.SamePerson, 1);
        Assert.InRange(FaceAnalyzer.Similarity(obama.Embedding, both[0].Embedding), FaceAnalyzer.SamePerson, 1);
        Assert.InRange(FaceAnalyzer.Similarity(biden.Embedding, both[1].Embedding), FaceAnalyzer.SamePerson, 1);
        Assert.InRange(FaceAnalyzer.Similarity(obama.Embedding, biden.Embedding), -1, FaceAnalyzer.SamePerson - 0.1);
        Assert.InRange(FaceAnalyzer.Similarity(both[0].Embedding, both[1].Embedding), -1, FaceAnalyzer.SamePerson - 0.1);
    }

    [FaceModelFact]
    public void Finds_the_eyes_of_faces_already_known_by_looking_only_around_their_boxes()
    {
        using var analyzer = FaceAnalyzer.Load(FaceModelFactAttribute.Folder!);
        var image = TestImages.Decode(Path.Combine(FaceModelFactAttribute.Folder!, "two_people.jpg"))!.Value;
        var both = analyzer.Analyze(image.Pixels, image.Width, image.Height).OrderBy(f => f.Box.X).ToList();
        Assert.Equal(2, both.Count);

        // Boxes as another program may have stored them: looser and a little off.
        var loose = both.Select(f => new FaceBox(f.Box.X - f.Box.Width * 0.2, f.Box.Y - f.Box.Height * 0.1, f.Box.Width * 1.4, f.Box.Height * 1.3)).ToList();
        var located = analyzer.Locate(image.Pixels, image.Width, image.Height, loose).OrderBy(f => f.Box.X).ToList();

        Assert.Equal(2, located.Count);
        for (var k = 0; k < 2; k++)
        {
            for (var n = 0; n < 2; n++)
            {
                var (want, got) = (both[k].Points[n], located[k].Points[n]);
                var off = Math.Sqrt(Math.Pow((want.X - got.X) * image.Width, 2) + Math.Pow((want.Y - got.Y) * image.Height, 2));
                Assert.True(off < both[k].Box.Width * image.Width * 0.05, $"face {k} eye {n} is {off:F1} px off");
            }
        }
    }

    [FaceModelFact]
    public void Finds_a_small_face_in_a_big_photo()
    {
        using var analyzer = FaceAnalyzer.Load(FaceModelFactAttribute.Folder!);
        var reference = Assert.Single(Analyze(analyzer, "obama.jpg"));

        // The portrait shrunk to a corner of a large empty photo, as when someone stands further away.
        var portrait = TestImages.Decode(Path.Combine(FaceModelFactAttribute.Folder!, "obama.jpg"))!.Value;
        var (pw, ph) = (portrait.Width / 5, portrait.Height / 5);
        var small = ImageMath.Resize(portrait.Pixels, portrait.Width, portrait.Height, pw, ph);
        var (sw, sh) = (FaceAnalyzer.ImageSize, FaceAnalyzer.ImageSize * 3 / 4);
        var scene = new byte[sw * sh * 4];
        for (var i = 0; i < scene.Length; i += 4)
            (scene[i], scene[i + 1], scene[i + 2], scene[i + 3]) = (90, 120, 140, 255);
        for (var y = 0; y < ph && 600 + y < sh; y++)
            Array.Copy(small, y * pw * 4, scene, ((600 + y) * sw + 900) * 4, Math.Min(pw, sw - 900) * 4);

        var face = Assert.Single(analyzer.Analyze(scene, sw, sh));
        Assert.InRange(face.Box.X, 900.0 / sw, 1);
        Assert.InRange(FaceAnalyzer.Similarity(reference.Embedding, face.Embedding), FaceAnalyzer.SamePerson, 1);
    }

    [FaceModelFact]
    public void Finds_faces_in_a_blurry_photo()
    {
        using var analyzer = FaceAnalyzer.Load(FaceModelFactAttribute.Folder!);
        var sharp = Analyze(analyzer, "two_people.jpg").OrderBy(f => f.Box.X).ToList();

        // Shaken or out of focus: the same photo blurred, small and soft as from an old compact camera.
        var image = TestImages.Decode(Path.Combine(FaceModelFactAttribute.Folder!, "two_people.jpg"))!.Value;
        var (w, h) = (image.Width / 3, image.Height / 3);
        var small = ImageMath.Resize(image.Pixels, image.Width, image.Height, w, h);
        var blurry = ImageMath.Resize(ImageMath.Resize(small, w, h, w / 4, h / 4), w / 4, h / 4, w, h);
        var faces = analyzer.Analyze(blurry, w, h).OrderBy(f => f.Box.X).ToList();

        Assert.Equal(2, faces.Count);
        for (var i = 0; i < 2; i++)
            Assert.True(faces[i].Box.IoU(sharp[i].Box) > 0.5);
    }

    [FaceModelFact]
    public void Mirrored_look_finds_the_face_in_the_same_place()
    {
        using var analyzer = FaceAnalyzer.Load(FaceModelFactAttribute.Folder!);
        var face = Assert.Single(Analyze(analyzer, "obama.jpg"));
        // Eyes and mouth corners stay on their own sides after the mirrored look.
        Assert.True(face.Points[0].X < face.Points[1].X);
        Assert.True(face.Points[3].X < face.Points[4].X);
        Assert.True(face.Points[0].Y < face.Points[3].Y);
    }

    private static IReadOnlyList<DetectedFace> Analyze(FaceAnalyzer analyzer, string file)
    {
        var image = TestImages.Decode(Path.Combine(FaceModelFactAttribute.Folder!, file))!.Value;
        var scale = Math.Min(1.0, (double)FaceAnalyzer.ImageSize / Math.Max(image.Width, image.Height));
        var (w, h) = ((int)(image.Width * scale), (int)(image.Height * scale));
        return analyzer.Analyze(ImageMath.Resize(image.Pixels, image.Width, image.Height, w, h), w, h);
    }
}
