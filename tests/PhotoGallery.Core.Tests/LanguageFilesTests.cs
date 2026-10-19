using System.Text.RegularExpressions;
using PhotoGallery.Core.Localization;

namespace PhotoGallery.Core.Tests;

public partial class LanguageFilesTests
{
    private static string LanguagesFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var folder = Path.Combine(dir.FullName, "src", "PhotoGallery.App", "Languages");
            if (Directory.Exists(folder))
                return folder;
        }
        throw new DirectoryNotFoundException("src/PhotoGallery.App/Languages");
    }

    [GeneratedRegex(@"\{(\d+)(?:[,:][^}]*)?\}")]
    private static partial Regex Placeholder();

    private static string[] Placeholders(string text) =>
        Placeholder().Matches(text.Replace("{{", "").Replace("}}", "")).Select(m => m.Groups[1].Value).Distinct().Order().ToArray();

    [Fact]
    public void Shipped_languages_translate_every_text_with_the_same_placeholders()
    {
        var folder = LanguagesFolder();
        var template = File.ReadAllText(Path.Combine(folder, "template.json"));
        Assert.Null(Strings.Read(Path.Combine(folder, "template.json")));
        var keys = System.Text.Json.JsonDocument.Parse(template).RootElement.GetProperty("texts").EnumerateObject().Select(p => p.Name).ToList();
        Assert.NotEmpty(keys);

        var packs = Strings.Available([folder]);
        Assert.Equal(["ar", "de", "en", "es", "zh-Hans"], packs.Select(p => p.Code).Order(StringComparer.Ordinal));
        Assert.True(packs.Single(p => p.Code == "ar").RightToLeft);
        foreach (var pack in packs.Where(p => p.Code != Strings.English))
        {
            var untranslated = keys.Where(k => !pack.Texts.TryGetValue(k, out var t) || string.IsNullOrWhiteSpace(t)).ToList();
            Assert.True(untranslated.Count == 0, $"{pack.Code} misses: {string.Join(" | ", untranslated.Take(10))}");
            var broken = keys.Where(k => !Placeholders(k).SequenceEqual(Placeholders(pack.Texts[k]))).ToList();
            Assert.True(broken.Count == 0, $"{pack.Code} placeholders differ: {string.Join(" | ", broken.Take(10))}");
            foreach (var key in keys.Where(k => Placeholders(k).Length > 0))
                _ = string.Format(pack.Texts[key], Enumerable.Repeat<object?>(1, 10).ToArray());
        }
    }

    [Fact]
    public void A_language_file_in_the_user_folder_wins_and_bad_files_are_ignored()
    {
        using var shipped = new TempFolder();
        using var user = new TempFolder();
        File.WriteAllText(Path.Combine(shipped.Path, "de.json"), """{"code":"de","name":"Deutsch","texts":{"Settings":"Einstellungen"}}""");
        File.WriteAllText(Path.Combine(user.Path, "de.json"), """{"code":"de","name":"Deutsch (mine)","texts":{"Settings":"Optionen"}}""");
        File.WriteAllText(Path.Combine(user.Path, "broken.json"), "{ not json");
        File.WriteAllText(Path.Combine(user.Path, "template.json"), """{"code":"","name":"","texts":{"Settings":""}}""");

        var packs = Strings.Available([shipped.Path, user.Path]);

        Assert.Equal(["en", "de"], packs.Select(p => p.Code));
        Assert.Equal("Optionen", packs[1].Texts["Settings"]);
    }
}
