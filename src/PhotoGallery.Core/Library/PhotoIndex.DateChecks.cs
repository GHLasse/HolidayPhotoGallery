namespace PhotoGallery.Core.Library;

/// <summary>
/// The background date sync (<see cref="Metadata.DateSync"/>): which photos had their dates checked, for the file as
/// it was then, so a photo is looked at again only when it changed.
/// </summary>
public sealed partial class PhotoIndex
{
    /// <summary>Photos and videos whose dates were not checked yet, or that changed since, newest first.</summary>
    public IReadOnlyList<string> GetNotDateChecked(int limit)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT p.path FROM main.photos p
                WHERE NOT EXISTS (SELECT 1 FROM date_checks d WHERE d.photo_id = p.id AND d.file_mtime = p.file_mtime)
                ORDER BY p.id DESC LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$limit", limit);
            var result = new List<string>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                result.Add(r.GetString(0));
            return result;
        }
    }

    /// <summary>Notes that the photos' dates were checked (and synced where needed), for the files as the index has them now.</summary>
    public void SaveDateChecked(IEnumerable<string> paths)
    {
        lock (_lock)
        {
            using var tx = _connection.BeginTransaction();
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO date_checks (photo_id, file_mtime)
                SELECT id, file_mtime FROM main.photos WHERE path = $path
                ON CONFLICT (photo_id) DO UPDATE SET file_mtime = excluded.file_mtime;
                """;
            var path = cmd.Parameters.Add("$path", Microsoft.Data.Sqlite.SqliteType.Text);
            foreach (var p in paths)
            {
                path.Value = p;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>Photos with their dates checked, of all photos.</summary>
    public JobProgress GetDateCheckProgress() => Progress(
        "SELECT COUNT(*) FROM main.photos p WHERE EXISTS (SELECT 1 FROM date_checks d WHERE d.photo_id = p.id AND d.file_mtime = p.file_mtime);",
        "SELECT COUNT(*) FROM main.photos;");

    /// <summary>
    /// After only the photo's details and file times changed (a date sync), what was worked out from its picture
    /// (fingerprint, picture vector, file check) still holds: it follows the file's new modified time instead of
    /// being worked out again. Call it after the index has the new time.
    /// </summary>
    public void CarryPictureFacts(string path, DateTime oldModifiedUtc)
    {
        lock (_lock)
        {
            using var tx = _connection.BeginTransaction();
            foreach (var table in new[] { "picture_hashes", "picture_vectors", "file_checks" })
            {
                using var cmd = _connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = $"""
                    UPDATE {table} SET file_mtime = (SELECT file_mtime FROM main.photos WHERE path = $path)
                    WHERE photo_id = (SELECT id FROM main.photos WHERE path = $path) AND file_mtime = $old;
                    """;
                cmd.Parameters.AddWithValue("$path", path);
                cmd.Parameters.AddWithValue("$old", oldModifiedUtc.Ticks);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }
}
