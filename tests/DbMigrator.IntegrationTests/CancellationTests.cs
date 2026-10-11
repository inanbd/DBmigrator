using DbMigrator.Core.Execution;
using DbMigrator.Core.Planning;
using DbMigrator.Core.Providers;
using DbMigrator.Core.Schema;
using DbMigrator.Providers.PostgreSql;
using DbMigrator.Providers.SqlServer;

namespace DbMigrator.IntegrationTests;

/// <summary>Cancelling a real copy part-way: the interrupted table keeps nothing, the report says so, and it is quick.</summary>
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
        await using var realTarget = new PostgreSqlProvider().OpenTarget(target);
        var migration = await MigrationEngine.PrepareAsync(sourceDb, realTarget, new MigrationOptions(), TestContext.Current.CancellationToken);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        // Cancels while writing row 50,000 of Events: mid-table, whatever the machine's speed, and
        // on the write side, where SqlClient never sees the token and must be told to stop reading.
        var targetDb = new CancelAfterRows(realTarget, "events", 50_000, cancel);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var cancelled = await Assert.ThrowsAsync<MigrationCancelledException>(() =>
            MigrationEngine.ExecuteAsync(migration, sourceDb, targetDb, progress: null, cancel.Token));

        // Without cancelling the source query, closing its reader would stream the other 950,000 rows first.
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"The cancel took {clock.Elapsed}.");
        var report = cancelled.PartialReport;
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

    [Fact]
    public async Task Closing_a_reader_part_way_through_an_endless_query_returns_at_once()
    {
        // Three cross joins of sys.all_columns: hundreds of billions of rows, so a close that
        // drained the result set would never finish.
        var source = await servers.CreateSqlServerDatabaseAsync(seedSql: """
            CREATE VIEW dbo.Endless AS SELECT CAST(1 AS int) AS N FROM sys.all_columns a CROSS JOIN sys.all_columns b CROSS JOIN sys.all_columns c;
            """);
        await using var database = new SqlServerProvider().OpenSource(source);
        var view = new TableDefinition { Name = new QualifiedName("dbo", "Endless"), Columns = [new ColumnDefinition { Name = "N", Type = new DataType(DataTypeKind.Int32), NativeType = "int" }] };
        var reader = await database.OpenTableReaderAsync(view, view.Columns, TestContext.Current.CancellationToken);
        for (var i = 0; i < 1000; i++)
        {
            Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        }

        // SqlClient closes synchronously, so the close runs elsewhere and the test can time out instead of hanging.
        var closing = Task.Run(async () => await reader.DisposeAsync(), TestContext.Current.CancellationToken);

        Assert.Same(closing, await Task.WhenAny(closing, Task.Delay(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken)));
        await closing;
    }

    /// <summary>A target whose bulk writer for one table cancels the run after a number of rows.</summary>
    private sealed class CancelAfterRows(ITargetDatabase inner, string table, long rows, CancellationTokenSource cancel) : ITargetDatabase
    {
        public Task<TargetInfo> InspectAsync(CancellationToken cancellationToken) => inner.InspectAsync(cancellationToken);

        public Task ExecuteAsync(IReadOnlyList<string> statements, bool inTransaction, CancellationToken cancellationToken) =>
            inner.ExecuteAsync(statements, inTransaction, cancellationToken);

        public async Task<IBulkWriter> OpenBulkWriterAsync(TablePlan plan, CancellationToken cancellationToken)
        {
            var writer = await inner.OpenBulkWriterAsync(plan, cancellationToken);
            return plan.Target.Name == table ? new Writer(writer, rows, cancel) : writer;
        }

        public Task<long> CountRowsAsync(QualifiedName name, CancellationToken cancellationToken) => inner.CountRowsAsync(name, cancellationToken);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class Writer(IBulkWriter inner, long rows, CancellationTokenSource cancel) : IBulkWriter
        {
            private long _written;

            public async ValueTask WriteRowAsync(object?[] values, CancellationToken cancellationToken)
            {
                if (++_written == rows)
                {
                    await cancel.CancelAsync();
                }

                await inner.WriteRowAsync(values, cancellationToken);
            }

            public Task<BulkWriteResult> CompleteAsync(CancellationToken cancellationToken) => inner.CompleteAsync(cancellationToken);

            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }
}
