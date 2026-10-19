using PhotoGallery.Core.Faces;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

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
    public void Finds_a_small_face_in_a_big_photo()
    {
        using var analyzer = FaceAnalyzer.Load(FaceModelFactAttribute.Folder!);
        var reference = Assert.Single(Analyze(analyzer, "obama.jpg"));

        // The portrait shrunk to a corner of a large empty photo, as when someone stands further away.
        using var portrait = Image.Load<Bgra32>(Path.Combine(FaceModelFactAttribute.Folder!, "obama.jpg"));
        portrait.Mutate(p => p.Resize(portrait.Width / 5, portrait.Height / 5));
        using var scene = new Image<Bgra32>(FaceAnalyzer.ImageSize, FaceAnalyzer.ImageSize * 3 / 4, new Bgra32(90, 120, 140, 255));
        scene.Mutate(s => s.DrawImage(portrait, new Point(900, 600), 1));

        var face = Assert.Single(Analyze(analyzer, scene));
        Assert.InRange(face.Box.X, 900.0 / scene.Width, 1);
        Assert.InRange(FaceAnalyzer.Similarity(reference.Embedding, face.Embedding), FaceAnalyzer.SamePerson, 1);
    }

    private static IReadOnlyList<DetectedFace> Analyze(FaceAnalyzer analyzer, string file)
    {
        using var image = Image.Load<Bgra32>(Path.Combine(FaceModelFactAttribute.Folder!, file));
        var scale = Math.Min(1.0, (double)FaceAnalyzer.ImageSize / Math.Max(image.Width, image.Height));
        image.Mutate(i => i.Resize((int)(image.Width * scale), (int)(image.Height * scale)));
        return Analyze(analyzer, image);
    }

    private static IReadOnlyList<DetectedFace> Analyze(FaceAnalyzer analyzer, Image<Bgra32> image)
    {
        var pixels = new byte[image.Width * image.Height * 4];
        image.CopyPixelDataTo(pixels);
        return analyzer.Analyze(pixels, image.Width, image.Height);
    }
}
