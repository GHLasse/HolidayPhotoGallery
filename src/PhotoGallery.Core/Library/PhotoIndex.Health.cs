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
                WHERE p.video = 0 AND NOT EXISTS (SELECT 1 FROM file_checks c WHERE c.photo_id = p.id AND c.file_mtime = p.file_mtime AND c.level >= $level)
                ORDER BY p.id DESC LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$limit", limit);
            cmd.Parameters.AddWithValue("$level", FileCheck.Level);
            var result = new List<(string, DateTime)>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                result.Add((r.GetString(0), new DateTime(r.GetInt64(1), DateTimeKind.Utc)));
            return result;
        }
    }

    /// <summary>
    /// Notes what the check found for the file as it was at <paramref name="fileModifiedUtc"/>. A damaged file
    /// can be <paramref name="misnamed"/> too; that is kept beside its damage.
    /// </summary>
    public void SaveCheck(string path, FileHealth health, DateTime fileModifiedUtc, bool misnamed = false)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO file_checks (photo_id, health, file_mtime, level, misnamed)
                SELECT id, $health, $mtime, $level, $misnamed FROM photos WHERE path = $path
                ON CONFLICT (photo_id) DO UPDATE SET health = excluded.health, file_mtime = excluded.file_mtime, level = excluded.level,
                    misnamed = excluded.misnamed;
                """;
            cmd.Parameters.AddWithValue("$misnamed", misnamed || health == FileHealth.Misnamed ? 1 : 0);
            cmd.Parameters.AddWithValue("$path", path);
            cmd.Parameters.AddWithValue("$health", (int)health);
            cmd.Parameters.AddWithValue("$mtime", fileModifiedUtc.Ticks);
            cmd.Parameters.AddWithValue("$level", FileCheck.Level);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>How many photos the check found damaged (and not changed since).</summary>
    public int CountDamaged()
    {
        lock (_lock)
            return Convert.ToInt32(Scalar($"""
                SELECT COUNT(*) FROM file_checks c JOIN photos p ON p.id = c.photo_id
                WHERE c.health NOT IN (0, {(int)FileHealth.Misnamed}) AND c.file_mtime = p.file_mtime;
                """));
    }

    /// <summary>The photos whose name claims the wrong format (and not changed since), with their index entries.</summary>
    public IReadOnlyList<IndexedPhoto> GetMisnamed() =>
        Search(new PhotoQuery { MisnamedOnly = true });

    /// <summary>How many photos have a name claiming the wrong format.</summary>
    public int CountMisnamed()
    {
        lock (_lock)
            return Convert.ToInt32(Scalar($"""
                SELECT COUNT(*) FROM file_checks c JOIN photos p ON p.id = c.photo_id
                WHERE (c.health = {(int)FileHealth.Misnamed} OR c.misnamed = 1) AND c.file_mtime = p.file_mtime;
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
