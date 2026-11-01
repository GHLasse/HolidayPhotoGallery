using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using PhotoGallery.Core.Faces;
using PhotoGallery.Core.Metadata;
using PhotoGallery.Core.Tagging;

namespace PhotoGallery.Core.Library;

/// <summary>A tag, or a tag group (<see cref="IsGroup"/>), and how many photos have it.</summary>
/// <summary>A tag or tag group and how many photos have it; <see cref="IsAuto"/> when auto-tagging added it on every one of them.</summary>
public sealed record TagCount(string Tag, int Count, bool IsGroup, bool IsAuto = false);

/// <summary>
/// A SQLite index of the photos in the library, used only to search and sort quickly.
/// It holds nothing that is not also in the files, so it can be deleted and rebuilt at any time.
/// </summary>
public sealed partial class PhotoIndex : IDisposable
{
    private const int SchemaVersion = 8;
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
        ShowPrivate = false;
    }

    private void EnsureSchema()
    {
        var version = Convert.ToInt32(Scalar("PRAGMA user_version;"), CultureInfo.InvariantCulture);
        if (version is 5 or 6 or 7)
        {
            // Flags came with version 6 and the auto tag mark with 7; adding the columns keeps the faces already found.
            if (version == 5)
                Execute("ALTER TABLE photos ADD COLUMN flagged INTEGER NOT NULL DEFAULT 0;");
            if (version is 5 or 6)
                Execute("ALTER TABLE photo_tags ADD COLUMN auto INTEGER NOT NULL DEFAULT 0;");
            // Version 8 takes fingerprints of the upright picture, decoded by Windows: the older ones are taken again.
            Execute("DROP TABLE IF EXISTS picture_hashes;");
        }
        else if (version != SchemaVersion)
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
                flagged INTEGER NOT NULL DEFAULT 0,
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
                auto INTEGER NOT NULL DEFAULT 0,
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
            -- What the quick check of each file found (FileHealth), for the file as it was then.
            CREATE TABLE IF NOT EXISTS file_checks (
                photo_id INTEGER PRIMARY KEY REFERENCES photos(id) ON DELETE CASCADE,
                health INTEGER NOT NULL,
                file_mtime INTEGER NOT NULL,
                level INTEGER NOT NULL DEFAULT 1,
                misnamed INTEGER NOT NULL DEFAULT 0
            );
            -- Picture fingerprints for finding duplicates (F-3), for the file as it was then.
            CREATE TABLE IF NOT EXISTS picture_hashes (
                photo_id INTEGER PRIMARY KEY REFERENCES photos(id) ON DELETE CASCADE,
                file_mtime INTEGER NOT NULL,
                dhash INTEGER
            );
            -- Groups of look-alikes the user kept all of; a new copy joining the group changes its key.
            CREATE TABLE IF NOT EXISTS kept_duplicates (group_key TEXT PRIMARY KEY);
            -- The tagging model's picture vector (L-7), half precision, for the file as it was then.
            CREATE TABLE IF NOT EXISTS picture_vectors (
                photo_id INTEGER PRIMARY KEY REFERENCES photos(id) ON DELETE CASCADE,
                file_mtime INTEGER NOT NULL,
                vector BLOB NOT NULL
            );
            -- The user's changes to the stacks of similar shots (C-4): the cover, taken out, a new stack starts here.
            CREATE TABLE IF NOT EXISTS shot_marks (
                photo_id INTEGER PRIMARY KEY REFERENCES photos(id) ON DELETE CASCADE,
                mark INTEGER NOT NULL,
                stack TEXT -- a stack the user made: its members share this key
            );
            -- Sharpness and exposure measured for quality hints (C-4); ignored = the user dismissed the hints.
            CREATE TABLE IF NOT EXISTS shot_quality (
                photo_id INTEGER PRIMARY KEY REFERENCES photos(id) ON DELETE CASCADE,
                sharpness REAL NOT NULL,
                face_sharpness REAL,
                brightness REAL NOT NULL,
                dark_share REAL NOT NULL,
                bright_share REAL NOT NULL,
                hints INTEGER NOT NULL,
                ignored INTEGER NOT NULL DEFAULT 0
            );
            -- Photos that arrived in a folder already in the library, to be auto-tagged in the background (L-7).
            CREATE TABLE IF NOT EXISTS auto_tag_queue (
                photo_id INTEGER PRIMARY KEY REFERENCES photos(id) ON DELETE CASCADE
            );
            PRAGMA user_version = {SchemaVersion};
            """);
        // Checks saved before the level was kept count as level 1, so those files are checked again.
        if (Convert.ToInt32(Scalar("SELECT COUNT(*) FROM pragma_table_info('file_checks') WHERE name = 'level';"), CultureInfo.InvariantCulture) == 0)
            Execute("ALTER TABLE file_checks ADD COLUMN level INTEGER NOT NULL DEFAULT 1;");
        // A damaged file's wrong name used to be hidden by its damage; those files are checked again.
        if (Convert.ToInt32(Scalar("SELECT COUNT(*) FROM pragma_table_info('file_checks') WHERE name = 'misnamed';"), CultureInfo.InvariantCulture) == 0)
            Execute($"ALTER TABLE file_checks ADD COLUMN misnamed INTEGER NOT NULL DEFAULT 0; UPDATE file_checks SET level = 0 WHERE health NOT IN (0, {(int)FileHealth.Misnamed});");
        // Private photos (P-1) came later; their mark is read from the folders again on the next scan.
        if (Convert.ToInt32(Scalar("SELECT COUNT(*) FROM pragma_table_info('photos') WHERE name = 'private';"), CultureInfo.InvariantCulture) == 0)
            Execute("ALTER TABLE photos ADD COLUMN private INTEGER NOT NULL DEFAULT 0;");
        // Videos (F-4) came later too; until now the library held only photos.
        if (Convert.ToInt32(Scalar("SELECT COUNT(*) FROM pragma_table_info('photos') WHERE name = 'video';"), CultureInfo.InvariantCulture) == 0)
            Execute("ALTER TABLE photos ADD COLUMN video INTEGER NOT NULL DEFAULT 0; ALTER TABLE photos ADD COLUMN duration REAL;");
        // Frame rates came after videos; forgetting the videos' file times makes the next scan read them again.
        if (Convert.ToInt32(Scalar("SELECT COUNT(*) FROM pragma_table_info('photos') WHERE name = 'frame_rate';"), CultureInfo.InvariantCulture) == 0)
            Execute("ALTER TABLE photos ADD COLUMN frame_rate REAL; UPDATE photos SET file_mtime = 0 WHERE video = 1;");
    }

    /// <summary>Path → (size, modified time) for every indexed file under a folder, to spot changes.</summary>
    public Dictionary<string, (long Size, DateTime ModifiedUtc)> GetFileStamps(string rootFolder)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = $"SELECT path, file_size, file_mtime FROM main.photos WHERE {FolderCondition(cmd, rootFolder, true)};";
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
            _privateMarks.Clear();
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
            INSERT INTO main.photos (path, folder, file_name, file_size, file_mtime, date_taken, date_offset_minutes, rating, flagged, caption, orientation, width, height, camera, latitude, longitude,
                                country, country_code, state, city, sublocation, private, video, duration, frame_rate)
            VALUES ($path, $folder, $name, $size, $mtime, $date, $offset, $rating, $flagged, $caption, $orientation, $width, $height, $camera, $lat, $lon,
                    $country, $countryCode, $state, $city, $sublocation, $private, $video, $duration, $frameRate)
            ON CONFLICT(path) DO UPDATE SET
                folder = excluded.folder, file_name = excluded.file_name, file_size = excluded.file_size,
                file_mtime = excluded.file_mtime, date_taken = excluded.date_taken,
                date_offset_minutes = excluded.date_offset_minutes, rating = excluded.rating, flagged = excluded.flagged,
                caption = excluded.caption, orientation = excluded.orientation,
                width = excluded.width, height = excluded.height, camera = excluded.camera,
                latitude = excluded.latitude, longitude = excluded.longitude,
                country = excluded.country, country_code = excluded.country_code, state = excluded.state,
                city = excluded.city, sublocation = excluded.sublocation, private = excluded.private,
                video = excluded.video, duration = excluded.duration, frame_rate = excluded.frame_rate
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
        // One column for the pick mark: 1 flagged, -1 rejected.
        cmd.Parameters.AddWithValue("$flagged", photo.Rejected ? -1 : photo.Flagged ? 1 : 0);
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
        cmd.Parameters.AddWithValue("$private", PrivateMarks.IsPrivate(photo.Path, _privateMarks) ? 1 : 0);
        cmd.Parameters.AddWithValue("$video", photo.IsVideo ? 1 : 0);
        cmd.Parameters.AddWithValue("$duration", (object?)photo.Duration ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$frameRate", (object?)photo.FrameRate ?? DBNull.Value);
        var id = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);

        using var delete = _connection.CreateCommand();
        delete.Transaction = tx;
        delete.CommandText = "DELETE FROM photo_tags WHERE photo_id = $id;";
        delete.Parameters.AddWithValue("$id", id);
        delete.ExecuteNonQuery();

        var autoTags = new HashSet<string>(photo.AutoTags, StringComparer.OrdinalIgnoreCase);
        foreach (var tag in TagList.Normalize(photo.Tags))
        {
            using var insert = _connection.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = "INSERT OR IGNORE INTO photo_tags (photo_id, tag, auto) VALUES ($id, $tag, $auto);";
            insert.Parameters.AddWithValue("$id", id);
            insert.Parameters.AddWithValue("$tag", tag);
            insert.Parameters.AddWithValue("$auto", autoTags.Contains(tag) ? 1 : 0);
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
                cmd.CommandText = "DELETE FROM main.photos WHERE path = $path;";
                cmd.Parameters.AddWithValue("$path", path);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>Follows a photo that was moved, keeping what was found in it (faces) and its index entry.</summary>
    public void Move(IndexedPhoto photo, string newPath)
    {
        lock (_lock)
        {
            _privateMarks.Clear();
            using var tx = _connection.BeginTransaction();
            using (var cmd = _connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM main.photos WHERE path = $new; UPDATE main.photos SET path = $new WHERE path = $old;";
                // A new extension can make a misnamed photo right (or a right one misnamed): check it again.
                if (!string.Equals(Path.GetExtension(photo.Path), Path.GetExtension(newPath), StringComparison.OrdinalIgnoreCase))
                    cmd.CommandText += " DELETE FROM file_checks WHERE photo_id = (SELECT id FROM main.photos WHERE path = $new);";
                cmd.Parameters.AddWithValue("$old", photo.Path);
                cmd.Parameters.AddWithValue("$new", newPath);
                cmd.ExecuteNonQuery();
            }
            UpsertOne(photo.MovedTo(newPath), tx);
            tx.Commit();
        }
    }

    /// <summary>
    /// Follows a folder renamed or moved on disk: every photo in and under it keeps its entry (faces, checks)
    /// under its new path.
    /// </summary>
    public void RenameFolder(string oldFolder, string newFolder)
    {
        var oldRoot = Path.TrimEndingDirectorySeparator(oldFolder);
        var newRoot = Path.TrimEndingDirectorySeparator(newFolder);
        lock (_lock)
        {
            using var tx = _connection.BeginTransaction();
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = tx;
            var where = FolderCondition(cmd, oldRoot, true);
            cmd.Parameters.AddWithValue("$oldLen", oldRoot.Length);
            cmd.Parameters.AddWithValue("$new", newRoot);
            // Only the start of each path changes: the old folder's part is cut off and the new one put in front.
            cmd.CommandText = $"""
                UPDATE main.photos SET path = $new || substr(path, $oldLen + 1), folder = $new || substr(folder, $oldLen + 1)
                WHERE {where};
                """;
            cmd.ExecuteNonQuery();
            tx.Commit();
        }
    }

    /// <summary>Forgets every photo under a folder, e.g. when it is removed from the library.</summary>
    public void RemoveFolder(string rootFolder)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = $"DELETE FROM main.photos WHERE {FolderCondition(cmd, rootFolder, true)};";
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>The photo as the index has it, or null.</summary>
    public IndexedPhoto? GetPhoto(string path) => Search(new PhotoQuery { ExactPath = path }).FirstOrDefault();

    public IReadOnlyList<IndexedPhoto> Search(PhotoQuery query)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            var where = new List<string>();

            if (!string.IsNullOrWhiteSpace(query.Folder))
                where.Add(FolderCondition(cmd, query.Folder, query.IncludeSubfolders));

            if (query.ExactPath is { } exact)
            {
                where.Add("p.path = $exactPath");
                cmd.Parameters.AddWithValue("$exactPath", exact);
            }

            if (query.MinRating > 0)
            {
                where.Add("p.rating >= $minRating");
                cmd.Parameters.AddWithValue("$minRating", query.MinRating);
            }

            if (query.FlaggedOnly)
                where.Add("p.flagged = 1");

            if (query.RejectedOnly)
                where.Add("p.flagged = -1");

            if (query.WithoutTags)
            {
                var notAuto = AutoTags.Prefixes.Select((prefix, i) =>
                {
                    cmd.Parameters.AddWithValue($"$autoTags{i}", EscapeLike(prefix) + "%");
                    return $"t.tag NOT LIKE $autoTags{i} ESCAPE '\\'";
                });
                where.Add($"NOT EXISTS (SELECT 1 FROM photo_tags t WHERE t.photo_id = p.id AND t.auto = 0 AND {string.Join(" AND ", notAuto)})");
            }

            if (query.WithoutPeople)
                where.Add("NOT EXISTS (SELECT 1 FROM photo_people pp WHERE pp.photo_id = p.id)");

            if (query.WithoutLocation)
                where.Add("(p.latitude IS NULL OR p.longitude IS NULL)");

            if (query.WithoutDate)
                where.Add("p.date_taken IS NULL");

            if (query.WithoutCamera)
                where.Add("(p.camera IS NULL OR p.camera = '')");

            if (query.Media == MediaKind.Photos)
                where.Add("p.video = 0");
            else if (query.Media == MediaKind.Videos)
                where.Add("p.video = 1");

            if (query.DamagedOnly)
                where.Add($"EXISTS (SELECT 1 FROM file_checks c WHERE c.photo_id = p.id AND c.health <> 0 AND c.file_mtime = p.file_mtime)");

            if (query.QualityHintsOnly)
                where.Add(HasQualityHintSql);

            if (query.MisnamedOnly)
                where.Add($"EXISTS (SELECT 1 FROM file_checks c WHERE c.photo_id = p.id AND (c.health = {(int)FileHealth.Misnamed} OR c.misnamed = 1) AND c.file_mtime = p.file_mtime)");

            if (query.WithoutPlace)
                where.Add("(p.country IS NULL OR p.country = '')");

            if (!string.IsNullOrEmpty(query.Country))
            {
                where.Add("p.country = $country COLLATE NOCASE");
                cmd.Parameters.AddWithValue("$country", query.Country);
            }

            if (query.Countries is { Count: > 0 } countries)
            {
                where.Add($"p.country COLLATE NOCASE IN ({string.Join(", ", countries.Select((_, n) => $"$country{n}"))})");
                for (var n = 0; n < countries.Count; n++)
                    cmd.Parameters.AddWithValue($"$country{n}", countries[n]);
            }

            if (!string.IsNullOrEmpty(query.City))
            {
                where.Add("p.city = $city COLLATE NOCASE");
                cmd.Parameters.AddWithValue("$city", query.City);
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
                // A tag in a tree also finds the tags under it (M-5): "Animals" finds "Animals/Birds/Seagull".
                where.Add($"EXISTS (SELECT 1 FROM photo_tags t WHERE t.photo_id = p.id AND (t.tag = {name} OR t.tag LIKE {name}_under ESCAPE '\\'))");
                cmd.Parameters.AddWithValue(name, tag);
                cmd.Parameters.AddWithValue(name + "_under", EscapeLike(tag + "/") + "%");
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
                        FROM photo_people pp WHERE pp.photo_id = p.id) AS people,
                       p.flagged,
                       (SELECT group_concat(tag, char(31)) FROM photo_tags t WHERE t.photo_id = p.id AND t.auto = 1) AS auto_tags,
                       p.duration, p.frame_rate
                FROM photos p
                """);
            if (where.Count > 0)
                sql.Append(" WHERE ").Append(string.Join(" AND ", where));
            sql.Append(';');
            cmd.CommandText = sql.ToString();

            var results = new List<IndexedPhoto>();
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                    results.Add(ReadPhoto(reader));
            }
            PhotoSort.Sort(results, query.Sort);
            return results;
        }
    }

    /// <summary>
    /// Goes up with every change to the index, so counts worked out from it can be kept until it moves.
    /// </summary>
    public long ChangeCount
    {
        get
        {
            lock (_lock)
                return Convert.ToInt64(Scalar("SELECT total_changes();"));
        }
    }

    /// <summary>All tags in the library with how many photos carry each, by name.</summary>
    public IReadOnlyList<(string Tag, int Count)> GetTagCounts()
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT tag, COUNT(*) FROM photo_tags WHERE photo_id IN (SELECT id FROM photos) GROUP BY tag COLLATE NOCASE ORDER BY tag COLLATE NOCASE;";
            var result = new List<(string, int)>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                result.Add((reader.GetString(0), reader.GetInt32(1)));
            return result;
        }
    }

    /// <summary>
    /// The tags for the filter (M-5): every tag, and every tree level above a tag ("Animals" and
    /// "Animals/Birds" above "Animals/Birds/Seagull") with how many photos have a tag at or under it,
    /// in tree order: each level followed by what is under it.
    /// </summary>
    private static readonly Comparer<string> TreeOrder = Comparer<string>.Create((a, b) =>
    {
        var pa = a.Split('/');
        var pb = b.Split('/');
        for (var i = 0; i < Math.Min(pa.Length, pb.Length); i++)
        {
            var c = StringComparer.CurrentCultureIgnoreCase.Compare(pa[i], pb[i]);
            if (c != 0)
                return c;
        }
        return pa.Length.CompareTo(pb.Length);
    });

    public IReadOnlyList<(string Tag, int Count)> GetTagTreeCounts()
    {
        var tags = GetTagCounts();
        var levels = new HashSet<string>(tags.SelectMany(t => TagTree.Ancestors(t.Tag)), StringComparer.OrdinalIgnoreCase);
        var result = tags.Where(t => !levels.Contains(t.Tag)).ToList();
        // A level counts every photo with a tag at or under it.
        foreach (var level in levels)
            result.Add((level, PathsWithTag(level).Count));
        // Part by part, so each level is followed by what is under it.
        return result.OrderBy(t => t.Tag, TreeOrder).ToList();
    }

    /// <summary>The tags that auto-tagging added on every photo that has them (the hidden list in the file).</summary>
    public IReadOnlySet<string> GetAutoTagNames()
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT tag FROM photo_tags WHERE photo_id IN (SELECT id FROM photos) GROUP BY tag COLLATE NOCASE HAVING MIN(auto) = 1;";
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                result.Add(reader.GetString(0));
            return result;
        }
    }

    /// <summary>
    /// Every tag with how many photos have it, and every tag group (the "ai" in "ai/beach") with how many
    /// photos have a tag in it; groups are listed before their tags.
    /// </summary>
    public IReadOnlyList<TagCount> GetTagOverview()
    {
        var tags = GetTagCounts();
        var groups = tags.SelectMany(t => GroupsOf(t.Tag)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var auto = GetAutoTagNames();
        var result = tags.Select(t => new TagCount(t.Tag, t.Count, false, auto.Contains(t.Tag)))
            .Concat(groups.Select(g => new TagCount(g, PathsWithTag(g, groupOnly: true).Count, true)));
        return result.OrderBy(t => t.Tag, StringComparer.CurrentCultureIgnoreCase).ThenByDescending(t => t.IsGroup).ToList();
    }

    /// <summary>The groups a tag is in: "a/b/c" is in "a" and "a/b".</summary>
    private static IEnumerable<string> GroupsOf(string tag)
    {
        for (var i = tag.IndexOf('/'); i > 0; i = tag.IndexOf('/', i + 1))
            yield return tag[..i];
    }

    /// <summary>The photos with the tag or a tag under it ("tag/…"); with <paramref name="groupOnly"/>, only those under it.</summary>
    public IReadOnlyList<string> PathsWithTag(string tag, bool groupOnly = false)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            var match = groupOnly ? "t.tag LIKE $group ESCAPE '\\'" : "(t.tag = $tag OR t.tag LIKE $group ESCAPE '\\')";
            cmd.CommandText = $"SELECT p.path FROM photos p WHERE EXISTS (SELECT 1 FROM photo_tags t WHERE t.photo_id = p.id AND {match}) ORDER BY p.path;";
            cmd.Parameters.AddWithValue("$tag", tag);
            cmd.Parameters.AddWithValue("$group", EscapeLike(tag + "/") + "%");
            var result = new List<string>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                result.Add(reader.GetString(0));
            return result;
        }
    }

    /// <summary>
    /// How many photos were taken in each month of the whole library, newest first, for the date tree.
    /// Photos without a date taken come last, with no year or month.
    /// </summary>
    public IReadOnlyList<DateCount> GetDateCounts()
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT CAST(substr(date_taken, 1, 4) AS INTEGER) AS y, CAST(substr(date_taken, 6, 2) AS INTEGER) AS m, COUNT(*)
                FROM photos GROUP BY y, m ORDER BY y IS NULL, y DESC, m DESC;
                """;
            var result = new List<DateCount>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                result.Add(new DateCount(reader.IsDBNull(0) ? null : reader.GetInt32(0), reader.IsDBNull(1) ? null : reader.GetInt32(1), reader.GetInt32(2)));
            return result;
        }
    }

    /// <summary>
    /// How many photos were taken in each city of the whole library, by country, for the place tree.
    /// Photos without place names come last, with no country.
    /// </summary>
    public IReadOnlyList<PlaceCount> GetPlaceCounts()
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT NULLIF(country, '') AS c, NULLIF(city, '') AS t, COUNT(*), UPPER(NULLIF(country_code, '')) AS k
                FROM photos GROUP BY c COLLATE NOCASE, t COLLATE NOCASE, k
                ORDER BY c IS NULL, c COLLATE NOCASE, t IS NULL, t COLLATE NOCASE, k;
                """;
            var result = new List<PlaceCount>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                result.Add(new PlaceCount(reader.IsDBNull(0) ? null : reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetInt32(2),
                                          reader.IsDBNull(3) ? null : reader.GetString(3)));
            return result;
        }
    }

    /// <summary>How many photos a folder holds (subfolders included), when they were taken, and the first of them.</summary>
    public FolderSummary GetFolderSummary(string folder)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            var where = FolderCondition(cmd, folder, true);
            cmd.CommandText = $"""
                SELECT COUNT(*) FROM photos WHERE {where};
                SELECT path FROM photos WHERE {where} ORDER BY date_taken IS NULL, date_taken, path COLLATE NOCASE LIMIT 1;
                SELECT date_taken FROM photos WHERE {where} AND date_taken IS NOT NULL ORDER BY date_taken;
                """;
            using var reader = cmd.ExecuteReader();
            reader.Read();
            var count = reader.GetInt32(0);
            string? first = null;
            if (reader.NextResult() && reader.Read())
                first = reader.GetString(0);
            var dates = new List<DateTime>();
            if (reader.NextResult())
            {
                while (reader.Read())
                    if (ParseDate(reader, 0) is { } date)
                        dates.Add(date);
            }
            // The trip's dates without stray photos weeks before or after it (AL-1).
            var (from, to) = Albums.TripDates.MainGroup(dates);
            return new FolderSummary(count, from, to, first);
        }

        static DateTime? ParseDate(SqliteDataReader r, int i) =>
            !r.IsDBNull(i) && DateTime.TryParseExact(r.GetString(i), DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                ? d : null;
    }

    /// <summary>
    /// Every photo with both a GPS position and a date taken, with the moment in UTC, sorted by it: what a
    /// location estimate (G-3) works from. Dates without a time zone count as UTC.
    /// </summary>
    public List<Metadata.GeoTimePoint> GetGeotaggedTimes()
    {
        var points = new List<Metadata.GeoTimePoint>();
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT path, date_taken, date_offset_minutes, latitude, longitude FROM photos " +
                              "WHERE latitude IS NOT NULL AND longitude IS NOT NULL AND date_taken IS NOT NULL;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (!DateTime.TryParseExact(reader.GetString(1), DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
                    continue;
                TimeSpan? offset = reader.IsDBNull(2) ? null : TimeSpan.FromMinutes(reader.GetInt32(2));
                points.Add(new Metadata.GeoTimePoint(reader.GetString(0), Metadata.LocationEstimate.ToUtc(new Metadata.DateTaken(local, offset)),
                    new Metadata.GeoPosition(reader.GetDouble(3), reader.GetDouble(4))));
            }
        }
        points.Sort((a, b) => a.Utc.CompareTo(b.Utc));
        return points;
    }

    /// <summary>Every camera in the library (or one folder of it) with how many photos it took, by name.</summary>
    public IReadOnlyList<(string Camera, int Count)> GetCameraCounts(string? folder = null)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            var where = string.IsNullOrWhiteSpace(folder) ? "" : $"AND {FolderCondition(cmd, folder, true)}";
            cmd.CommandText = $"SELECT camera, COUNT(*) FROM photos WHERE camera IS NOT NULL {where} GROUP BY camera ORDER BY camera COLLATE NOCASE;";
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
            Flagged = r.GetInt32(22) == 1,
            Rejected = r.GetInt32(22) == -1,
            AutoTags = r.IsDBNull(23) ? [] : r.GetString(23).Split('\u001f'),
            Duration = r.IsDBNull(24) ? null : r.GetDouble(24),
            FrameRate = r.IsDBNull(25) ? null : r.GetDouble(25),
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

/// <summary>A folder's photos at a glance: how many, the dates they span, and the first one.</summary>
/// <summary>How many photos were taken in a month; no year or month for photos without a date taken.</summary>
public sealed record DateCount(int? Year, int? Month, int Count);

/// <summary>How many photos were taken in a city; no country for photos without place names, no city when only the country is known.</summary>
public sealed record PlaceCount(string? Country, string? City, int Count, string? CountryCode = null);

public sealed record FolderSummary(int Count, DateTime? From, DateTime? To, string? FirstPhoto);
