using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using PhotoGallery.Core.Faces;

namespace PhotoGallery.Core.Library;

/// <summary>A face the face finder found on a photo, as kept in the index.</summary>
/// <param name="Box">On the stored pixels of the photo.</param>
/// <param name="Person">The name given to the face in the file, if any.</param>
/// <param name="Rejected">Names the user said this face is not.</param>
/// <param name="Thumbnail">A small JPEG of the face, upright.</param>
public sealed record FoundFace(long Id, string Path, FaceBox Box, float Score, float[] Embedding, string? Person,
                               IReadOnlyList<string> Rejected, bool Ignored, byte[]? Thumbnail);

/// <summary>A face to save for a photo; see <see cref="FoundFace"/>.</summary>
/// <summary>Where to find a picture of a person's face: a ready thumbnail, or the photo and the box to cut.</summary>
public sealed record PersonFace(string Path, DateTime FileModifiedUtc, int Orientation, PersonTag Person, byte[]? Thumbnail);

public sealed record NewFace(FaceBox Box, float Score, float[] Embedding, byte[]? Thumbnail);

public sealed partial class PhotoIndex
{
    /// <summary>
    /// Replaces the faces found on a photo and marks it as looked at. Faces are named from the people
    /// already in the file. Returns false when the photo is not in the index.
    /// </summary>
    public bool SaveFaces(string path, IReadOnlyList<NewFace> faces)
    {
        lock (_lock)
        {
            using var tx = _connection.BeginTransaction();
            if (PhotoId(path, tx) is not { } id)
                return false;

            Run(tx, "DELETE FROM faces WHERE photo_id = $id;", ("$id", id));
            Run(tx, "INSERT OR IGNORE INTO face_scans (photo_id) VALUES ($id);", ("$id", id));
            foreach (var face in faces)
            {
                Run(tx, """
                    INSERT INTO faces (photo_id, x, y, w, h, score, embedding, thumbnail)
                    VALUES ($id, $x, $y, $w, $h, $score, $embedding, $thumbnail);
                    """,
                    ("$id", id), ("$x", face.Box.X), ("$y", face.Box.Y), ("$w", face.Box.Width), ("$h", face.Box.Height),
                    ("$score", face.Score), ("$embedding", MemoryMarshal.AsBytes(face.Embedding.AsSpan()).ToArray()),
                    ("$thumbnail", face.Thumbnail));
            }
            LinkFaces(id, ReadPeople(id, tx), tx);
            tx.Commit();
            return true;
        }
    }

    /// <summary>Forgets the faces found on a photo, e.g. after its pixels were edited, so it is looked at again.</summary>
    public void ForgetFaces(string path)
    {
        lock (_lock)
        {
            using var tx = _connection.BeginTransaction();
            if (PhotoId(path, tx) is { } id)
            {
                Run(tx, "DELETE FROM faces WHERE photo_id = $id;", ("$id", id));
                Run(tx, "DELETE FROM face_scans WHERE photo_id = $id;", ("$id", id));
            }
            tx.Commit();
        }
    }

