using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using PhotoGallery.Core.Metadata;

namespace PhotoGallery.Core.Library;

/// <summary>
/// A SQLite index of the photos in the library, used only to search and sort quickly.
/// It holds nothing that is not also in the files, so it can be deleted and rebuilt at any time.
/// </summary>
public sealed class PhotoIndex : IDisposable
{
    private const int SchemaVersion = 1;
    private const string DateFormat = "yyyy-MM-ddTHH:mm:ss";

    private readonly SqliteConnection _connection;
    private readonly Lock _lock = new();

    public PhotoIndex(string databasePath)
    {
        var dir = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString());
        _connection.Open();
        EnsureSchema();
    }

    private void EnsureSchema()
    {
        var version = Convert.ToInt32(Scalar("PRAGMA user_version;"), CultureInfo.InvariantCulture);
        if (version != SchemaVersion)
        {
            // The index is only a cache, so an outdated layout is simply rebuilt.
            Execute("""
                DROP TABLE IF EXISTS photo_tags;
                DROP TABLE IF EXISTS photos;
                """);
        }

        Execute($"""
            PRAGMA journal_mode = WAL;
            PRAGMA foreign_keys = ON;
            CREATE TABLE IF NOT EXISTS photos (
                id INTEGER PRIMARY KEY,
                path TEXT NOT NULL UNIQUE COLLATE NOCASE,
                folder TEXT NOT NULL COLLATE NOCASE,
                file_name TEXT NOT NULL COLLATE NOCASE,
                file_size INTEGER NOT NULL,
                file_mtime INTEGER NOT NULL,
                date_taken TEXT,
                date_offset_minutes INTEGER,
                rating INTEGER NOT NULL DEFAULT 0,
                caption TEXT,
                orientation INTEGER NOT NULL DEFAULT 1,
                width INTEGER,
                height INTEGER
            );
            CREATE INDEX IF NOT EXISTS ix_photos_folder ON photos(folder);
            CREATE INDEX IF NOT EXISTS ix_photos_date ON photos(date_taken);
            CREATE TABLE IF NOT EXISTS photo_tags (
                photo_id INTEGER NOT NULL REFERENCES photos(id) ON DELETE CASCADE,
                tag TEXT NOT NULL COLLATE NOCASE,
                PRIMARY KEY (photo_id, tag)
            );
            CREATE INDEX IF NOT EXISTS ix_photo_tags_tag ON photo_tags(tag);
            PRAGMA user_version = {SchemaVersion};
            """);
    }

    /// <summary>Path → (size, modified time) for every indexed file under a folder, to spot changes.</summary>
    public Dictionary<string, (long Size, DateTime ModifiedUtc)> GetFileStamps(string rootFolder)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = $"SELECT path, file_size, file_mtime FROM photos WHERE {FolderCondition(cmd, rootFolder, true)};";
            var result = new Dictionary<string, (long, DateTime)>(StringComparer.OrdinalIgnoreCase);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                result[reader.GetString(0)] = (reader.GetInt64(1), new DateTime(reader.GetInt64(2), DateTimeKind.Utc));
            return result;
        }
    }

    public void Upsert(IEnumerable<IndexedPhoto> photos)
    {
        lock (_lock)
        {
            using var tx = _connection.BeginTransaction();
            foreach (var photo in photos)
                UpsertOne(photo, tx);
            tx.Commit();
        }
    }

    private void UpsertOne(IndexedPhoto photo, SqliteTransaction tx)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO photos (path, folder, file_name, file_size, file_mtime, date_taken, date_offset_minutes, rating, caption, orientation, width, height)
            VALUES ($path, $folder, $name, $size, $mtime, $date, $offset, $rating, $caption, $orientation, $width, $height)
            ON CONFLICT(path) DO UPDATE SET
                folder = excluded.folder, file_name = excluded.file_name, file_size = excluded.file_size,
                file_mtime = excluded.file_mtime, date_taken = excluded.date_taken,
                date_offset_minutes = excluded.date_offset_minutes, rating = excluded.rating,
                caption = excluded.caption, orientation = excluded.orientation,
                width = excluded.width, height = excluded.height
            RETURNING id;
            """;
        cmd.Parameters.AddWithValue("$path", photo.Path);
        cmd.Parameters.AddWithValue("$folder", photo.Folder);
        cmd.Parameters.AddWithValue("$name", photo.FileName);
        cmd.Parameters.AddWithValue("$size", photo.FileSize);
        cmd.Parameters.AddWithValue("$mtime", photo.FileModifiedUtc.Ticks);
        cmd.Parameters.AddWithValue("$date", (object?)photo.DateTaken?.Local.ToString(DateFormat, CultureInfo.InvariantCulture) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$offset", (object?)(photo.DateTaken?.Offset is { } o ? (int)o.TotalMinutes : null) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$rating", photo.Rating);
        cmd.Parameters.AddWithValue("$caption", (object?)photo.Caption ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$orientation", photo.Orientation);
        cmd.Parameters.AddWithValue("$width", (object?)photo.Width ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$height", (object?)photo.Height ?? DBNull.Value);
        var id = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);

        using var delete = _connection.CreateCommand();
        delete.Transaction = tx;
        delete.CommandText = "DELETE FROM photo_tags WHERE photo_id = $id;";
        delete.Parameters.AddWithValue("$id", id);
        delete.ExecuteNonQuery();

        foreach (var tag in TagList.Normalize(photo.Tags))
        {
            using var insert = _connection.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = "INSERT OR IGNORE INTO photo_tags (photo_id, tag) VALUES ($id, $tag);";
            insert.Parameters.AddWithValue("$id", id);
            insert.Parameters.AddWithValue("$tag", tag);
            insert.ExecuteNonQuery();
        }
    }

    public void Remove(IEnumerable<string> paths)
    {
        lock (_lock)
        {
            using var tx = _connection.BeginTransaction();
            foreach (var path in paths)
            {
                using var cmd = _connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM photos WHERE path = $path;";
                cmd.Parameters.AddWithValue("$path", path);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>Forgets every photo under a folder, e.g. when it is removed from the library.</summary>
    public void RemoveFolder(string rootFolder)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = $"DELETE FROM photos WHERE {FolderCondition(cmd, rootFolder, true)};";
            cmd.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<IndexedPhoto> Search(PhotoQuery query)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            var where = new List<string>();

            if (!string.IsNullOrWhiteSpace(query.Folder))
                where.Add(FolderCondition(cmd, query.Folder, query.IncludeSubfolders));

            if (query.MinRating > 0)
            {
                where.Add("p.rating >= $minRating");
                cmd.Parameters.AddWithValue("$minRating", query.MinRating);
            }

            if (query.TakenFrom is { } from)
            {
                where.Add("p.date_taken >= $from");
                cmd.Parameters.AddWithValue("$from", from.ToString(DateFormat, CultureInfo.InvariantCulture));
            }

            if (query.TakenTo is { } to)
            {
                // A bare date means "until the end of that day".
                var end = to.TimeOfDay == TimeSpan.Zero ? to.Date.AddDays(1).AddSeconds(-1) : to;
                where.Add("p.date_taken <= $to");
                cmd.Parameters.AddWithValue("$to", end.ToString(DateFormat, CultureInfo.InvariantCulture));
            }

            var i = 0;
            foreach (var tag in TagList.Normalize(query.Tags))
            {
                var name = $"$tag{i++}";
                where.Add($"EXISTS (SELECT 1 FROM photo_tags t WHERE t.photo_id = p.id AND t.tag = {name})");
                cmd.Parameters.AddWithValue(name, tag);
            }

            if (!string.IsNullOrWhiteSpace(query.Text))
            {
                foreach (var word in query.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var name = $"$word{i++}";
                    where.Add($"""
                        (p.file_name LIKE {name} ESCAPE '\' OR p.caption LIKE {name} ESCAPE '\'
                         OR EXISTS (SELECT 1 FROM photo_tags t WHERE t.photo_id = p.id AND t.tag LIKE {name} ESCAPE '\'))
                        """);
                    cmd.Parameters.AddWithValue(name, $"%{EscapeLike(word)}%");
                }
            }

            var sql = new StringBuilder("""
                SELECT p.id, p.path, p.folder, p.file_size, p.file_mtime, p.date_taken, p.date_offset_minutes,
                       p.rating, p.caption, p.orientation, p.width, p.height,
                       (SELECT group_concat(tag, char(31)) FROM photo_tags t WHERE t.photo_id = p.id) AS tags
                FROM photos p
                """);
            if (where.Count > 0)
                sql.Append(" WHERE ").Append(string.Join(" AND ", where));
            // Undated photos go last, then everything by date and name.
            sql.Append(" ORDER BY p.date_taken IS NULL, p.date_taken, p.path;");
            cmd.CommandText = sql.ToString();

            var results = new List<IndexedPhoto>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                results.Add(ReadPhoto(reader));
            return results;
        }
    }

    /// <summary>All tags in the library with how many photos carry each, most used first.</summary>
    public IReadOnlyList<(string Tag, int Count)> GetTagCounts()
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT tag, COUNT(*) FROM photo_tags GROUP BY tag COLLATE NOCASE ORDER BY COUNT(*) DESC, tag;";
            var result = new List<(string, int)>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                result.Add((reader.GetString(0), reader.GetInt32(1)));
            return result;
        }
    }

    private static IndexedPhoto ReadPhoto(SqliteDataReader r)
    {
        DateTaken? date = null;
        if (!r.IsDBNull(5) &&
            DateTime.TryParseExact(r.GetString(5), DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            date = new DateTaken(local, r.IsDBNull(6) ? null : TimeSpan.FromMinutes(r.GetInt32(6)));
        }

        return new IndexedPhoto
        {
            Path = r.GetString(1),
            Folder = r.GetString(2),
            FileSize = r.GetInt64(3),
            FileModifiedUtc = new DateTime(r.GetInt64(4), DateTimeKind.Utc),
            DateTaken = date,
            Rating = r.GetInt32(7),
            Caption = r.IsDBNull(8) ? null : r.GetString(8),
            Orientation = r.GetInt32(9),
            Width = r.IsDBNull(10) ? null : r.GetInt32(10),
            Height = r.IsDBNull(11) ? null : r.GetInt32(11),
            Tags = r.IsDBNull(12) ? [] : r.GetString(12).Split('\u001f'),
        };
    }

    private static string FolderCondition(SqliteCommand cmd, string folder, bool includeSubfolders)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(folder);
        cmd.Parameters.AddWithValue("$folder", trimmed);
        if (!includeSubfolders)
            return "folder = $folder";

        // A drive root such as "D:\" already ends with a separator.
        var prefix = Path.EndsInDirectorySeparator(trimmed) ? trimmed : trimmed + Path.DirectorySeparatorChar;
        cmd.Parameters.AddWithValue("$folderPrefix", prefix);
        cmd.Parameters.AddWithValue("$folderPrefixLen", prefix.Length);
        return "(folder = $folder OR substr(folder, 1, $folderPrefixLen) = $folderPrefix COLLATE NOCASE)";
    }

    private static string EscapeLike(string s) =>
        s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private object? Scalar(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    private void Execute(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _connection.Dispose();
}
