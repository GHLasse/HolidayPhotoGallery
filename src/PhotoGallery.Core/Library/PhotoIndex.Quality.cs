using PhotoGallery.Core.Culling;

namespace PhotoGallery.Core.Library;

/// <summary>What was measured on a photo, the hints it gives and whether the user said to ignore them (C-4).</summary>
public sealed record ShotQualityRow(QualityMeasure Measure, QualityHint Hints, bool Ignored);

/// <summary>
/// Quality hints kept per photo (C-4). Like the picture vectors they stay when only the file's details change
/// (a rating, a tag), and the app forgets them when it changes the pixels. "Ignore" is kept with them, so a
/// hint the user dismissed never shows again for that photo.
/// </summary>
public sealed partial class PhotoIndex
{
    /// <summary>Every measured photo's row (private photos only in private mode).</summary>
    public Dictionary<string, ShotQualityRow> GetShotQualities()
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT p.path, q.sharpness, q.face_sharpness, q.brightness, q.dark_share, q.bright_share, q.hints, q.ignored
                FROM shot_quality q JOIN photos p ON p.id = q.photo_id;
                """;
            var result = new Dictionary<string, ShotQualityRow>(StringComparer.OrdinalIgnoreCase);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var measure = new QualityMeasure((float)r.GetDouble(1), r.IsDBNull(2) ? null : (float)r.GetDouble(2),
                                                 (float)r.GetDouble(3), (float)r.GetDouble(4), (float)r.GetDouble(5));
                result[r.GetString(0)] = new ShotQualityRow(measure, (QualityHint)r.GetInt32(6), r.GetInt32(7) != 0);
            }
            return result;
        }
    }

    /// <summary>Keeps what was measured on a photo, and its hints (a dismissal stays).</summary>
    public void SaveShotQuality(IEnumerable<(string Path, QualityMeasure Measure)> measured)
    {
        lock (_lock)
        {
            using var tx = _connection.BeginTransaction();
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO shot_quality (photo_id, sharpness, face_sharpness, brightness, dark_share, bright_share, hints)
                SELECT id, $sharpness, $face, $brightness, $dark, $bright, $hints FROM main.photos WHERE path = $path
                ON CONFLICT (photo_id) DO UPDATE SET sharpness = excluded.sharpness, face_sharpness = excluded.face_sharpness,
                    brightness = excluded.brightness, dark_share = excluded.dark_share, bright_share = excluded.bright_share,
                    hints = excluded.hints;
                """;
            var path = cmd.Parameters.Add("$path", Microsoft.Data.Sqlite.SqliteType.Text);
            var sharpness = cmd.Parameters.Add("$sharpness", Microsoft.Data.Sqlite.SqliteType.Real);
            var face = cmd.Parameters.Add("$face", Microsoft.Data.Sqlite.SqliteType.Real);
            var brightness = cmd.Parameters.Add("$brightness", Microsoft.Data.Sqlite.SqliteType.Real);
            var dark = cmd.Parameters.Add("$dark", Microsoft.Data.Sqlite.SqliteType.Real);
            var bright = cmd.Parameters.Add("$bright", Microsoft.Data.Sqlite.SqliteType.Real);
            var hints = cmd.Parameters.Add("$hints", Microsoft.Data.Sqlite.SqliteType.Integer);
            foreach (var (p, m) in measured)
            {
                path.Value = p;
                sharpness.Value = (double)m.Sharpness;
                face.Value = m.FaceSharpness is { } f ? (double)f : DBNull.Value;
                brightness.Value = (double)m.Brightness;
                dark.Value = (double)m.DarkShare;
                bright.Value = (double)m.BrightShare;
                hints.Value = (int)ShotQuality.Hints(m);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>"Ignore": the photos' hints don't show again (until their pixels change).</summary>
    public void IgnoreQualityHints(IEnumerable<string> paths)
    {
        lock (_lock)
        {
            using var tx = _connection.BeginTransaction();
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE shot_quality SET ignored = 1 WHERE photo_id IN (SELECT id FROM main.photos WHERE path = $path);";
            var path = cmd.Parameters.Add("$path", Microsoft.Data.Sqlite.SqliteType.Text);
            foreach (var p in paths)
            {
                path.Value = p;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>Forgets what was measured after the pixels changed (an edit, a rotation, a repair).</summary>
    public void ForgetShotQuality(string path)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM shot_quality WHERE photo_id IN (SELECT id FROM main.photos WHERE path = $path);";
            cmd.Parameters.AddWithValue("$path", path);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Photos (not videos) not measured yet, newest first, with their EXIF orientation, to measure in the background.</summary>
    public IReadOnlyList<(string Path, int Orientation)> GetNotQualityChecked(int limit)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT p.path, p.orientation FROM main.photos p
                WHERE p.video = 0 AND NOT EXISTS (SELECT 1 FROM shot_quality q WHERE q.photo_id = p.id)
                ORDER BY p.id DESC LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$limit", limit);
            var result = new List<(string, int)>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                result.Add((r.GetString(0), r.IsDBNull(1) ? 1 : r.GetInt32(1)));
            return result;
        }
    }

    /// <summary>
    /// The SQL condition for photos that have a hint to show: measured with a hint, not ignored, and not
    /// rated, flagged or rejected yet (a photo the user already judged needs no hint).
    /// </summary>
    internal const string HasQualityHintSql =
        "(p.rating = 0 AND p.flagged = 0 AND EXISTS (SELECT 1 FROM shot_quality q WHERE q.photo_id = p.id AND q.hints <> 0 AND q.ignored = 0))";
}
