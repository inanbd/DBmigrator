using DbMigrator.Core.Execution;
using DbMigrator.Core.Planning;
using DbMigrator.Desktop.Testing;
using DbMigrator.Desktop.ViewModels;

namespace DbMigrator.Desktop.Tests;

public sealed class RunTrackerTests
{
    private readonly ManualTimeProvider _time = new();

    [Fact]
    public void Follows_a_table_from_waiting_to_verified()
    {
        var migration = Plans.Prepare();
        var tracker = new RunTracker(migration, _time);
        var a = migration.Table(TestServices.A);
        var row = tracker.Row(TestServices.A);
        Assert.Equal(TableRunStatus.Waiting, row.Status);

        tracker.Apply(new PhaseStarted(MigrationPhase.CopyingData).At(_time.GetUtcNow()));
        tracker.Apply(new TableCopyStarted(a).At(_time.GetUtcNow()));
        Assert.Equal(TableRunStatus.Copying, row.Status);
        Assert.Same(row, tracker.Copying);

        tracker.ApplyProgress(new TableCopyProgress(a, 400, TimeSpan.FromSeconds(2)));
        Assert.Equal(400, row.RowsCopied);
        Assert.Equal(40, row.Percent);
        Assert.False(row.IsIndeterminate);

        tracker.Apply(new TableCopyFinished(new TableResult { Table = a, Status = TableStatus.Copied, RowsCopied = 1000, Elapsed = TimeSpan.FromSeconds(4) }).At(_time.GetUtcNow()));
        Assert.Equal(TableRunStatus.Copied, row.Status);
        Assert.Null(tracker.Copying);

        tracker.Apply(new PhaseStarted(MigrationPhase.VerifyingRowCounts).At(_time.GetUtcNow()));
        tracker.Apply(new TableVerifyStarted(a, 1, 4).At(_time.GetUtcNow()));
        Assert.Same(row, tracker.Verifying);
        tracker.Apply(new TableVerified(new TableResult { Table = a, Status = TableStatus.Copied, RowsCopied = 1000, SourceRowCount = 1000, TargetRowCount = 1000 }).At(_time.GetUtcNow()));
        Assert.Equal(TableRunStatus.Verified, row.Status);
        Assert.Equal("Copied, counts match", row.StatusText);
    }

    [Fact]
    public void A_table_past_its_estimate_shows_an_indeterminate_bar()
    {
        var migration = Plans.Prepare(estimate: 10);
        var tracker = new RunTracker(migration, _time);
        var a = migration.Table(TestServices.A);
        tracker.Apply(new TableCopyStarted(a).At(_time.GetUtcNow()));

        tracker.ApplyProgress(new TableCopyProgress(a, 25, TimeSpan.FromSeconds(1)));

        var row = tracker.Row(TestServices.A);
        Assert.True(row.IsIndeterminate);
        Assert.Equal(100, row.Percent);
    }

    [Fact]
    public void A_table_without_an_estimate_shows_an_indeterminate_bar()
    {
        var migration = Plans.Prepare(estimate: null);
        var tracker = new RunTracker(migration, _time);
        tracker.Apply(new TableCopyStarted(migration.Table(TestServices.A)).At(_time.GetUtcNow()));

        Assert.True(tracker.Row(TestServices.A).IsIndeterminate);
    }

    [Fact]
    public void Phases_advance_and_show_their_elapsed_time()
    {
        var tracker = new RunTracker(Plans.Prepare(), _time);
        tracker.Apply(new PhaseStarted(MigrationPhase.CreatingTables).At(_time.GetUtcNow()));
        _time.Advance(TimeSpan.FromSeconds(3));
        tracker.Apply(new PhaseStarted(MigrationPhase.CopyingData).At(_time.GetUtcNow()));

        Assert.Equal(PhaseState.Done, tracker.Phases[0].State);
        Assert.Equal("3.0s", tracker.Phases[0].ElapsedText);
        Assert.Equal(PhaseState.Running, tracker.Phases[1].State);
        Assert.Equal(PhaseState.Waiting, tracker.Phases[2].State);
    }

