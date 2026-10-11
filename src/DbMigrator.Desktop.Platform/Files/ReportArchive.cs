using DbMigrator.Desktop.ViewModels;

namespace DbMigrator.Desktop.Platform;

/// <summary>Keeps every finished run's report, as Markdown and JSON side by side.</summary>
public sealed class ReportArchive(AppPaths paths) : IReportArchive
{
    public string Save(string baseName, string markdown, string json)
    {
        PrivateFiles.EnsureFolder(paths.ReportsFolder);
        var name = baseName;
        for (var n = 2; File.Exists(Path.Combine(paths.ReportsFolder, name + ".md")) || File.Exists(Path.Combine(paths.ReportsFolder, name + ".json")); n++)
        {
            name = $"{baseName}-{n}";
        }

        var markdownPath = Path.Combine(paths.ReportsFolder, name + ".md");
        PrivateFiles.WriteAtomically(Path.Combine(paths.ReportsFolder, name + ".json"), json);
        PrivateFiles.WriteAtomically(markdownPath, markdown);
        return markdownPath;
    }
}
