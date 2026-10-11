using System.Globalization;
using DbMigrator.Desktop.ViewModels;

namespace DbMigrator.Desktop.Platform;

/// <summary>
/// One log file per day, kept for ten days. Callers write only descriptions of connections, never
/// connection strings. Logging never throws: a log that cannot be written is not a reason to stop.
/// </summary>
public sealed class FileAppLog : IAppLog
{
    private const int DaysKept = 10;
    private readonly Lock _lock = new();
    private readonly TimeProvider _time;

    public FileAppLog(string folder, TimeProvider time)
    {
        LogFolder = folder;
        _time = time;
        try
        {
            PrivateFiles.EnsureFolder(folder);
            RemoveOld();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The app works without a log.
        }
    }

    public string LogFolder { get; }

    public void Info(string message) => Write("INFO ", message);

    public void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception is null ? message : $"{message}{Environment.NewLine}{exception}");

    private void Write(string level, string message)
    {
        var now = _time.GetUtcNow();
        var line = $"{now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)} {level} {message}{Environment.NewLine}";
        lock (_lock)
        {
            try
            {
                // Created readable by the user only: the log names servers, databases and logins.
                var path = Path.Combine(LogFolder, $"desktop-{now.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.log");
                using var stream = new FileStream(path, PrivateFiles.PrivateFileOptions(FileMode.Append, FileAccess.Write, FileShare.ReadWrite));
                using var writer = new StreamWriter(stream);
                writer.Write(line);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Nowhere to report it.
            }
        }
    }

    private void RemoveOld()
    {
        var cutoff = _time.GetUtcNow().UtcDateTime.Date.AddDays(-DaysKept);
        foreach (var file in Directory.EnumerateFiles(LogFolder, "desktop-*.log"))
        {
            var stamp = Path.GetFileNameWithoutExtension(file)["desktop-".Length..];
            if (DateTime.TryParseExact(stamp, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var day) && day < cutoff)
            {
                File.Delete(file);
            }
        }
    }
}
