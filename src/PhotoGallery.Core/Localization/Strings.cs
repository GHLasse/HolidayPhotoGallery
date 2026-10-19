using System.Globalization;
using System.Text.Json;

namespace PhotoGallery.Core.Localization;

/// <summary>A language the app can be shown in: built in, or a language file added to the Languages folder.</summary>
public sealed record LanguagePack(string Code, string Name, bool RightToLeft, IReadOnlyDictionary<string, string> Texts);

/// <summary>
/// The app's texts in the chosen language. The code is written in English and wraps every text a user
/// reads in <see cref="Tr(string)"/>; a language file maps each English text to its translation, so a
/// missing or empty translation simply stays English. Texts with values use {0}, {1}… placeholders.
/// </summary>
/// <remarks>
/// A language file is JSON: <c>{ "code": "de", "name": "Deutsch", "rightToLeft": false, "texts": { "Settings": "Einstellungen" } }</c>.
/// The app reads the ones it ships with and those in the Languages folder of its data folder, where a
/// new language can be added without changing the app (Languages/template.json lists every text).
/// </remarks>
public static class Strings
{
    public const string English = "en";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static IReadOnlyDictionary<string, string> _texts = new Dictionary<string, string>();

    /// <summary>The language shown now, e.g. "de".</summary>
    public static string Language { get; private set; } = English;

    /// <summary>Whether the language is written from right to left (Arabic), so the window is mirrored.</summary>
    public static bool RightToLeft { get; private set; }

    /// <summary>The text in the chosen language.</summary>
    public static string Tr(string english) =>
        _texts.TryGetValue(english, out var text) && !string.IsNullOrWhiteSpace(text) ? text : english;

    /// <summary>The text in the chosen language, with its {0}, {1}… filled in.</summary>
    public static string Tr(string english, params object?[] values)
    {
        try
        {
            return string.Format(CultureInfo.CurrentCulture, Tr(english), values);
        }
        catch (FormatException)
        {
            // A translation with broken placeholders: the English text still works.
            return string.Format(CultureInfo.CurrentCulture, english, values);
        }
    }

    /// <summary>English and every language file in <paramref name="folders"/>; a later folder's file wins for the same language.</summary>
    public static IReadOnlyList<LanguagePack> Available(IEnumerable<string> folders)
    {
        var packs = new Dictionary<string, LanguagePack>(StringComparer.OrdinalIgnoreCase)
        {
            [English] = new(English, "English", false, new Dictionary<string, string>()),
        };
        foreach (var folder in folders.Where(Directory.Exists))
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*.json").Order(StringComparer.OrdinalIgnoreCase))
            {
                if (Read(file) is { } pack && pack.Code != English)
                    packs[pack.Code] = pack;
            }
        }
        return packs.Values.OrderBy(p => p.Code == English ? 0 : 1).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Reads a language file; null when it isn't one (the template, or a damaged file).</summary>
    public static LanguagePack? Read(string file)
    {
        try
        {
            var data = JsonSerializer.Deserialize<LanguageFile>(File.ReadAllText(file), JsonOptions);
            if (data is not { Code.Length: > 0, Name.Length: > 0 })
                return null;
            return new LanguagePack(data.Code.Trim(), data.Name.Trim(), data.RightToLeft,
                                    new Dictionary<string, string>(data.Texts ?? [], StringComparer.Ordinal));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Shows the app in <paramref name="code"/>; null picks Windows' language when the app has it, else English.
    /// Returns the language chosen.
    /// </summary>
    public static string Use(string? code, IEnumerable<string> folders)
    {
        var packs = Available(folders);
        code ??= CultureInfo.CurrentUICulture.Name;
        // "de-AT" uses "de"; "zh-CN" uses "zh-Hans" when that is how the file is named.
        var pack = packs.FirstOrDefault(p => p.Code.Equals(code, StringComparison.OrdinalIgnoreCase))
                   ?? packs.FirstOrDefault(p => SameLanguage(p.Code, code))
                   ?? packs[0];
        Use(pack);
        return pack.Code;
    }

    public static void Use(LanguagePack pack)
    {
        _texts = pack.Texts;
        Language = pack.Code;
        RightToLeft = pack.RightToLeft;
    }

    private static bool SameLanguage(string a, string b)
    {
        try
        {
            return CultureInfo.GetCultureInfo(a).TwoLetterISOLanguageName == CultureInfo.GetCultureInfo(b).TwoLetterISOLanguageName;
        }
        catch (CultureNotFoundException)
        {
            return false;
        }
    }

    private sealed class LanguageFile
    {
        public string? Code { get; set; }

        public string? Name { get; set; }

        public bool RightToLeft { get; set; }

        public Dictionary<string, string>? Texts { get; set; }
    }
}
