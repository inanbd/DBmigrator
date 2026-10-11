using DbMigrator.Core.Execution;
using DbMigrator.Core.Planning;
using DbMigrator.Desktop.ViewModels;

namespace DbMigrator.Desktop.Tests;

public sealed class ReportModelTests
{
    private static readonly IReadOnlyDictionary<Core.Schema.QualifiedName, TableExistsAction> Truncates = new Dictionary<Core.Schema.QualifiedName, TableExistsAction>
    {
        [Testing.TestServices.C] = TableExistsAction.Truncate,
    };

    [Fact]
    public void Outcomes_map_from_the_report()
    {
        var migration = Plans.Prepare();
        var ok = Report(migration, TableStatus.Copied);

        Assert.Equal(ReportOutcome.Succeeded, ReportSummary.Outcome(ok, null));
        Assert.Equal(ReportOutcome.CompletedWithProblems, ReportSummary.Outcome(Report(migration, TableStatus.Failed), null));
        Assert.Equal(ReportOutcome.CompletedWithProblems, ReportSummary.Outcome(Report(migration, TableStatus.CountMismatch), null));
        Assert.Equal(ReportOutcome.Cancelled, ReportSummary.Outcome(ok with { CancelledDuring = MigrationPhase.CopyingData }, null));
        Assert.Equal(ReportOutcome.Stopped, ReportSummary.Outcome(ok with { FatalError = "create failed" }, null));
        Assert.Equal(ReportOutcome.Crashed, ReportSummary.Outcome(ok, new InvalidOperationException()));
    }

    [Fact]
    public void Headlines_say_what_happened()
    {
        var migration = Plans.Prepare();
        var ok = Report(migration, TableStatus.Copied);
        Assert.Equal("Migration succeeded", ReportSummary.Headline(ok, ReportOutcome.Succeeded));
        var steps = migration.Script.AfterData.Count;
        Assert.Equal(
            $"Copied 20 rows into 4 tables in 3.0s · built {steps} of {steps} key, index and identity steps · row counts match",
            ReportSummary.SubHeadline(ok, ReportOutcome.Succeeded, migration.Options));

        var failed = Report(migration, TableStatus.Failed);
        Assert.StartsWith("4 tables failed · Copied", ReportSummary.SubHeadline(failed, ReportOutcome.CompletedWithProblems, migration.Options), StringComparison.Ordinal);

        var cancelled = ok with { CancelledDuring = MigrationPhase.CreatingTables };
        Assert.Equal("Cancelled while creating tables after 3.0s", ReportSummary.Headline(cancelled, ReportOutcome.Cancelled));
        Assert.Equal("That step runs in one transaction, which was rolled back. Nothing was changed.", ReportSummary.SubHeadline(cancelled, ReportOutcome.Cancelled, migration.Options));
    }

    [Fact]
    public void Tables_are_left_empty_only_when_the_cancel_came_after_creating_them()
    {
        var migration = Plans.Prepare(decisions: Truncates);
        var report = Report(migration, TableStatus.Cancelled) with { CancelledDuring = MigrationPhase.CreatingTables, Steps = [], StepsNotRun = migration.Script.AfterData };

        Assert.DoesNotContain(ReportAttention.Build(report, migration.Options), g => g.Kind == AttentionKind.LeftEmpty);

        var later = report with { CancelledDuring = MigrationPhase.CopyingData };
        var groups = ReportAttention.Build(later, migration.Options);
        var empty = Assert.Single(groups, g => g.Kind == AttentionKind.LeftEmpty);
        Assert.Equal("public.c", Assert.Single(empty.Items).Title);
        Assert.Equal(AttentionKind.LeftEmpty, groups[0].Kind);
        Assert.True(empty.IsSevere);

        // The emptied table is not listed again under "Not started".
        var notStarted = Assert.Single(groups, g => g.Kind == AttentionKind.NotStarted);
        Assert.Equal(3, notStarted.Count);
    }

    [Fact]
    public void Remaining_sql_separates_failed_steps_from_steps_not_run()
    {
        var migration = Plans.Prepare();
        var after = migration.Script.AfterData;
        var report = Report(migration, TableStatus.Copied) with
        {
            Steps = [new StepResult(after[0], TimeSpan.FromSeconds(1), "duplicate key"), new StepResult(after[1], TimeSpan.FromSeconds(1))],
            StepsNotRun = after.Skip(2).ToList(),
            CancelledDuring = MigrationPhase.CreatingConstraints,
        };

        var sections = ReportAttention.RemainingSql(report, migration.Script);

        Assert.Equal(["Failed: fix the cause, then run these", "Not run"], sections.Select(s => s.Heading));
        Assert.Equal([after[0]], sections[0].Steps);
        Assert.Equal(after.Skip(2), sections[1].Steps);
        var groups = ReportAttention.Build(report, migration.Options);
        Assert.Equal(after[0], Assert.Single(groups, g => g.Kind == AttentionKind.FailedSteps).Items[0].Step);
        Assert.Contains(groups, g => g.Kind == AttentionKind.StepsNotRun && g.OffersRemainingSql);
    }

    [Fact]
    public void A_cancel_while_creating_tables_leaves_the_whole_script_to_run()
    {
        var migration = Plans.Prepare();
        var report = Report(migration, TableStatus.Cancelled) with { Steps = [], StepsNotRun = migration.Script.AfterData, CancelledDuring = MigrationPhase.CreatingTables };

        var sections = ReportAttention.RemainingSql(report, migration.Script);

        Assert.Equal(migration.Script.BeforeData, sections[1].Steps);
        Assert.Equal(migration.Script.AfterData, sections[2].Steps);
    }

    internal static MigrationReport Report(PreparedMigration migration, TableStatus status) => new()
    {
        Plan = migration.Plan,
        Tables = migration.Plan.Tables.Select(t => new TableResult
        {
            Table = t,
            Status = status,
            RowsCopied = status is TableStatus.Copied or TableStatus.CountMismatch ? 5 : 0,
            SourceRowCount = status is TableStatus.Copied or TableStatus.CountMismatch ? 5 : null,
            TargetRowCount = status == TableStatus.CountMismatch ? 4 : status == TableStatus.Copied ? 5 : null,
            Error = status == TableStatus.Failed ? "disk full" : null,
        }).ToList(),
        Steps = migration.Script.AfterData.Select(s => new StepResult(s, TimeSpan.FromSeconds(1))).ToList(),
        Issues = migration.Plan.Issues,
        Elapsed = TimeSpan.FromSeconds(3),
    };
}
