using PhotoGallery.Core.Tagging;

namespace PhotoGallery.Core.Tests;

/// <summary>
/// A test that needs the downloaded tagging model; the folder is given in PHOTOGALLERY_TAG_MODEL
/// (CI downloads it, see .github/workflows/build.yml). Skipped otherwise.
/// </summary>
public sealed class TagModelFactAttribute : FactAttribute
{
    public static string? Folder => Environment.GetEnvironmentVariable("PHOTOGALLERY_TAG_MODEL");

    public TagModelFactAttribute()
    {
        if (Folder is null || !TagModel.IsInstalled(Folder))
            Skip = "The tagging model is not downloaded (set PHOTOGALLERY_TAG_MODEL to its folder).";
    }
}

public sealed class ImageTaggerTests
{
    // Reference ids from the Python tokenizer of onnx-community/siglip2-base-patch16-224-ONNX.
    [TagModelFact]
    public void Tokenizes_like_the_original()
    {
        using var tagger = ImageTagger.Load(TagModelFactAttribute.Folder!);
        Assert.Equal([883, 603, 476, 2686, 576, 8318, 235265, 1, 0, 0], tagger.Tokenize("this is a photo of beach.").Take(10));
        Assert.Equal([883, 603, 476, 2686, 576, 157520, 201082, 235265, 1, 0], tagger.Tokenize("This is a photo of Sonnenuntergang.").Take(10));
        Assert.Equal(64, tagger.Tokenize("x").Length);
    }

    // Two cats on a sofa (COCO val2017 000000039769), downloaded next to the model by CI.
    [TagModelFact]
    public void Recognises_cats_in_english_and_german()
    {
        using var tagger = ImageTagger.Load(TagModelFactAttribute.Folder!);
        var image = TestImages.Decode(Path.Combine(TagModelFactAttribute.Folder!, "cats.jpg"))!.Value;
        var vector = tagger.EmbedImage(image.Pixels, image.Width, image.Height);

        foreach (var (language, expected) in new[] { ("en", "cat"), ("de", "Katze") })
        {
            var labels = AutoTags.StarterList(language);
            var texts = tagger.EmbedTexts(labels.Select(l => AutoTags.Prompt(l, language)).ToList());
            var scores = ImageTagger.Scores(vector, texts);
            var picked = AutoTags.Pick(labels, scores, 0.15);
            Assert.Equal(expected, picked.FirstOrDefault());
            Assert.True(scores.Max() > 0.6, $"{language}: best score only {scores.Max():P0}");
            Assert.DoesNotContain(language == "de" ? "Strand" : "beach", picked);
        }
    }
}
