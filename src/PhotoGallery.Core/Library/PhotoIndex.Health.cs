namespace PhotoGallery.Core.Library;

/// <summary>The results of the quick file check (<see cref="FileCheck"/>), kept per photo.</summary>
public sealed partial class PhotoIndex
{
    /// <summary>Photos not checked yet, or changed since they were, newest first.</summary>
    public IReadOnlyList<(string Path, DateTime FileModifiedUtc)> GetUnchecked(int limit)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT p.path, p.file_mtime FROM photos p
                WHERE NOT EXISTS (SELECT 1 FROM file_checks c WHERE c.photo_id = p.id AND c.file_mtime = p.file_mtime)
                ORDER BY p.id DESC LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$limit", limit);
            var result = new List<(string, DateTime)>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                result.Add((r.GetString(0), new DateTime(r.GetInt64(1), DateTimeKind.Utc)));
            return result;
        }
    }

    /// <summary>Notes what the check found for the file as it was at <paramref name="fileModifiedUtc"/>.</summary>
    public void SaveCheck(string path, FileHealth health, DateTime fileModifiedUtc)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO file_checks (photo_id, health, file_mtime)
                SELECT id, $health, $mtime FROM photos WHERE path = $path
                ON CONFLICT (photo_id) DO UPDATE SET health = excluded.health, file_mtime = excluded.file_mtime;
                """;
            cmd.Parameters.AddWithValue("$path", path);
            cmd.Parameters.AddWithValue("$health", (int)health);
            cmd.Parameters.AddWithValue("$mtime", fileModifiedUtc.Ticks);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>How many photos the check found damaged (and not changed since).</summary>
    public int CountDamaged()
    {
        lock (_lock)
            return Convert.ToInt32(Scalar("""
                SELECT COUNT(*) FROM file_checks c JOIN photos p ON p.id = c.photo_id
                WHERE c.health <> 0 AND c.file_mtime = p.file_mtime;
                """));
    }

    /// <summary>Other photos in the library with this file name: maybe copies of a damaged one.</summary>
    public IReadOnlyList<string> FindByFileName(string fileName, string except)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT path FROM photos WHERE file_name = $name AND path <> $except ORDER BY path LIMIT 20;";
            cmd.Parameters.AddWithValue("$name", fileName);
            cmd.Parameters.AddWithValue("$except", except);
            var result = new List<string>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                result.Add(r.GetString(0));
            return result;
        }
    }
}