    [Fact]
    public void The_final_report_marks_unreached_tables_not_started_and_the_interrupted_one_rolled_back()
    {
        var migration = Plans.Prepare();
        var tracker = new RunTracker(migration, _time);
        var a = migration.Table(TestServices.A);
        var b = migration.Table(TestServices.B);
        tracker.Apply(new PhaseStarted(MigrationPhase.CopyingData).At(_time.GetUtcNow()));
        tracker.Apply(new TableCopyStarted(a).At(_time.GetUtcNow()));
        tracker.Apply(new TableCopyFinished(new TableResult { Table = a, Status = TableStatus.Copied, RowsCopied = 5 }).At(_time.GetUtcNow()));
        tracker.Apply(new TableCopyStarted(b).At(_time.GetUtcNow()));

        tracker.ApplyReport(new MigrationReport
        {
            Plan = migration.Plan,
            Tables =
            [
                new TableResult { Table = a, Status = TableStatus.Copied, RowsCopied = 5 },
                new TableResult { Table = b, Status = TableStatus.Cancelled, RowsCopied = 2 },
                new TableResult { Table = migration.Table(TestServices.C), Status = TableStatus.Cancelled, Error = "Not started: the run was cancelled first." },
                new TableResult { Table = migration.Table(TestServices.D), Status = TableStatus.Cancelled, Error = "Not started: the run was cancelled first." },
            ],
            Steps = [],
            StepsNotRun = migration.Script.AfterData,
            Issues = [],
            Elapsed = TimeSpan.FromSeconds(9),
            CancelledDuring = MigrationPhase.CopyingData,
            InterruptedTable = b,
        });

        Assert.Equal(TableRunStatus.Copied, tracker.Row(TestServices.A).Status);
        Assert.Equal(TableRunStatus.RolledBack, tracker.Row(TestServices.B).Status);
        Assert.Equal(TableRunStatus.NotStarted, tracker.Row(TestServices.C).Status);
        Assert.Equal(PhaseState.Stopped, tracker.Phases[1].State);
        Assert.Null(tracker.Copying);
    }

    [Fact]
    public void A_failure_report_keeps_what_was_seen()
    {
        var migration = Plans.Prepare();
        var tracker = new RunTracker(migration, _time);
        var a = migration.Table(TestServices.A);
        tracker.Apply(new TableCopyStarted(a).At(_time.GetUtcNow()));
        tracker.Apply(new TableCopyFinished(new TableResult { Table = a, Status = TableStatus.Copied, RowsCopied = 5 }).At(_time.GetUtcNow()));
        tracker.Apply(new TableCopyStarted(migration.Table(TestServices.B)).At(_time.GetUtcNow()));

        var report = tracker.ToFailureReport(new InvalidOperationException("boom"));

        Assert.Equal("The migration stopped unexpectedly: boom", report.FatalError);
        Assert.Equal(TableStatus.Copied, report.Tables[0].Status);
        Assert.Equal(TableStatus.Failed, report.Tables[1].Status);
        Assert.Equal(TableStatus.NotCopied, report.Tables[2].Status);
        Assert.Equal(migration.Script.AfterData, report.StepsNotRun);
    }

    [Fact]
    public void Skipped_tables_and_schema_only_runs_get_no_progress_rows_to_wait_for()
    {
        var migration = Plans.Prepare(options: new MigrationOptions { CopyData = false });
        var tracker = new RunTracker(migration, _time);

        Assert.All(tracker.Rows, r => Assert.Equal(TableRunStatus.Skipped, r.Status));
        Assert.Equal(0, tracker.DataTableCount);
    }

    [Fact]
    public void Counters_count_from_one_and_unchecked_counts_are_not_called_a_match()
    {
        var migration = Plans.Prepare(estimate: 0);
        var tracker = new RunTracker(migration, _time);
        var a = migration.Table(TestServices.A);
        var row = tracker.Row(TestServices.A);
        var changed = new List<string?>();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        tracker.Apply(new TableCopyStarted(a).At(_time.GetUtcNow()));
        tracker.Apply(new TableCopyFinished(new TableResult { Table = a, Status = TableStatus.Copied }).At(_time.GetUtcNow()));
        tracker.Apply(new StepStarted(migration.Script.AfterData[0], 0, migration.Script.AfterData.Count).At(_time.GetUtcNow()));
        tracker.Apply(new TableVerifyStarted(a, 0, 4).At(_time.GetUtcNow()));
        tracker.Apply(new TableVerified(new TableResult { Table = a, Status = TableStatus.Copied, Error = "Row counts could not be checked: timeout" }).At(_time.GetUtcNow()));

        Assert.Equal(1, tracker.CurrentStepIndex);
        Assert.Equal(1, tracker.VerifyIndex);
        Assert.Equal(TableRunStatus.Copied, row.Status);
        Assert.Equal("Row counts could not be checked: timeout", row.Message);

        // An empty table never reports progress: its bar is refreshed by the status alone.
        Assert.Contains(nameof(TableProgressRowViewModel.Percent), changed);
        Assert.Equal(100, row.Percent);
    }
}
