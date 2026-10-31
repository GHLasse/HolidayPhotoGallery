using PhotoGallery.Core.Tagging;

namespace PhotoGallery.Core.Library;

/// <summary>
/// The tagging model's picture vectors kept per photo (L-7), so a search in your own words, Find similar
/// photos and tagging again after the tag list changed need no photo to be opened. Like every table here
/// only a cache: rebuilt by tagging the photos again. A vector stays when only the file's details change
/// (a rating, a tag), which changes the file's time but not the picture; the app forgets it when it
/// changes the pixels (<see cref="ForgetPictureVector"/>).
/// </summary>
public sealed partial class PhotoIndex
{
    /// <summary>The vectors saved (private photos only in private mode).</summary>
    public PictureVectors GetPictureVectors()
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT p.path, v.vector FROM picture_vectors v JOIN photos p ON p.id = v.photo_id;";
            var result = new PictureVectors();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var vector = PictureVectors.FromBytes((byte[])r.GetValue(1));
                if (vector.Length == result.Length)
                    result.Set(r.GetString(0), vector);
            }
            return result;
        }
    }

    /// <summary>The vector saved for the photo, or null.</summary>
    public float[]? GetPictureVector(string path)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT v.vector FROM picture_vectors v JOIN photos p ON p.id = v.photo_id WHERE p.path = $path;";
            cmd.Parameters.AddWithValue("$path", path);
            return cmd.ExecuteScalar() is byte[] bytes ? PictureVectors.FromBytes(bytes) : null;
        }
    }

    /// <summary>Forgets a photo's vector after its pixels changed (an edit, a rotation, a repair).</summary>
    public void ForgetPictureVector(string path)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM picture_vectors WHERE photo_id IN (SELECT id FROM main.photos WHERE path = $path);";
            cmd.Parameters.AddWithValue("$path", path);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Keeps a photo's vector.</summary>
    public void SavePictureVector(string path, ReadOnlySpan<float> vector)
    {
        var bytes = PictureVectors.ToBytes(vector);
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO picture_vectors (photo_id, file_mtime, vector)
                SELECT id, file_mtime, $vector FROM photos WHERE path = $path
                ON CONFLICT (photo_id) DO UPDATE SET file_mtime = excluded.file_mtime, vector = excluded.vector;
                """;
            cmd.Parameters.AddWithValue("$path", path);
            cmd.Parameters.AddWithValue("$vector", bytes);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Photos with no vector, newest first, to get one in the background.</summary>
    public IReadOnlyList<string> GetNotPictureVectored(int limit)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT p.path FROM photos p
                WHERE NOT EXISTS (SELECT 1 FROM picture_vectors v WHERE v.photo_id = p.id)
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

    /// <summary>Notes new photos for auto-tagging in the background.</summary>
    public void QueueAutoTag(IEnumerable<string> paths)
    {
        lock (_lock)
        {
            using var tx = _connection.BeginTransaction();
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT OR IGNORE INTO auto_tag_queue (photo_id) SELECT id FROM main.photos WHERE path = $path;";
            var path = cmd.Parameters.Add("$path", Microsoft.Data.Sqlite.SqliteType.Text);
            foreach (var p in paths)
            {
                path.Value = p;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>The photos waiting to be auto-tagged, oldest arrival first (private photos only in private mode).</summary>
    public IReadOnlyList<string> GetAutoTagQueue(int limit)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT p.path FROM auto_tag_queue q JOIN photos p ON p.id = q.photo_id ORDER BY q.photo_id LIMIT $limit;";
            cmd.Parameters.AddWithValue("$limit", limit);
            var result = new List<string>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                result.Add(r.GetString(0));
            return result;
        }
    }

    /// <summary>Takes photos off the auto-tag queue, tagged or not.</summary>
    public void DequeueAutoTag(IEnumerable<string> paths)
    {
        lock (_lock)
        {
            using var tx = _connection.BeginTransaction();
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM auto_tag_queue WHERE photo_id IN (SELECT id FROM main.photos WHERE path = $path);";
            var path = cmd.Parameters.Add("$path", Microsoft.Data.Sqlite.SqliteType.Text);
            foreach (var p in paths)
            {
                path.Value = p;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>Queues every photo without auto tags, for "Tag existing photos too" in Settings.</summary>
    public void QueueAllWithoutAutoTags()
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT OR IGNORE INTO auto_tag_queue (photo_id)
                SELECT p.id FROM photos p WHERE NOT EXISTS (SELECT 1 FROM photo_tags t WHERE t.photo_id = p.id AND t.auto = 1);
                """;
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Forgets every photo waiting to be auto-tagged, when automatic tagging is turned off.</summary>
    public void ClearAutoTagQueue()
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM auto_tag_queue;";
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>How many photos wait to be auto-tagged.</summary>
    public int CountAutoTagQueue()
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM auto_tag_queue q JOIN photos p ON p.id = q.photo_id;";
            return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
