namespace PhotoGallery.App;

/// <summary>Finding people, albums and places by what is typed in the search box.</summary>
internal static class NameSearch
{
    /// <summary>"li" finds Liam, Li Wang and Wei Ling: any part of the name that starts with what was typed.</summary>
    public static bool Matches(string name, string query)
    {
        for (var i = name.IndexOf(query, StringComparison.CurrentCultureIgnoreCase); i >= 0;
             i = i + 1 < name.Length ? name.IndexOf(query, i + 1, StringComparison.CurrentCultureIgnoreCase) : -1)
        {
            if (i == 0 || !char.IsLetterOrDigit(name[i - 1]))
                return true;
        }
        return false;
    }
}
