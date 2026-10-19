using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using PhotoGallery.Core.Faces;
using PhotoGallery.Core.Metadata;

namespace PhotoGallery.Core.Library;

/// <summary>
/// A SQLite index of the photos in the library, used only to search and sort quickly.
/// It holds nothing that is not also in the files, so it can be deleted and rebuilt at any time.
/// </summary>
public sealed partial class PhotoIndex : IDisposable
{
    private const int SchemaVersion = 5;
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
        RelinkFaces();
    }

    private void EnsureSchema()
    {
        var version = Convert.ToInt32(Scalar("PRAGMA user_version;"), CultureInfo.InvariantCulture);
        if (version != SchemaVersion)
        {
            // The index is only a cache, so an outdated layout is simply rebuilt.
            Execute("""
                DROP TABLE IF EXISTS faces;
                DROP TABLE IF EXISTS face_scans;
                DROP TABLE IF EXISTS photo_people;
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
                height INTEGER,
                camera TEXT COLLATE NOCASE,
                latitude REAL,
                longitude REAL,
                country TEXT,
                country_code TEXT,
                state TEXT,
                city TEXT,
                sublocation TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_photos_folder ON photos(folder);
            CREATE INDEX IF NOT EXISTS ix_photos_date ON photos(date_taken);
            CREATE TABLE IF NOT EXISTS photo_tags (
                photo_id INTEGER NOT NULL REFERENCES photos(id) ON DELETE CASCADE,
                tag TEXT NOT NULL COLLATE NOCASE,
                PRIMARY KEY (photo_id, tag)
            );
            CREATE INDEX IF NOT EXISTS ix_photo_tags_tag ON photo_tags(tag);
            CREATE TABLE IF NOT EXISTS photo_people (
                photo_id INTEGER NOT NULL REFERENCES photos(id) ON DELETE CASCADE,
                name TEXT NOT NULL COLLATE NOCASE,
                x REAL NOT NULL, y REAL NOT NULL, w REAL NOT NULL, h REAL NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_photo_people_photo ON photo_people(photo_id);
            CREATE INDEX IF NOT EXISTS ix_photo_people_name ON photo_people(name);
            -- Photos the face finder has looked at, so a photo without faces is not looked at again.
            CREATE TABLE IF NOT EXISTS face_scans (
                photo_id INTEGER PRIMARY KEY REFERENCES photos(id) ON DELETE CASCADE
            );
            -- Faces the face finder found (boxes on the stored pixels). "person" mirrors the name in the
            -- file when a named person's box covers the face; "rejected" holds names the user said it is not.
            CREATE TABLE IF NOT EXISTS faces (
                id INTEGER PRIMARY KEY,
                photo_id INTEGER NOT NULL REFERENCES photos(id) ON DELETE CASCADE,
                x REAL NOT NULL, y REAL NOT NULL, w REAL NOT NULL, h REAL NOT NULL,
                score REAL NOT NULL,
                embedding BLOB NOT NULL,
                thumbnail BLOB,
                person TEXT COLLATE NOCASE,
                rejected TEXT,
                ignored INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS ix_faces_photo ON faces(photo_id);
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
            INSERT INTO photos (path, folder, file_name, file_size, file_mtime, date_taken, date_offset_minutes, rating, caption, orientation, width, height, camera, latitude, longitude,
                                country, country_code, state, city, sublocation)
            VALUES ($path, $folder, $name, $size, $mtime, $date, $offset, $rating, $caption, $orientation, $width, $height, $camera, $lat, $lon,
                    $country, $countryCode, $state, $city, $sublocation)
            ON CONFLICT(path) DO UPDATE SET
                folder = excluded.folder, file_name = excluded.file_name, file_size = excluded.file_size,
                file_mtime = excluded.file_mtime, date_taken = excluded.date_taken,
                date_offset_minutes = excluded.date_offset_minutes, rating = excluded.rating,
                caption = excluded.caption, orientation = excluded.orientation,
                width = excluded.width, height = excluded.height, camera = excluded.camera,
                latitude = excluded.latitude, longitude = excluded.longitude,
                country = excluded.country, country_code = excluded.country_code, state = excluded.state,
                city = excluded.city, sublocation = excluded.sublocation
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
        cmd.Parameters.AddWithValue("$camera", (object?)photo.Camera ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$lat", (object?)photo.Location?.Latitude ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$lon", (object?)photo.Location?.Longitude ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$country", (object?)photo.Place?.Country ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$countryCode", (object?)photo.Place?.CountryCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$state", (object?)photo.Place?.State ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$city", (object?)photo.Place?.City ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sublocation", (object?)photo.Place?.Sublocation ?? DBNull.Value);
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

        using var deletePeople = _connection.CreateCommand();
        deletePeople.Transaction = tx;
        deletePeople.CommandText = "DELETE FROM photo_people WHERE photo_id = $id;";
        deletePeople.Parameters.AddWithValue("$id", id);
        deletePeople.ExecuteNonQuery();

        foreach (var person in photo.People)
        {
            using var insert = _connection.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = "INSERT INTO photo_people (photo_id, name, x, y, w, h) VALUES ($id, $name, $x, $y, $w, $h);";
            insert.Parameters.AddWithValue("$id", id);
            insert.Parameters.AddWithValue("$name", person.Name);
            insert.Parameters.AddWithValue("$x", person.Box.X);
            insert.Parameters.AddWithValue("$y", person.Box.Y);
            insert.Parameters.AddWithValue("$w", person.Box.Width);
            insert.Parameters.AddWithValue("$h", person.Box.Height);
            insert.ExecuteNonQuery();
        }

        LinkFaces(id, photo.People, tx);
    }

    /// <summary>Gives each found face of a photo the name of the person whose box covers it in the file, if any.</summary>
    private void LinkFaces(long photoId, IReadOnlyList<PersonTag> people, SqliteTransaction tx)
    {
        var faces = new List<(long Id, FaceBox Box)>();
        using (var select = _connection.CreateCommand())
        {
            select.Transaction = tx;
            select.CommandText = "SELECT id, x, y, w, h FROM faces WHERE photo_id = $id;";
            select.Parameters.AddWithValue("$id", photoId);
            using var reader = select.ExecuteReader();
            while (reader.Read())
                faces.Add((reader.GetInt64(0), new FaceBox(reader.GetDouble(1), reader.GetDouble(2), reader.GetDouble(3), reader.GetDouble(4))));
        }

        foreach (var (faceId, box) in faces)
        {
            var person = FaceLinks.PersonFor(box, people);
            using var update = _connection.CreateCommand();
            update.Transaction = tx;
            update.CommandText = "UPDATE faces SET person = $person WHERE id = $face;";
            update.Parameters.AddWithValue("$person", (object?)person ?? DBNull.Value);
            update.Parameters.AddWithValue("$face", faceId);
            update.ExecuteNonQuery();
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

            if (!string.IsNullOrEmpty(query.Camera))
            {
                where.Add("p.camera = $camera");
                cmd.Parameters.AddWithValue("$camera", query.Camera);
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

            foreach (var person in query.People.Distinct(StringComparer.CurrentCultureIgnoreCase))
            {
                var name = $"$person{i++}";
                where.Add($"EXISTS (SELECT 1 FROM photo_people pp WHERE pp.photo_id = p.id AND pp.name = {name})");
                cmd.Parameters.AddWithValue(name, person);
            }

            if (!string.IsNullOrWhiteSpace(query.Text))
            {
                foreach (var word in query.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var name = $"$word{i++}";
                    where.Add($"""
                        (p.file_name LIKE {name} ESCAPE '\' OR p.caption LIKE {name} ESCAPE '\'
                         OR p.country LIKE {name} ESCAPE '\' OR p.state LIKE {name} ESCAPE '\'
                         OR p.city LIKE {name} ESCAPE '\' OR p.sublocation LIKE {name} ESCAPE '\'
                         OR EXISTS (SELECT 1 FROM photo_tags t WHERE t.photo_id = p.id AND t.tag LIKE {name} ESCAPE '\')
                         OR EXISTS (SELECT 1 FROM photo_people pp WHERE pp.photo_id = p.id AND pp.name LIKE {name} ESCAPE '\'))
                        """);
                    cmd.Parameters.AddWithValue(name, $"%{EscapeLike(word)}%");
                }
            }

            var sql = new StringBuilder("""
                SELECT p.id, p.path, p.folder, p.file_size, p.file_mtime, p.date_taken, p.date_offset_minutes,
                       p.rating, p.caption, p.orientation, p.width, p.height, p.camera,
                       (SELECT group_concat(tag, char(31)) FROM photo_tags t WHERE t.photo_id = p.id) AS tags,
                       p.latitude, p.longitude, p.country, p.country_code, p.state, p.city, p.sublocation,
                       (SELECT group_concat(name || char(30) || x || char(30) || y || char(30) || w || char(30) || h, char(31))
                        FROM photo_people pp WHERE pp.photo_id = p.id) AS people
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

    /// <summary>Every camera in the library (or one folder of it) with how many photos it took, most used first.</summary>
    public IReadOnlyList<(string Camera, int Count)> GetCameraCounts(string? folder = null)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            var where = string.IsNullOrWhiteSpace(folder) ? "" : $"AND {FolderCondition(cmd, folder, true)}";
            cmd.CommandText = $"SELECT camera, COUNT(*) FROM photos WHERE camera IS NOT NULL {where} GROUP BY camera ORDER BY COUNT(*) DESC, camera;";
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
            Camera = r.IsDBNull(12) ? null : r.GetString(12),
            Tags = r.IsDBNull(13) ? [] : r.GetString(13).Split('\u001f'),
            Location = r.IsDBNull(14) || r.IsDBNull(15) ? null : new GeoPosition(r.GetDouble(14), r.GetDouble(15)),
            Place = ReadPlace(r, 16),
            People = r.IsDBNull(21) ? [] : ReadPeople(r.GetString(21)),
        };
    }

    private static List<PersonTag> ReadPeople(string packed)
    {
        var people = new List<PersonTag>();
        foreach (var entry in packed.Split('\u001f'))
        {
            var parts = entry.Split('\u001e');
            if (parts.Length != 5)
                continue;
            double N(int i) => double.Parse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture);
            people.Add(new PersonTag(parts[0], new FaceBox(N(1), N(2), N(3), N(4))));
        }
        return people;
    }

    private static PlaceName? ReadPlace(SqliteDataReader r, int first)
    {
        string? Text(int i) => r.IsDBNull(i) ? null : r.GetString(i);
        var place = new PlaceName
        {
            Country = Text(first),
            CountryCode = Text(first + 1),
            State = Text(first + 2),
            City = Text(first + 3),
            Sublocation = Text(first + 4),
        };
        return place.IsEmpty ? null : place;
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
