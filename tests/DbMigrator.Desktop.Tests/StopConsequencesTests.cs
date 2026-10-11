using DbMigrator.Core.Execution;
using DbMigrator.Core.Planning;
using DbMigrator.Desktop.Testing;
using DbMigrator.Desktop.ViewModels;

namespace DbMigrator.Desktop.Tests;

public sealed class StopConsequencesTests
{
    private readonly ManualTimeProvider _time = new();

    [Fact]
    public void Before_copying_nothing_has_changed()
    {
        var tracker = new RunTracker(Plans.Prepare(), _time);
        tracker.Apply(new PhaseStarted(MigrationPhase.CreatingTables).At(_time.GetUtcNow()));

        Assert.Equal(
            ["Nothing has been changed yet: creating the tables runs in one transaction, which is rolled back."],
            StopConsequences.From(tracker));
    }

    [Fact]
    public void While_copying_it_names_the_tables_that_will_stay_empty()
    {
        // C is emptied and D dropped at the start; the copy is on B, so neither is reloaded.
        var migration = Plans.Prepare(decisions: new Dictionary<Core.Schema.QualifiedName, TableExistsAction>
        {
            [TestServices.C] = TableExistsAction.Truncate,
            [TestServices.D] = TableExistsAction.DropAndRecreate,
        });
        var tracker = new RunTracker(migration, _time);
        var a = migration.Table(TestServices.A);
        tracker.Apply(new PhaseStarted(MigrationPhase.CopyingData).At(_time.GetUtcNow()));
        tracker.Apply(new TableCopyStarted(a).At(_time.GetUtcNow()));
        tracker.Apply(new TableCopyFinished(new TableResult { Table = a, Status = TableStatus.Copied, RowsCopied = 1_500 }).At(_time.GetUtcNow()));
        tracker.Apply(new TableCopyStarted(migration.Table(TestServices.B)).At(_time.GetUtcNow()));

        Assert.Equal(
            [
                "Tables already copied stay: 1 table, 1,500 rows.",
                "The table being copied, dbo.B, is rolled back; none of its rows are kept.",
                "2 tables are not copied yet. 2 of them were emptied or dropped and recreated at the start of the run (public.c, public.d) and will stay EMPTY until you run again.",
                $"Keys, indexes and foreign keys are not built yet: {migration.Script.AfterData.Count} steps. Their SQL will be in the report.",
            ],
            StopConsequences.From(tracker));
    }

    [Fact]
    public void While_copying_with_nothing_emptied_it_does_not_mention_empty_tables()
    {
        var migration = Plans.Prepare();
        var tracker = new RunTracker(migration, _time);
        tracker.Apply(new PhaseStarted(MigrationPhase.CopyingData).At(_time.GetUtcNow()));
        tracker.Apply(new TableCopyStarted(migration.Table(TestServices.A)).At(_time.GetUtcNow()));

        var bullets = StopConsequences.From(tracker);

        Assert.Equal("3 tables are not copied yet.", bullets[2]);
        Assert.DoesNotContain(bullets, b => b.Contains("EMPTY", StringComparison.Ordinal));
    }

    [Fact]
    public void While_building_keys_it_counts_the_steps_left()
    {
        var migration = Plans.Prepare();
        var tracker = new RunTracker(migration, _time);
        tracker.Apply(new PhaseStarted(MigrationPhase.CreatingConstraints).At(_time.GetUtcNow()));
        var first = migration.Script.AfterData[0];
        tracker.Apply(new StepStarted(first, 0, migration.Script.AfterData.Count).At(_time.GetUtcNow()));
        tracker.Apply(new StepFinished(new StepResult(first, TimeSpan.FromSeconds(1))).At(_time.GetUtcNow()));

        var total = migration.Script.AfterData.Count;
        Assert.Equal(
            [$"All data is copied. 1 of {total} key and index steps are built; the remaining {total - 1} will not run (their SQL will be in the report)."],
            StopConsequences.From(tracker));
    }

    [Fact]
    public void While_counting_rows_it_says_which_counts_are_skipped()
    {
        var migration = Plans.Prepare();
        var tracker = new RunTracker(migration, _time);
        tracker.Apply(new PhaseStarted(MigrationPhase.CopyingData).At(_time.GetUtcNow()));
        foreach (var table in migration.Plan.Tables)
        {
            tracker.Apply(new TableCopyStarted(table).At(_time.GetUtcNow()));
            tracker.Apply(new TableCopyFinished(new TableResult { Table = table, Status = TableStatus.Copied, RowsCopied = 5 }).At(_time.GetUtcNow()));
        }

        tracker.Apply(new PhaseStarted(MigrationPhase.VerifyingRowCounts).At(_time.GetUtcNow()));
        var a = migration.Table(TestServices.A);
        tracker.Apply(new TableVerified(new TableResult { Table = a, Status = TableStatus.Copied, RowsCopied = 5, SourceRowCount = 5, TargetRowCount = 5 }).At(_time.GetUtcNow()));

        Assert.Equal(
            ["All data is copied. The keys and indexes are done. Row counts for 3 tables will not be checked."],
            StopConsequences.From(tracker));
    }

    [Fact]
    public void A_table_emptied_at_the_start_and_being_copied_is_named_on_its_own()
    {
        // B, C and D are all emptied at the start; the copy is on B.
        var migration = Plans.Prepare(decisions: new Dictionary<Core.Schema.QualifiedName, TableExistsAction>
        {
            [TestServices.B] = TableExistsAction.Truncate,
            [TestServices.C] = TableExistsAction.Truncate,
            [TestServices.D] = TableExistsAction.Truncate,
        });
        var tracker = new RunTracker(migration, _time);
        tracker.Apply(new PhaseStarted(MigrationPhase.CopyingData).At(_time.GetUtcNow()));
        var a = migration.Table(TestServices.A);
        tracker.Apply(new TableCopyStarted(a).At(_time.GetUtcNow()));
        tracker.Apply(new TableCopyFinished(new TableResult { Table = a, Status = TableStatus.Copied, RowsCopied = 5 }).At(_time.GetUtcNow()));
        tracker.Apply(new TableCopyStarted(migration.Table(TestServices.B)).At(_time.GetUtcNow()));

        var bullets = StopConsequences.From(tracker);

        Assert.Equal("The table being copied, dbo.B, is rolled back; none of its rows are kept. It was emptied at the start of the run, so public.b will stay EMPTY until you run again.", bullets[1]);
        Assert.Equal("2 tables are not copied yet. 2 of them were emptied or dropped and recreated at the start of the run (public.c, public.d) and will stay EMPTY until you run again.", bullets[2]);
    }

    [Fact]
    public void Later_phases_do_not_claim_a_copy_that_failed()
    {
        var migration = Plans.Prepare();
        var tracker = new RunTracker(migration, _time);
        tracker.Apply(new PhaseStarted(MigrationPhase.CopyingData).At(_time.GetUtcNow()));
        var a = migration.Table(TestServices.A);
        tracker.Apply(new TableCopyStarted(a).At(_time.GetUtcNow()));
        tracker.Apply(new TableCopyFinished(new TableResult { Table = a, Status = TableStatus.Failed, Error = "disk full" }).At(_time.GetUtcNow()));
        tracker.Apply(new PhaseStarted(MigrationPhase.CreatingConstraints).At(_time.GetUtcNow()));

        Assert.StartsWith("The data copy is over; 1 table failed. 0 of", StopConsequences.From(tracker)[0], StringComparison.Ordinal);
    }
}
