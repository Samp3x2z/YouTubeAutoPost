using Microsoft.Data.Sqlite;

namespace YouTubeAutoPost.Services;

public sealed record UploadRecord(long Size, double Mtime, string? VideoId, string Status);

public sealed class UploadHistory : IDisposable
{
    private readonly SqliteConnection _connection;

    public UploadHistory()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YouTubeAutoPost");
        Directory.CreateDirectory(folder);
        var dbPath = Path.Combine(folder, "history.sqlite3");
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString());
        _connection.Open();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS uploads (
                path TEXT PRIMARY KEY,
                size INTEGER NOT NULL,
                mtime REAL NOT NULL,
                video_id TEXT,
                status TEXT NOT NULL,
                last_error TEXT,
                updated_at TEXT DEFAULT CURRENT_TIMESTAMP
            );
            """;
        command.ExecuteNonQuery();
    }

    public UploadRecord? Get(string path)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT size, mtime, video_id, status FROM uploads WHERE path = $path";
        command.Parameters.AddWithValue("$path", path);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new UploadRecord(reader.GetInt64(0), reader.GetDouble(1),
            reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3));
    }

    public void Save(string path, long size, double mtime, string? videoId, string status, string? error = null)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO uploads(path, size, mtime, video_id, status, last_error, updated_at)
            VALUES($path, $size, $mtime, $videoId, $status, $error, CURRENT_TIMESTAMP)
            ON CONFLICT(path) DO UPDATE SET
                size=excluded.size, mtime=excluded.mtime, video_id=excluded.video_id,
                status=excluded.status, last_error=excluded.last_error,
                updated_at=CURRENT_TIMESTAMP;
            """;
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$size", size);
        command.Parameters.AddWithValue("$mtime", mtime);
        command.Parameters.AddWithValue("$videoId", (object?)videoId ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    public void Dispose() => _connection.Dispose();
}