    /// <summary>The photos among <paramref name="paths"/> the face finder has already looked at.</summary>
    public HashSet<string> GetFaceScanned(IEnumerable<string> paths)
    {
        lock (_lock)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM face_scans s JOIN photos p ON p.id = s.photo_id WHERE p.path = $path;";
            var parameter = cmd.Parameters.Add("$path", SqliteType.Text);
            foreach (var path in paths)
            {
                parameter.Value = path;
                if (cmd.ExecuteScalar() is not null)
                    result.Add(path);
            }
            return result;
        }
    }

    /// <summary>
    /// Photos the face finder has not looked at yet, newest added first, so new photos can be looked at
    /// in the background. The orientation is the photo's EXIF orientation.
    /// </summary>
    public IReadOnlyList<(string Path, int Orientation)> GetNotFaceScanned(int limit)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT p.path, p.orientation FROM main.photos p
                WHERE p.video = 0 AND NOT EXISTS (SELECT 1 FROM face_scans s WHERE s.photo_id = p.id)
                ORDER BY p.id DESC LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$limit", limit);
            var result = new List<(string, int)>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                result.Add((r.GetString(0), r.GetInt32(1)));
            return result;
        }
    }

    /// <summary>The faces found on one photo, left to right.</summary>
    public IReadOnlyList<FoundFace> GetFaces(string path) => QueryFaces("p.path = $path", ("$path", path));

    /// <summary>Every face in the library that is not ignored, to suggest names and group unnamed faces.</summary>
    public IReadOnlyList<FoundFace> GetAllFaces(bool withThumbnails = false) => QueryFaces("f.ignored = 0", withThumbnails);

    /// <summary>How many faces found in the library have no name yet (ignored faces don't count).</summary>
    public int CountUnnamedFaces()
    {
        lock (_lock)
            return Convert.ToInt32(Scalar("""
                SELECT COUNT(*) FROM faces f JOIN photos p ON p.id = f.photo_id
                WHERE f.ignored = 0 AND f.person IS NULL;
                """));
    }

    public FoundFace? GetFace(long id) => QueryFaces("f.id = $face", ("$face", id)).FirstOrDefault();

    /// <summary>Remembers that a face is not this person, so they are not suggested for it again.</summary>
    public void RejectName(long faceId, string name)
    {
        lock (_lock)
        {
            using var tx = _connection.BeginTransaction();
            var current = Scalar(tx, "SELECT rejected FROM faces WHERE id = $face;", ("$face", faceId)) as string;
            var names = Split(current).Append(name.Trim()).Distinct(StringComparer.CurrentCultureIgnoreCase);
            Run(tx, "UPDATE faces SET rejected = $rejected WHERE id = $face;", ("$rejected", string.Join('\u001f', names)), ("$face", faceId));
            tx.Commit();
        }
    }

    /// <summary>Hides a face that is not worth naming (a stranger, a poster, not a face at all).</summary>
    public void IgnoreFace(long faceId, bool ignored = true)
    {
        lock (_lock)
        {
            using var tx = _connection.BeginTransaction();
            Run(tx, "UPDATE faces SET ignored = $ignored WHERE id = $face;", ("$ignored", ignored ? 1 : 0), ("$face", faceId));
            tx.Commit();
        }
    }

    /// <summary>Everyone named in the library with on how many photos, most photos first.</summary>
    public IReadOnlyList<(string Name, int Count)> GetPeopleCounts()
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT name, COUNT(DISTINCT photo_id) FROM photo_people WHERE photo_id IN (SELECT id FROM photos) GROUP BY name COLLATE NOCASE ORDER BY COUNT(DISTINCT photo_id) DESC, name;";
            var result = new List<(string, int)>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                result.Add((reader.GetString(0), reader.GetInt32(1)));
            return result;
        }
    }

    /// <summary>
    /// The person's face on their most recent photo (by date taken) that marks the face, not the whole photo:
    /// the face finder's thumbnail when it has one, otherwise the box to cut the face from the photo.
    /// </summary>
    public PersonFace? GetLatestFace(string name)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT p.path, p.file_mtime, p.orientation, pp.x, pp.y, pp.w, pp.h,
                       (SELECT f.thumbnail FROM faces f
                        WHERE f.photo_id = p.id AND f.person = pp.name AND f.thumbnail IS NOT NULL LIMIT 1)
                FROM photo_people pp JOIN photos p ON p.id = pp.photo_id
                WHERE pp.name = $name AND pp.w * pp.h <= 0.9
                ORDER BY p.date_taken IS NULL, p.date_taken DESC, p.file_mtime DESC
                LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("$name", name);
            using var r = cmd.ExecuteReader();
            if (!r.Read())
                return null;
            return new PersonFace(r.GetString(0), new DateTime(r.GetInt64(1), DateTimeKind.Utc), r.GetInt32(2),
                                  new PersonTag(name, new FaceBox(r.GetDouble(3), r.GetDouble(4), r.GetDouble(5), r.GetDouble(6))),
                                  r.IsDBNull(7) ? null : (byte[])r.GetValue(7));
        }
    }

    private IReadOnlyList<FoundFace> QueryFaces(string condition, params (string Name, object? Value)[] parameters) =>
        QueryFaces(condition, true, parameters);

    private IReadOnlyList<FoundFace> QueryFaces(string condition, bool withThumbnails, params (string Name, object? Value)[] parameters)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            var thumbnail = withThumbnails ? "f.thumbnail" : "NULL";
            cmd.CommandText = $"""
                SELECT f.id, p.path, f.x, f.y, f.w, f.h, f.score, f.embedding, f.person, f.rejected, f.ignored, {thumbnail}
                FROM faces f JOIN photos p ON p.id = f.photo_id
                WHERE {condition}
                ORDER BY p.date_taken IS NULL, p.date_taken, p.path, f.x;
                """;
            foreach (var (name, value) in parameters)
                cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
            var result = new List<FoundFace>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var bytes = (byte[])r.GetValue(7);
                result.Add(new FoundFace(
                    r.GetInt64(0), r.GetString(1), new FaceBox(r.GetDouble(2), r.GetDouble(3), r.GetDouble(4), r.GetDouble(5)),
                    (float)r.GetDouble(6), MemoryMarshal.Cast<byte, float>(bytes).ToArray(),
                    r.IsDBNull(8) ? null : r.GetString(8), Split(r.IsDBNull(9) ? null : r.GetString(9)),
                    r.GetInt64(10) != 0, r.IsDBNull(11) ? null : (byte[])r.GetValue(11)));
            }
            return result;
        }
    }

    /// <summary>
    /// Matches every found face with the people in its file again, so a change in how they are matched
    /// applies to photos looked at before. It is quick: only the boxes are compared.
    /// </summary>
    private void RelinkFaces()
    {
        lock (_lock)
        {
            using var tx = _connection.BeginTransaction();
            var photos = new List<long>();
            using (var cmd = _connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT DISTINCT photo_id FROM faces;";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    photos.Add(r.GetInt64(0));
            }
            foreach (var id in photos)
                LinkFaces(id, ReadPeople(id, tx), tx);
            tx.Commit();
        }
    }

    private List<PersonTag> ReadPeople(long photoId, SqliteTransaction tx)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT name, x, y, w, h FROM photo_people WHERE photo_id = $id;";
        cmd.Parameters.AddWithValue("$id", photoId);
        var people = new List<PersonTag>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            people.Add(new PersonTag(r.GetString(0), new FaceBox(r.GetDouble(1), r.GetDouble(2), r.GetDouble(3), r.GetDouble(4))));
        return people;
    }

    private long? PhotoId(string path, SqliteTransaction tx) =>
        Scalar(tx, "SELECT id FROM main.photos WHERE path = $path;", ("$path", path)) is { } id ? Convert.ToInt64(id, CultureInfo.InvariantCulture) : null;

    private static string[] Split(string? names) =>
        string.IsNullOrEmpty(names) ? [] : names.Split('\u001f');

    private void Run(SqliteTransaction tx, string sql, params (string Name, object? Value)[] parameters)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private object? Scalar(SqliteTransaction tx, string sql, params (string Name, object? Value)[] parameters)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd.ExecuteScalar();
    }
}
