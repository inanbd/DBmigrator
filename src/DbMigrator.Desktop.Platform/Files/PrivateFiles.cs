using System.Text;

namespace DbMigrator.Desktop.Platform;

/// <summary>
/// Files only the user can read: folders are 0700 and files 0600 on Unix (server and login names
/// are sensitive even without passwords). Writes go to a temporary file first and replace the
/// target in one step, so a crash never leaves half a file.
/// </summary>
internal static class PrivateFiles
{
    private const UnixFileMode FolderMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode FileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public static void EnsureFolder(string folder)
    {
        if (Directory.Exists(folder))
        {
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(folder);
        }
        else
        {
            Directory.CreateDirectory(folder, FolderMode);
        }
    }

    public static void WriteAtomically(string path, string text)
    {
        EnsureFolder(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        var options = new FileStreamOptions { Mode = System.IO.FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = FileMode;
        }

        using (var stream = new FileStream(temporary, options))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            writer.Write(text);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        if (!OperatingSystem.IsWindows())
        {
            // UnixCreateMode applies only to new files; a leftover temporary file keeps its old mode.
            File.SetUnixFileMode(temporary, FileMode);
        }

        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>Sets a file that could not be read aside as "name.corrupt-20261010T080000Z" and returns the new name.</summary>
    public static string Quarantine(string path, DateTimeOffset now)
    {
        var aside = $"{path}.corrupt-{now.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}";
        File.Move(path, aside, overwrite: true);
        return Path.GetFileName(aside);
    }
}
