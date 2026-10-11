using DbMigrator.Core.Execution;
using DbMigrator.Desktop.Testing;
using DbMigrator.Desktop.ViewModels;

namespace DbMigrator.Desktop.Tests;

public sealed class RunProgressHubTests
{
    private readonly ManualTimeProvider _time = new();

    [Fact]
    public void Keeps_only_the_newest_progress_per_table()
    {
        var migration = Plans.Prepare();
        var a = migration.Table(TestServices.A);
        var b = migration.Table(TestServices.B);
        var hub = new RunProgressHub(_time);
        for (var i = 1; i <= 1000; i++)
        {
            hub.Report(new TableCopyProgress(a, i, TimeSpan.FromSeconds(i)));
        }

        hub.Report(new TableCopyProgress(b, 7, TimeSpan.Zero));

        var batch = hub.Drain(1000);

        Assert.Empty(batch.Discrete);
        Assert.Equal(2, batch.Latest.Count);
        Assert.Equal(1000, batch.Latest.Single(p => p.Table == a).RowsCopied);
        Assert.Empty(hub.Drain(1000).Latest);
    }

    [Fact]
    public void Drains_discrete_events_in_order_and_in_bounded_batches()
    {
        var migration = Plans.Prepare();
        var hub = new RunProgressHub(_time);
        hub.Report(new PhaseStarted(MigrationPhase.CreatingTables));
        _time.Advance(TimeSpan.FromSeconds(1));
        foreach (var table in migration.Plan.Tables)
        {
            hub.Report(new TableCopyStarted(table));
        }

        var first = hub.Drain(3);
        var second = hub.Drain(3);

        Assert.True(first.More);
        Assert.False(second.More);
        Assert.IsType<PhaseStarted>(first.Discrete[0].Event);
        Assert.Equal(_time.GetUtcNow() - TimeSpan.FromSeconds(1), first.Discrete[0].At);
        Assert.Equal(
            migration.Plan.Tables,
            first.Discrete.Skip(1).Concat(second.Discrete).Select(e => ((TableCopyStarted)e.Event).Table));
    }

    [Fact]
    public void A_finished_table_drops_its_pending_progress()
    {
        var migration = Plans.Prepare();
        var a = migration.Table(TestServices.A);
        var hub = new RunProgressHub(_time);
        hub.Report(new TableCopyProgress(a, 3, TimeSpan.Zero));
        hub.Report(new TableCopyFinished(new TableResult { Table = a, Status = TableStatus.Copied, RowsCopied = 5 }));

        var batch = hub.Drain(10);

        Assert.Empty(batch.Latest);
        Assert.Single(batch.Discrete);
    }
}
