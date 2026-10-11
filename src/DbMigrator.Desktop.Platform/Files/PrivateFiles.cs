using System.Text;

namespace DbMigrator.Desktop.Platform;

/// <summary>
/// Files only the user can read: folders are 0700 and files 0600 on Unix (server and login names
/// are sensitive even without passwords). Writes go to a temporary file first and replace the
/// target in one step, so a crash never leaves half a file.
/// </summary>
internal static class PrivateFiles
{
    private const UnixFileMode FolderUnixMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode FileUnixMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public static void EnsureFolder(string folder)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(folder);
            return;
        }

        if (!Directory.Exists(folder))
        {
            Directory.CreateDirectory(folder, FolderUnixMode);
            return;
        }

        // A folder that was already there (e.g. under DBMIGRATOR_DESKTOP_HOME) is made private too.
        try
        {
            if ((File.GetUnixFileMode(folder) & ~FolderUnixMode) != 0)
            {
                File.SetUnixFileMode(folder, FolderUnixMode);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Not ours to change; the files inside are still created private.
        }
    }

    /// <summary>Options for a file only the user can read, created if missing.</summary>
    public static FileStreamOptions PrivateFileOptions(FileMode mode, FileAccess access, FileShare share)
    {
        var options = new FileStreamOptions { Mode = mode, Access = access, Share = share };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = FileUnixMode;
        }

        return options;
    }

    /// <summary>
    /// Holds a lock file next to <paramref name="path"/> while a read-modify-write runs, so two
    /// app windows (two processes) cannot interleave and lose each other's change.
    /// </summary>
    public static IDisposable Lock(string path, TimeSpan wait)
    {
        EnsureFolder(Path.GetDirectoryName(path)!);
        var deadline = DateTime.UtcNow + wait;
        while (true)
        {
            try
            {
                return new FileStream(path + ".lock", PrivateFileOptions(FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(50);
            }
        }
    }

    public static void WriteAtomically(string path, string text)
    {
        EnsureFolder(Path.GetDirectoryName(path)!);

        // A name of its own, so a writer in another process never shares or truncates it.
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        using (var stream = new FileStream(temporary, PrivateFileOptions(FileMode.CreateNew, FileAccess.Write, FileShare.None)))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            writer.Write(text);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        try
        {
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            File.Delete(temporary);
            throw;
        }
    }

    /// <summary>Sets a file that could not be read aside as "name.corrupt-20261010T080000Z" and returns the new name.</summary>
    public static string Quarantine(string path, DateTimeOffset now)
    {
        var aside = $"{path}.corrupt-{now.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}";
        File.Move(path, aside, overwrite: true);
        return Path.GetFileName(aside);
    }
}
