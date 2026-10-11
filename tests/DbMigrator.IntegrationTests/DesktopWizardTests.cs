using DbMigrator.Core.Planning;
using DbMigrator.Desktop.Testing;
using DbMigrator.Desktop.ViewModels;
using DbMigrator.Providers;

namespace DbMigrator.IntegrationTests;

/// <summary>The desktop wizard's view models, end to end, against real SQL Server and PostgreSQL.</summary>
public class DesktopWizardTests(DatabaseServers servers)
{
    private const string Seed = """
        CREATE TABLE dbo.Customers (CustomerID int IDENTITY(1, 1) NOT NULL PRIMARY KEY, Name nvarchar(100) NOT NULL);
        CREATE TABLE dbo.Orders (
            OrderID int IDENTITY(1, 1) NOT NULL PRIMARY KEY,
            CustomerID int NOT NULL REFERENCES dbo.Customers (CustomerID),
            Total decimal(10, 2) NOT NULL,
            PlacedAt datetime2(3) NOT NULL);
        GO
        INSERT INTO dbo.Customers (Name) VALUES (N'Ada'), (N'Grace'), (N'Edsger');
        INSERT INTO dbo.Orders (CustomerID, Total, PlacedAt) VALUES (1, 10.50, '2026-01-02T03:04:05.678'), (1, 7.25, '2026-02-01'), (2, 99.99, '2026-03-01'), (3, 0.01, '2026-04-01');
        """;

    [Fact]
    public Task A_migration_through_the_wizard() => UiThreadContext.RunAsync(async () =>
    {
        var source = await servers.CreateSqlServerDatabaseAsync(seedSql: Seed);
        var target = await servers.CreatePostgresDatabaseAsync();
        await Postgres.ExecuteAsync(target, "CREATE TABLE public.customers (old_column int)");
        var catalog = BuiltInProviders.Catalog;
        var dialogs = new DialogHostViewModel();
        _ = new ScriptedDialogs(dialogs);
        var reports = new InMemoryReportArchive();
        var log = new InMemoryAppLog();
        var services = new ShellServices(
            catalog, dialogs, new FakeFileService(), new FakeClipboard(), new ManualUiScheduler(), new InMemoryCredentialStore(),
            new InMemoryProfileStore(catalog), new InMemorySettingsStore(), reports, log, TimeProvider.System);
        var shell = new ShellViewModel(services);
        await shell.StartAsync();

        // Source: the connection string is pasted; its password moves into the password box.
        shell.Source.SelectedProvider = shell.Source.Providers.Single(p => p.Provider.Id == "sqlserver");
        Paste(shell.Source.Editor!, source);
        await shell.Source.ConnectCommand.ExecuteAsync(null);
        Assert.True(shell.Source.IsConnected, shell.Source.Error);
        Assert.Equal("2 tables in 1 schema visible to this login", shell.Source.Summary!.TablesText);
        await shell.NextCommand.ExecuteAsync(null);

        // Target
        shell.Target.SelectedProvider = shell.Target.Providers.Single(p => p.Provider.Id == "postgresql");
        Paste(shell.Target.Editor!, target);
        await shell.Target.ConnectCommand.ExecuteAsync(null);
        Assert.True(shell.Target.IsConnected, shell.Target.Error);
        await WaitAsync(() => shell.Target.MatchingTableCount is not null, "the matching count");
        Assert.Equal(1, shell.Target.MatchingTableCount);
        await shell.NextCommand.ExecuteAsync(null);
        await shell.NextCommand.ExecuteAsync(null);

        // Review: public.customers exists; drop and recreate it.
        Assert.Same(shell.Review, shell.Current);
        var conflict = Assert.Single(shell.Review.Conflicts);
        conflict.Choice = ConflictChoice.For(TableExistsAction.DropAndRecreate);
        await WaitAsync(() => !shell.Review.IsPlanning && shell.Review.UndecidedCount == 0, "the re-plan");
        await shell.NextCommand.ExecuteAsync(null);

        // Run
        Assert.Same(shell.Run, shell.Current);
        Assert.True(shell.Run.RequiresAcknowledgement);
        shell.Run.Acknowledged = true;
        await shell.Run.StartCommand.ExecuteAsync(null);

        Assert.Same(shell.Report, shell.Current);
        Assert.Equal(ReportOutcome.Succeeded, shell.Report.Outcome);
        Assert.All(shell.Run.Tables, t => Assert.Equal(TableRunStatus.Verified, t.Status));
        Assert.Equal(3, await Postgres.CountAsync(target, "public.customers"));
        Assert.Equal(4, await Postgres.CountAsync(target, "public.orders"));
        Assert.Equal("10.50", await Postgres.ScalarAsync<string>(target, "SELECT total::text FROM public.orders WHERE orderid = 1"));

        // The saved report describes both databases without their passwords.
        var saved = Assert.Single(reports.Saved);
        var password = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(source).Password;
        Assert.DoesNotContain(password, saved.Markdown, StringComparison.Ordinal);
        Assert.DoesNotContain(password, saved.Json, StringComparison.Ordinal);
        Assert.DoesNotContain("Password=", string.Join("\n", log.Lines), StringComparison.OrdinalIgnoreCase);
        await shell.DisposeAsync();
    });

    private static void Paste(ConnectionEditorViewModel editor, string connectionString)
    {
        editor.IsRawMode = true;
        editor.RawText = connectionString;
        editor.IsRawMode = false;
        Assert.Null(editor.RawError);
    }

    private static async Task WaitAsync(Func<bool> condition, string what)
    {
        var deadline = Environment.TickCount64 + 60_000;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException($"Timed out waiting for {what}.");
            }

            await Task.Delay(20);
        }
    }
}
