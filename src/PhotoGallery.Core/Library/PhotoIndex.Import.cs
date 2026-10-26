namespace PhotoGallery.Core.Library;

/// <summary>What importing (F-1) asks the index: whether a photo is already in the library.</summary>
public sealed partial class PhotoIndex
{
    /// <summary>Every (file name, size) in the library, private photos included, to skip photos imported before.</summary>
    public HashSet<(string Name, long Size)> GetNamesAndSizes()
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT file_name, file_size FROM main.photos;";
            var result = new HashSet<(string, long)>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                result.Add((r.GetString(0).ToLowerInvariant(), r.GetInt64(1)));
            return result;
        }
    }
}
