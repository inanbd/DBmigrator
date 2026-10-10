using System.Collections.Concurrent;
using DbMigrator.Core.Execution;

namespace DbMigrator.IntegrationTests;

public class LargeTableTests(DatabaseServers servers)
{
    private const int Rows = 250_000;

    private static readonly string Seed = $"""
        CREATE TABLE dbo.Events (
            EventID bigint IDENTITY(1, 1) NOT NULL PRIMARY KEY,
            OccurredAt datetime2(3) NOT NULL,
            Kind varchar(10) NOT NULL,
            Amount decimal(12, 2) NULL,
            Payload nvarchar(200) NULL
        );
        GO
        WITH n AS (SELECT TOP ({Rows}) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_columns a CROSS JOIN sys.all_columns b)
        INSERT INTO dbo.Events (OccurredAt, Kind, Amount, Payload)
        SELECT DATEADD(SECOND, i, '2020-01-01'),
               CHOOSE(i % 3 + 1, 'click', 'view', 'buy'),
               CASE WHEN i % 10 = 0 THEN NULL ELSE CAST(i % 10000 AS decimal(12, 2)) / 100 END,
               REPLICATE(N'é', i % 50)
        FROM n;
        """;

    [Fact]
    public async Task Copies_a_quarter_million_rows_exactly()
    {
        var source = await servers.CreateSqlServerDatabaseAsync(seedSql: Seed);
        var target = await servers.CreatePostgresDatabaseAsync();
        var events = new ConcurrentQueue<MigrationEvent>();

        var report = await Migrations.RunAsync(source, target, progress: new Progress(events));

        Migrations.AssertSucceeded(report);
        Assert.Equal(Rows, report.TotalRowsCopied);
        var totals = await Postgres.PairsAsync(target, """
            SELECT 'count', count(*)::text FROM public.events
            UNION ALL SELECT 'ids', sum(eventid)::text FROM public.events
            UNION ALL SELECT 'amount', sum(amount)::text FROM public.events
            UNION ALL SELECT 'nulls', count(*) FILTER (WHERE amount IS NULL)::text FROM public.events
            UNION ALL SELECT 'chars', sum(length(payload))::text FROM public.events
            UNION ALL SELECT 'last', max(occurredat)::text FROM public.events
            """);

        Assert.Equal(Rows.ToString(), totals["count"]);
        Assert.Equal(((long)Rows * (Rows + 1) / 2).ToString(), totals["ids"]);
        Assert.Equal(Enumerable.Range(1, Rows).Where(i => i % 10 != 0).Sum(i => (decimal)(i % 10000) / 100).ToString("0.00"), totals["amount"]);
        Assert.Equal((Rows / 10).ToString(), totals["nulls"]);
        Assert.Equal(Enumerable.Range(1, Rows).Sum(i => (long)(i % 50)).ToString(), totals["chars"]);
        Assert.Equal(new DateTime(2020, 1, 1).AddSeconds(Rows).ToString("yyyy-MM-dd HH:mm:ss"), totals["last"]);
        Assert.Contains(events, e => e is TableCopyFinished { Result.RowsCopied: Rows });
    }

    /// <summary>Records events synchronously; System.Progress would post them to the thread pool.</summary>
    private sealed class Progress(ConcurrentQueue<MigrationEvent> events) : IProgress<MigrationEvent>
    {
        public void Report(MigrationEvent value) => events.Enqueue(value);
    }
}
