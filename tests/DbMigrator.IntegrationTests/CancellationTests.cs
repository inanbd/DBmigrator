using DbMigrator.Core.Execution;
using DbMigrator.Core.Planning;
using DbMigrator.Providers.PostgreSql;
using DbMigrator.Providers.SqlServer;

namespace DbMigrator.IntegrationTests;

/// <summary>Cancelling a real copy part-way: the interrupted table keeps nothing and the report says so.</summary>
public class CancellationTests(DatabaseServers servers)
{
    private const int Rows = 1_000_000;

    private static readonly string Seed = $"""
        CREATE TABLE dbo.Customers (CustomerID int IDENTITY(1, 1) NOT NULL PRIMARY KEY, Name nvarchar(100) NOT NULL);
        CREATE TABLE dbo.Events (EventID bigint IDENTITY(1, 1) NOT NULL PRIMARY KEY, Kind varchar(10) NOT NULL, Payload nvarchar(100) NULL);
        CREATE TABLE dbo.Orders (OrderID int IDENTITY(1, 1) NOT NULL PRIMARY KEY, CustomerID int NOT NULL REFERENCES dbo.Customers (CustomerID));
        GO
        INSERT INTO dbo.Customers (Name) VALUES (N'Ada'), (N'Grace');
        INSERT INTO dbo.Orders (CustomerID) VALUES (1), (2);
        WITH n AS (SELECT TOP ({Rows}) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_columns a CROSS JOIN sys.all_columns b)
        INSERT INTO dbo.Events (Kind, Payload) SELECT CHOOSE(i % 3 + 1, 'click', 'view', 'buy'), REPLICATE(N'x', i % 50) FROM n;
        """;

    [Fact]
    public async Task Cancelling_a_large_copy_rolls_that_table_back_and_keeps_the_rest()
    {
        var source = await servers.CreateSqlServerDatabaseAsync(seedSql: Seed);
        var target = await servers.CreatePostgresDatabaseAsync();
        await using var sourceDb = new SqlServerProvider().OpenSource(source);
        await using var targetDb = new PostgreSqlProvider().OpenTarget(target);
        var migration = await MigrationEngine.PrepareAsync(sourceDb, targetDb, new MigrationOptions(), TestContext.Current.CancellationToken);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        long rowsWhenCancelled = 0;
        // Progress is reported after a second of copying, so the cancel lands mid-table.
        var progress = new Progress(e =>
        {
            if (e is TableCopyProgress { Table.Source.Name.Name: "Events" } p && !cancel.IsCancellationRequested)
            {
                rowsWhenCancelled = p.RowsCopied;
                cancel.Cancel();
            }
        });

        var cancelled = await Assert.ThrowsAsync<MigrationCancelledException>(() =>
            MigrationEngine.ExecuteAsync(migration, sourceDb, targetDb, progress, cancel.Token));

        var report = cancelled.PartialReport;
        Assert.True(rowsWhenCancelled > 0);
        Assert.Equal(MigrationPhase.CopyingData, report.CancelledDuring);
        Assert.Equal("Events", report.InterruptedTable?.Source.Name.Name);
        var status = report.Tables.ToDictionary(t => t.Table.Source.Name.Name, t => t.Status);
        Assert.Equal(TableStatus.Copied, status["Customers"]);
        Assert.Equal(TableStatus.Cancelled, status["Events"]);
        Assert.Equal(TableStatus.Cancelled, status["Orders"]);
        Assert.Equal(migration.Script.AfterData, report.StepsNotRun);
        Assert.Equal(0, await Postgres.CountAsync(target, "public.events"));
        Assert.Equal(2, await Postgres.CountAsync(target, "public.customers"));
    }

    private sealed class Progress(Action<MigrationEvent> onEvent) : IProgress<MigrationEvent>
    {
        public void Report(MigrationEvent value) => onEvent(value);
    }
}
