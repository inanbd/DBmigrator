using DbMigrator.Core.Execution;
using DbMigrator.Core.Planning;
using DbMigrator.Desktop.Testing;
using DbMigrator.Desktop.ViewModels;
using DbMigrator.TestSupport;

namespace DbMigrator.Desktop.Tests;

public sealed class RunStepTests
{
    [Fact]
    public Task Dropping_or_emptying_tables_needs_an_explicit_tick() => UiThreadContext.RunAsync(async () =>
    {
        var test = TestServices.Create();
        test.TargetDb.ExistingTables.Add(Plans.TargetA);
        test.TargetDb.ExistingTables.Add(Plans.TargetB);
        var wizard = new WizardDriver(test);
        await wizard.ToReviewAsync();
        wizard.Shell.Review.Conflicts[0].Choice = ConflictChoice.For(TableExistsAction.DropAndRecreate);
        wizard.Shell.Review.Conflicts[1].Choice = ConflictChoice.For(TableExistsAction.Truncate);
        await ReviewStepTests.ReplannedAsync(test, wizard.Shell.Review);
        await wizard.NextAsync();
        var run = wizard.Shell.Run;

        Assert.True(run.RequiresAcknowledgement);
        Assert.Equal("I understand that 1 table will be dropped and 1 table emptied in shop on pg01 as migrator.", run.AcknowledgementText);
        Assert.Contains("Drop and recreate 1: public.a.", run.ChangeSummary);
        Assert.Contains("Empty and reload 1: public.b.", run.ChangeSummary);
        Assert.False(run.StartCommand.CanExecute(null));

        run.Acknowledged = true;

        Assert.True(run.StartCommand.CanExecute(null));
    });

    [Fact]
    public Task A_target_that_changed_since_review_sends_the_user_back() => UiThreadContext.RunAsync(async () =>
    {
        var test = TestServices.Create();
        var wizard = new WizardDriver(test);
        await wizard.ToRunAsync();

        // Someone creates public.c after the review.
        test.TargetDb.ExistingTables.Add(Plans.TargetC);
        test.Dialogs.Confirm(true);
        await wizard.Shell.Run.StartCommand.ExecuteAsync(null);

        Assert.Equal("The target changed since you reviewed the plan", test.Dialogs.LastConfirm!.Title);
        Assert.Same(wizard.Shell.Review, wizard.Shell.Current);
        Assert.True(wizard.Shell.Review.HasPreflightDifferences);
        Assert.Equal(TestServices.C, Assert.Single(wizard.Shell.Review.Conflicts).Source);
        Assert.Empty(test.TargetDb.Statements);
        Assert.False(wizard.Shell.IsLocked);
    });

    [Fact]
    public Task Progress_reaches_the_screen_only_on_the_timer() => UiThreadContext.RunAsync(async () =>
    {
        var test = TestServices.Create();
        var gate = new Gate();
        test.SourceDb.Pauses[TestServices.C] = (2, gate);
        var wizard = new WizardDriver(test);
        await wizard.ToRunAsync();
        var run = wizard.Shell.Run;

        var running = run.StartCommand.ExecuteAsync(null);
        await Wait.ForAsync(gate.Reached, "the copy to reach dbo.C");

        Assert.True(wizard.Shell.IsLocked);
        Assert.Equal(RunState.Running, run.Stage);
        Assert.Equal(1, test.Scheduler.ActiveTimers);
        Assert.All(run.Tables, t => Assert.Equal(TableRunStatus.Waiting, t.Status));

        test.Scheduler.Tick();

        Assert.Equal(TableRunStatus.Copied, run.Tables[0].Status);
        Assert.Equal(TableRunStatus.Copied, run.Tables[1].Status);
        Assert.Equal(TableRunStatus.Copying, run.Tables[2].Status);
        Assert.Equal(TableRunStatus.Waiting, run.Tables[3].Status);
        Assert.Equal("Copying dbo.C → public.c", run.CurrentActivity);
        Assert.Same(run.Tables[2], run.CurrentRow);
        Assert.Equal(PhaseState.Done, run.Phases[0].State);
        Assert.Equal(PhaseState.Running, run.Phases[1].State);

        gate.Release();
        await Wait.ForAsync(running, "the run to finish");

        // The last events arrived after the last tick; the final drain applied them.
        Assert.Equal(0, test.Scheduler.ActiveTimers);
        Assert.All(run.Tables, t => Assert.Equal(TableRunStatus.Verified, t.Status));
        Assert.False(wizard.Shell.IsLocked);
        Assert.Same(wizard.Shell.Report, wizard.Shell.Current);
    });

