using DbMigrator.Desktop.ViewModels;

namespace DbMigrator.Desktop.Tests;

public sealed class ArchitectureTests
{
    [Fact]
    public void View_models_use_no_ui_toolkit_and_no_database_driver()
    {
        var references = typeof(ShellViewModel).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();

        Assert.DoesNotContain(references, name => name.StartsWith("Avalonia", StringComparison.Ordinal));
        Assert.DoesNotContain("Microsoft.Data.SqlClient", references);
        Assert.DoesNotContain("Npgsql", references);
    }
}
