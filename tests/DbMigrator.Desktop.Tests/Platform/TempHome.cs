using DbMigrator.Desktop.Platform;

namespace DbMigrator.Desktop.Tests.Platform;

/// <summary>A throwaway folder standing in for the user's app folders.</summary>
internal sealed class TempHome : IDisposable
{
    public TempHome()
    {
        Root = Path.Combine(Path.GetTempPath(), "dbmigrator-tests", Guid.NewGuid().ToString("N"));
        Paths = AppPaths.UnderHome(Root);
    }

    public string Root { get; }

    public AppPaths Paths { get; }

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}
