using PhotoGallery.Core.Metadata;

namespace PhotoGallery.Core.Library;

/// <summary>Place names in the background: the photos with a GPS position that still need them.</summary>
public sealed partial class PhotoIndex
{
    /// <summary>
    /// Photos with a GPS position and no place names, newest first, leaving out those already looked up with
    /// nothing found (private photos only in private mode).
    /// </summary>
    public IReadOnlyList<(string Path, GeoPosition Position)> GetLocatedWithoutPlace(int limit)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT p.path, p.latitude, p.longitude FROM photos p
                WHERE p.latitude IS NOT NULL AND p.longitude IS NOT NULL
                    AND COALESCE(p.country, '') = '' AND COALESCE(p.city, '') = '' AND COALESCE(p.state, '') = ''
                    AND NOT EXISTS (SELECT 1 FROM place_misses m WHERE m.photo_id = p.id)
                ORDER BY p.id DESC LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$limit", limit);
            var result = new List<(string, GeoPosition)>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                result.Add((r.GetString(0), new GeoPosition(r.GetDouble(1), r.GetDouble(2))));
            return result;
        }
    }

    /// <summary>How many photos still wait for place names (see <see cref="GetLocatedWithoutPlace"/>).</summary>
    public int CountLocatedWithoutPlace()
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT COUNT(*) FROM photos p
                WHERE p.latitude IS NOT NULL AND p.longitude IS NOT NULL
                    AND COALESCE(p.country, '') = '' AND COALESCE(p.city, '') = '' AND COALESCE(p.state, '') = ''
                    AND NOT EXISTS (SELECT 1 FROM place_misses m WHERE m.photo_id = p.id);
                """;
            return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Remembers that nothing was found at the photo's position, so it isn't asked again.</summary>
    public void NotePlaceMiss(string path)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO place_misses (photo_id) SELECT id FROM main.photos WHERE path = $path;";
            cmd.Parameters.AddWithValue("$path", path);
            cmd.ExecuteNonQuery();
        }
    }
}
