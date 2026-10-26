namespace PhotoGallery.Core.Library;

/// <summary>
/// Private photos (P-1). While private mode is off, a temporary view named "photos" stands in front of the
/// table and leaves them out, so every search, count, tree, map and people list skips them without a
/// condition of its own; writes go to main.photos.
/// </summary>
public sealed partial class PhotoIndex
{
    // What the folders' private marks said, for one batch of writes.
    private readonly Dictionary<string, PrivateInfo?> _privateMarks = new(StringComparer.OrdinalIgnoreCase);
    private bool _showPrivate = true;

    /// <summary>Whether private photos are shown (private mode on).</summary>
    public bool ShowPrivate
    {
        get => _showPrivate;
        set
        {
            lock (_lock)
            {
                Execute(value
                    ? "DROP VIEW IF EXISTS temp.photos;"
                    : "DROP VIEW IF EXISTS temp.photos; CREATE TEMP VIEW photos AS SELECT * FROM main.photos WHERE private = 0;");
                _showPrivate = value;
            }
        }
    }

    /// <summary>Reads the private marks again for every photo in and under the folder, after one changed.</summary>
    public void RefreshPrivate(string folder)
    {
        lock (_lock)
        {
            _privateMarks.Clear();
            var paths = new List<string>();
            using (var select = _connection.CreateCommand())
            {
                select.CommandText = $"SELECT path FROM main.photos WHERE {FolderCondition(select, folder, true)};";
                using var r = select.ExecuteReader();
                while (r.Read())
                    paths.Add(r.GetString(0));
            }
            using var tx = _connection.BeginTransaction();
            using var update = _connection.CreateCommand();
            update.Transaction = tx;
            update.CommandText = "UPDATE main.photos SET private = $private WHERE path = $path;";
            var path = update.Parameters.Add("$path", Microsoft.Data.Sqlite.SqliteType.Text);
            var mark = update.Parameters.Add("$private", Microsoft.Data.Sqlite.SqliteType.Integer);
            foreach (var p in paths)
            {
                path.Value = p;
                mark.Value = PrivateMarks.IsPrivate(p, _privateMarks) ? 1 : 0;
                update.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }
}
