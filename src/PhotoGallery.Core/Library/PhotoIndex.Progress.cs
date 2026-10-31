namespace PhotoGallery.Core.Library;

/// <summary>How far the background jobs have got through the library, for Settings to show.</summary>
public readonly record struct JobProgress(int Done, int Total)
{
    /// <summary>Done in whole per cent, rounded down so 100 % means really all.</summary>
    public int Percent => Total == 0 ? 100 : (int)Math.Floor(100.0 * Done / Total);
}

public sealed partial class PhotoIndex
{
    /// <summary>Photos and videos with a picture vector (search in your own words, auto-tagging), of all.</summary>
    public JobProgress GetVectorProgress() => Progress(
        "SELECT COUNT(*) FROM photos p WHERE EXISTS (SELECT 1 FROM picture_vectors v WHERE v.photo_id = p.id);",
        "SELECT COUNT(*) FROM photos;");

    /// <summary>Photos looked at for faces, of all photos (videos aren't).</summary>
    public JobProgress GetFaceScanProgress() => Progress(
        "SELECT COUNT(*) FROM photos p WHERE p.video = 0 AND EXISTS (SELECT 1 FROM face_scans s WHERE s.photo_id = p.id);",
        "SELECT COUNT(*) FROM photos WHERE video = 0;");

    /// <summary>Photos measured for quality hints, of all photos.</summary>
    public JobProgress GetQualityProgress() => Progress(
        "SELECT COUNT(*) FROM photos p WHERE p.video = 0 AND EXISTS (SELECT 1 FROM shot_quality q WHERE q.photo_id = p.id);",
        "SELECT COUNT(*) FROM photos WHERE video = 0;");

    private JobProgress Progress(string doneSql, string totalSql)
    {
        lock (_lock)
        {
            using var done = _connection.CreateCommand();
            done.CommandText = doneSql;
            using var total = _connection.CreateCommand();
            total.CommandText = totalSql;
            return new JobProgress(Convert.ToInt32(done.ExecuteScalar()), Convert.ToInt32(total.ExecuteScalar()));
        }
    }
}
