using PhotoGallery.Core.Culling;

namespace PhotoGallery.Core.Library;

/// <summary>The user's changes to the stacks of similar shots (C-4), kept per photo.</summary>
public sealed partial class PhotoIndex
{
    /// <summary>Every photo with a mark, by path.</summary>
    public Dictionary<string, ShotMark> GetShotMarks()
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT p.path, m.mark FROM shot_marks m JOIN main.photos p ON p.id = m.photo_id;";
            var result = new Dictionary<string, ShotMark>(StringComparer.OrdinalIgnoreCase);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                result[r.GetString(0)] = (ShotMark)r.GetInt32(1);
            return result;
        }
    }

    /// <summary>Sets the photos' mark; <see cref="ShotMark.None"/> takes it off.</summary>
    public void SetShotMarks(IEnumerable<(string Path, ShotMark Mark)> marks)
    {
        lock (_lock)
        {
            using var tx = _connection.BeginTransaction();
            foreach (var (path, mark) in marks)
            {
                using var cmd = _connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = mark == ShotMark.None
                    ? "DELETE FROM shot_marks WHERE photo_id IN (SELECT id FROM main.photos WHERE path = $path);"
                    : "INSERT INTO shot_marks (photo_id, mark) SELECT id, $mark FROM main.photos WHERE path = $path ON CONFLICT (photo_id) DO UPDATE SET mark = excluded.mark;";
                cmd.Parameters.AddWithValue("$path", path);
                cmd.Parameters.AddWithValue("$mark", (int)mark);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }
}
