using System.Text.Json;

namespace AIMediaWorker.Diagnostics;

public static class AppLog
{
    private const long MaximumBytes = 5 * 1024 * 1024;
    private static readonly SemaphoreSlim WriteLock = new(1, 1);
    public static string DirectoryPath { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIMediaWorker", "Logs");
    public static string CurrentPath => Path.Combine(DirectoryPath, "app.jsonl");

    public static async Task WriteAsync(string level, string category, string code, string message, Exception? exception = null)
    {
        await WriteLock.WaitAsync().ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            RotateIfNeeded();
            var entry = JsonSerializer.Serialize(new
            {
                timestamp = DateTimeOffset.UtcNow,
                level,
                category,
                code,
                message = Sanitize(message),
                exceptionType = exception?.GetType().FullName,
                exception = exception is null ? null : Sanitize(exception.ToString())
            });
            await File.AppendAllTextAsync(CurrentPath, entry + Environment.NewLine).ConfigureAwait(false);
        }
        catch (Exception logException) when (logException is IOException or UnauthorizedAccessException) { }
        finally { WriteLock.Release(); }
    }

    private static void RotateIfNeeded()
    {
        if (!File.Exists(CurrentPath) || new FileInfo(CurrentPath).Length < MaximumBytes) return;
        var previous = Path.Combine(DirectoryPath, "app.previous.jsonl");
        File.Move(CurrentPath, previous, true);
    }

    private static string Sanitize(string message)
    {
        if (string.IsNullOrEmpty(message)) return string.Empty;
        var lines = message.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
        foreach (var marker in new[] { "Authorization:", "api_key=", "apikey=", "token=" })
        {
            var index = lines.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index >= 0) lines = lines[..(index + marker.Length)] + " [redacted]";
        }
        return lines.Length <= 2000 ? lines : lines[..2000];
    }
}
