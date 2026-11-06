using PhotoGallery.Core.Culling;

namespace PhotoGallery.Core.Library;

/// <summary>The user's changes to the stacks of similar shots (C-4), kept per photo.</summary>
public sealed partial class PhotoIndex
{
    /// <summary>Every photo with a mark, by path.</summary>
    public Dictionary<string, ShotMarkRow> GetShotMarks()
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT p.path, m.mark, m.stack FROM shot_marks m JOIN main.photos p ON p.id = m.photo_id;";
            var result = new Dictionary<string, ShotMarkRow>(StringComparer.OrdinalIgnoreCase);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                result[r.GetString(0)] = new ShotMarkRow((ShotMark)r.GetInt32(1), r.IsDBNull(2) ? null : r.GetString(2));
            return result;
        }
    }

    /// <summary>Sets the photos' marks; <see cref="ShotMark.None"/> takes the mark off.</summary>
    public void SetShotMarks(IEnumerable<(string Path, ShotMarkRow Mark)> marks)
    {
        lock (_lock)
        {
            using var tx = _connection.BeginTransaction();
            foreach (var (path, mark) in marks)
            {
                using var cmd = _connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = mark.Mark == ShotMark.None
                    ? "DELETE FROM shot_marks WHERE photo_id IN (SELECT id FROM main.photos WHERE path = $path);"
                    : """
                      INSERT INTO shot_marks (photo_id, mark, stack) SELECT id, $mark, $stack FROM main.photos WHERE path = $path
                      ON CONFLICT (photo_id) DO UPDATE SET mark = excluded.mark, stack = excluded.stack;
                      """;
                cmd.Parameters.AddWithValue("$path", path);
                cmd.Parameters.AddWithValue("$mark", (int)mark.Mark);
                cmd.Parameters.AddWithValue("$stack", (object?)mark.Stack ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }
}