    [Fact]
    public Task Stopping_mid_table_rolls_it_back_and_reports_what_stays() => UiThreadContext.RunAsync(async () =>
    {
        var test = TestServices.Create();
        test.TargetDb.ExistingTables.Add(Plans.TargetC);
        test.TargetDb.ExistingTables.Add(Plans.TargetD);
        var gate = new Gate();
        test.SourceDb.Pauses[TestServices.B] = (2, gate);
        var wizard = new WizardDriver(test);
        await wizard.ToReviewAsync();
        wizard.Shell.Review.Conflicts[0].Choice = ConflictChoice.For(TableExistsAction.Truncate);
        wizard.Shell.Review.Conflicts[1].Choice = ConflictChoice.For(TableExistsAction.DropAndRecreate);
        await ReviewStepTests.ReplannedAsync(test, wizard.Shell.Review);
        await wizard.NextAsync();
        var run = wizard.Shell.Run;
        run.Acknowledged = true;

        var running = run.StartCommand.ExecuteAsync(null);
        await Wait.ForAsync(gate.Reached, "the copy to reach dbo.B");
        test.Scheduler.Tick();

        // Keep running first: the question shows exactly what stopping would leave.
        test.Dialogs.Confirm(false);
        await run.StopCommand.ExecuteAsync(null);
        var question = test.Dialogs.LastConfirm!;
        Assert.Equal("Stop the migration?", question.Title);
        Assert.True(question.IsDestructive);
        Assert.Equal(
            [
                "Tables already copied stay: 1 table, 5 rows.",
                "The table being copied, dbo.B, is rolled back; none of its rows are kept.",
                "2 tables are not copied yet. 2 of them were emptied or dropped and recreated at the start of the run (public.c, public.d) and will stay EMPTY until you run again.",
                $"Keys, indexes and foreign keys are not built yet: {run.StepsTotal} steps. Their SQL will be in the report.",
            ],
            question.Bullets);
        Assert.Equal(RunState.Running, run.Stage);

        test.Dialogs.Confirm(true);
        await run.StopCommand.ExecuteAsync(null);
        await Wait.ForAsync(running, "the run to stop");

        var report = wizard.Shell.Session.Report!;
        Assert.Equal(MigrationPhase.CopyingData, report.CancelledDuring);
        Assert.Equal(TestServices.B, report.InterruptedTable!.Source.Name);
        Assert.Equal(TableStatus.Copied, report.Tables[0].Status);
        Assert.Equal(TableStatus.Cancelled, report.Tables[1].Status);
        Assert.Empty(test.TargetDb.RowsOf(Plans.TargetB));
        Assert.Equal(TableRunStatus.RolledBack, run.Tables[1].Status);
        Assert.Equal(TableRunStatus.NotStarted, run.Tables[2].Status);
        Assert.Same(wizard.Shell.Report, wizard.Shell.Current);
        Assert.Equal(ReportOutcome.Cancelled, wizard.Shell.Report.Outcome);
        var empty = Assert.Single(wizard.Shell.Report.Attention, g => g.Kind == AttentionKind.LeftEmpty);
        Assert.Equal(["public.c", "public.d"], empty.Items.Select(i => i.Title));
    });
}
