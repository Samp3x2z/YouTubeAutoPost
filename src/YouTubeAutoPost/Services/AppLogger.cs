using System.Text;

namespace YouTubeAutoPost.Services;

public static class AppLogger
{
    private static readonly object Gate = new();
    private static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "YouTubeAutoPost.log");

    public static string FilePath => LogPath;

    public static void Write(string message, Exception? exception = null)
    {
        try
        {
            var entry = new StringBuilder()
                .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"))
                .Append(" [").Append(Environment.CurrentManagedThreadId).Append("] ")
                .AppendLine(message);

            if (exception is not null)
                entry.AppendLine(exception.ToString());

            lock (Gate)
                File.AppendAllText(LogPath, entry.ToString(), Encoding.UTF8);
        }
        catch
        {
            // Logging must never crash the application.
        }
    }
}
