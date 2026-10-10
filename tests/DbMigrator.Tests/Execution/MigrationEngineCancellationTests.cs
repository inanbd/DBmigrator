using DbMigrator.Core.Execution;
using DbMigrator.Core.Planning;
using DbMigrator.Core.Schema;
using DbMigrator.TestSupport;
using static DbMigrator.Tests.Execution.EngineHarness;

namespace DbMigrator.Tests.Execution;

public class MigrationEngineCancellationTests
{
    private static async Task<MigrationReport> CancelledReportAsync(Task run)
    {
        var cancelled = await Assert.ThrowsAsync<MigrationCancelledException>(() => run);
        var report = cancelled.PartialReport;
        Assert.True(report.Cancelled);
        Assert.False(report.Succeeded);
        Assert.Null(report.FatalError);
        return report;
    }

    private static TableStatus StatusOf(MigrationReport report, QualifiedName table) =>
        report.Tables.Single(t => t.Table.Source.Name == table).Status;

    private static void AssertEveryTableAccountedFor(EngineHarness harness, MigrationReport report)
    {
        Assert.Equal(harness.Migration.Plan.Tables.Select(t => t.Source.Name), report.Tables.Select(t => t.Table.Source.Name));
        Assert.Equal(harness.Migration.Script.AfterData, [.. report.Steps.Select(s => s.Step), .. report.StepsNotRun]);
        Assert.All(report.Steps, s => Assert.True(s.Succeeded));
    }

    [Fact]
    public async Task A_run_that_is_not_cancelled_reports_nothing_left_undone()
    {
        var harness = await CreateAsync();

        var report = await harness.RunAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(report.Succeeded);
        Assert.False(report.Cancelled);
        Assert.Empty(report.StepsNotRun);
        Assert.Equal(5, harness.Target.RowsOf(new QualifiedName("public", "a")).Count);
    }

    [Fact]
    public async Task Cancelling_while_creating_tables_changes_nothing()
    {
        var harness = await CreateAsync();
        using var cancel = new CancellationTokenSource();
        var progress = new SyncProgress(e =>
        {
            if (e is PhaseStarted { Phase: MigrationPhase.CreatingTables })
            {
                cancel.Cancel();
            }
        });

        var report = await CancelledReportAsync(harness.RunAsync(progress, cancel.Token));

        Assert.Equal(MigrationPhase.CreatingTables, report.CancelledDuring);
        Assert.All(report.Tables, t => Assert.Equal(TableStatus.Cancelled, t.Status));
        Assert.Empty(report.Steps);
        Assert.Empty(harness.Target.Statements);
        Assert.Empty(ReportWriter.LeftEmpty(report));
        AssertEveryTableAccountedFor(harness, report);
    }

    [Fact]
    public async Task Cancelling_mid_table_rolls_it_back_and_reports_emptied_tables()
    {
        var harness = await CreateAsync();
        var gate = new Gate();
        harness.Source.Pauses[A] = (2, gate);
        using var cancel = new CancellationTokenSource();

        var run = harness.RunAsync(cancellationToken: cancel.Token);
        await gate.Reached;
        await cancel.CancelAsync();
        var report = await CancelledReportAsync(run);

        Assert.Equal(MigrationPhase.CopyingData, report.CancelledDuring);
        Assert.Equal(A, report.InterruptedTable?.Source.Name);
        var interrupted = report.Tables.Single(t => t.Table.Source.Name == A);
        Assert.Equal(TableStatus.Cancelled, interrupted.Status);
        Assert.Contains("2 row(s) read so far were rolled back", interrupted.Error);
        Assert.Equal(TableStatus.Cancelled, StatusOf(report, B));
        Assert.Contains("Not started", report.Tables.Single(t => t.Table.Source.Name == B).Error);
        Assert.Empty(harness.Target.RowsOf(new QualifiedName("public", "a")));
        Assert.Equal([A, B], ReportWriter.LeftEmpty(report).Select(t => t.Table.Source.Name));
        Assert.Equal(harness.Migration.Script.AfterData, report.StepsNotRun);
        AssertEveryTableAccountedFor(harness, report);
    }

