namespace PhotoGallery.Core.Library;

/// <summary>Picture fingerprints (<see cref="Duplicates.PictureHash"/>) kept per photo, so a second search for duplicates is quick.</summary>
public sealed partial class PhotoIndex
{
    /// <summary>The fingerprints saved for files that haven't changed since; null where the picture couldn't be read.</summary>
    public Dictionary<string, ulong?> GetPictureHashes()
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT p.path, h.dhash FROM picture_hashes h JOIN photos p ON p.id = h.photo_id WHERE h.file_mtime = p.file_mtime;";
            var result = new Dictionary<string, ulong?>(StringComparer.OrdinalIgnoreCase);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                result[r.GetString(0)] = r.IsDBNull(1) ? null : unchecked((ulong)r.GetInt64(1));
            return result;
        }
    }

    public void SavePictureHashes(IEnumerable<(string Path, ulong? Hash)> hashes)
    {
        lock (_lock)
        {
            using var tx = _connection.BeginTransaction();
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO picture_hashes (photo_id, file_mtime, dhash)
                SELECT id, file_mtime, $hash FROM main.photos WHERE path = $path
                ON CONFLICT (photo_id) DO UPDATE SET file_mtime = excluded.file_mtime, dhash = excluded.dhash;
                """;
            var path = cmd.Parameters.Add("$path", Microsoft.Data.Sqlite.SqliteType.Text);
            var hash = cmd.Parameters.Add("$hash", Microsoft.Data.Sqlite.SqliteType.Integer);
            foreach (var (p, h) in hashes)
            {
                path.Value = p;
                hash.Value = h is { } value ? unchecked((long)value) : DBNull.Value;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>Photos with no fingerprint for the file as it is now, newest first, for the search in the background.</summary>
    public IReadOnlyList<string> GetNotPictureHashed(int limit)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT p.path FROM main.photos p
                WHERE NOT EXISTS (SELECT 1 FROM picture_hashes h WHERE h.photo_id = p.id AND h.file_mtime = p.file_mtime)
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

    /// <summary>The groups the user chose to keep all of (<see cref="Duplicates.DuplicateGroup.Key"/>), so they aren't offered again.</summary>
    public HashSet<string> GetKeptDuplicates()
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT group_key FROM kept_duplicates;";
            var result = new HashSet<string>(StringComparer.Ordinal);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                result.Add(r.GetString(0));
            return result;
        }
    }

    public void KeepDuplicates(string groupKey)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO kept_duplicates (group_key) VALUES ($key);";
            cmd.Parameters.AddWithValue("$key", groupKey);
            cmd.ExecuteNonQuery();
        }
    }
}