    [Fact]
    public async Task A_driver_exception_after_cancelling_still_counts_as_cancelled()
    {
        var harness = await CreateAsync();
        var gate = new Gate();
        harness.Source.Pauses[C] = (1, gate);
        harness.Source.CancellationError = () => new InvalidOperationException("Operation cancelled by user.");
        using var cancel = new CancellationTokenSource();

        var run = harness.RunAsync(cancellationToken: cancel.Token);
        await gate.Reached;
        await cancel.CancelAsync();
        var report = await CancelledReportAsync(run);

        Assert.Equal(TableStatus.Copied, StatusOf(report, A));
        Assert.Equal(TableStatus.Copied, StatusOf(report, B));
        Assert.Equal(TableStatus.Cancelled, StatusOf(report, C));
        Assert.Equal(TableStatus.Cancelled, StatusOf(report, D));
        Assert.DoesNotContain(report.Tables, t => t.Status == TableStatus.Failed);
        Assert.Empty(ReportWriter.LeftEmpty(report));
    }

    [Fact]
    public async Task Cancelling_while_building_keys_lists_the_steps_not_run()
    {
        var harness = await CreateAsync();
        var gate = new Gate();
        harness.Target.PausedStatements["FOREIGN KEY"] = gate;
        using var cancel = new CancellationTokenSource();

        var run = harness.RunAsync(cancellationToken: cancel.Token);
        await gate.Reached;
        await cancel.CancelAsync();
        var report = await CancelledReportAsync(run);

        Assert.Equal(MigrationPhase.CreatingConstraints, report.CancelledDuring);
        Assert.All(report.Tables, t => Assert.Equal(TableStatus.Copied, t.Status));
        Assert.All(report.Steps, s => Assert.Contains("PRIMARY KEY", s.Step.Sql));
        Assert.Contains("FOREIGN KEY", report.StepsNotRun[0].Sql);
        Assert.Null(report.InterruptedTable);
        AssertEveryTableAccountedFor(harness, report);
    }

    [Fact]
    public async Task Cancelling_while_verifying_keeps_the_counts_already_taken()
    {
        var harness = await CreateAsync();
        using var cancel = new CancellationTokenSource();
        var progress = new SyncProgress(e =>
        {
            if (e is TableVerifyStarted { Index: 2 })
            {
                cancel.Cancel();
            }
        });

        var report = await CancelledReportAsync(harness.RunAsync(progress, cancel.Token));

        Assert.Equal(MigrationPhase.VerifyingRowCounts, report.CancelledDuring);
        Assert.Empty(report.StepsNotRun);
        Assert.Equal(2, report.Tables.Count(t => t.SourceRowCount is not null));
        Assert.All(report.Tables, t => Assert.Equal(TableStatus.Copied, t.Status));
        AssertEveryTableAccountedFor(harness, report);
    }

    [Fact]
    public async Task A_real_failure_without_a_cancel_is_still_a_failure()
    {
        var harness = await CreateAsync();
        harness.Source.Failures[C] = (1, new InvalidOperationException("disk full"));

        var report = await harness.RunAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(report.Cancelled);
        Assert.Equal(TableStatus.Failed, StatusOf(report, C));
        Assert.Contains("disk full", report.Tables.Single(t => t.Table.Source.Name == C).Error);
        Assert.Equal(TableStatus.Copied, StatusOf(report, D));
    }

    [Fact]
    public async Task Cancelled_report_text_says_what_was_left_behind()
    {
        var harness = await CreateAsync();
        var gate = new Gate();
        harness.Source.Pauses[A] = (2, gate);
        using var cancel = new CancellationTokenSource();
        var run = harness.RunAsync(cancellationToken: cancel.Token);
        await gate.Reached;
        await cancel.CancelAsync();
        var report = await CancelledReportAsync(run);
        using var text = new StringWriter();

        ReportWriter.WriteReport(text, report);

        var lines = text.ToString().Split(Environment.NewLine);
        Assert.Contains(lines, l => l.StartsWith("Cancelled while copying data after ", StringComparison.Ordinal));
        Assert.Contains("Copied 0 rows into 0 table(s); rolled back dbo.A; 3 table(s) not started.", lines);
        Assert.Contains("2 table(s) emptied or recreated at the start are now empty: public.a, public.b.", lines);
        Assert.Contains($"{harness.Migration.Script.AfterData.Count} key, index and identity step(s) did not run.", lines);
    }
}
