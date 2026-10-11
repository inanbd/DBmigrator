using DbMigrator.Core.Execution;
using DbMigrator.Core.Planning;
using DbMigrator.Desktop.Testing;
using DbMigrator.Desktop.ViewModels;

namespace DbMigrator.Desktop.Tests;

public sealed class ShellTests
{
    [Fact]
    public Task The_whole_wizard_keeps_database_and_file_work_off_the_ui_thread() => UiThreadContext.RunAsync(async () =>
    {
        var test = TestServices.Create();
        test.ForbidUiThread();
        var wizard = new WizardDriver(test);

        await wizard.StartAsync();
        await wizard.ConnectSourceAsync();
        wizard.Shell.Source.RememberPassword = true;
        test.Dialogs.Prompt("shop source");
        await wizard.Shell.Source.SaveProfileCommand.ExecuteAsync(null);
        await wizard.NextAsync();
        await wizard.ConnectTargetAsync();
        await wizard.NextAsync();
        await wizard.NextAsync();
        await wizard.NextAsync();
        await wizard.Shell.Run.StartCommand.ExecuteAsync(null);

        Assert.Same(wizard.Shell.Report, wizard.Shell.Current);
        Assert.Equal(ReportOutcome.Succeeded, wizard.Shell.Report.Outcome);
        Assert.Single(test.Profiles.Stored);
        Assert.Single(test.Reports.Saved);
        Assert.Empty(test.ThreadViolations);
    });

    [Fact]
    public Task The_thread_guard_catches_engine_calls_made_on_the_ui_thread() => UiThreadContext.RunAsync(async () =>
    {
        var test = TestServices.Create();
        test.ForbidUiThread();

        await MigrationEngine.PrepareAsync(test.SourceDb, test.TargetDb, new MigrationOptions(), CancellationToken.None);

        Assert.NotEmpty(test.ThreadViolations);
    });

    [Fact]
    public Task Next_is_allowed_only_once_connected_to_what_is_on_screen() => UiThreadContext.RunAsync(async () =>
    {
        var wizard = new WizardDriver(TestServices.Create());
        await wizard.StartAsync();
        Assert.False(wizard.Shell.NextCommand.CanExecute(null));
        Assert.Equal("Connect to the source to continue.", wizard.Shell.Source.BlockingReason);

        await wizard.ConnectSourceAsync();
        Assert.True(wizard.Shell.NextCommand.CanExecute(null));

        // Editing the form after connecting means the connection no longer matches it.
        WizardDriver.Field(wizard.Shell.Source.Editor!, "Initial Catalog").Value = "other";
        Assert.False(wizard.Shell.Source.CanAdvance);
        WizardDriver.Field(wizard.Shell.Source.Editor!, "Initial Catalog").Value = "shop";
        Assert.True(wizard.Shell.Source.CanAdvance);
    });

    [Fact]
    public Task Back_keeps_what_was_entered_and_is_refused_while_locked() => UiThreadContext.RunAsync(async () =>
    {
        var wizard = new WizardDriver(TestServices.Create());
        await wizard.StartAsync();
        await wizard.ConnectSourceAsync();
        await wizard.NextAsync();
        Assert.Same(wizard.Shell.Target, wizard.Shell.Current);

        wizard.Shell.SetLocked(true);
        Assert.False(wizard.Shell.BackCommand.CanExecute(null));
        wizard.Shell.SetLocked(false);

        await wizard.Shell.BackCommand.ExecuteAsync(null);
        Assert.Same(wizard.Shell.Source, wizard.Shell.Current);
        Assert.True(wizard.Shell.Source.IsConnected);
        Assert.Equal("shop", WizardDriver.Field(wizard.Shell.Source.Editor!, "Initial Catalog").Value);
        Assert.Equal(1, wizard.Test.SourceDb.ReadSchemaCount);
    });

    [Fact]
    public Task The_rail_reaches_only_steps_whose_predecessors_are_done() => UiThreadContext.RunAsync(async () =>
    {
        var wizard = new WizardDriver(TestServices.Create());
        await wizard.StartAsync();
        Assert.Equal([StepState.Current, StepState.Locked, StepState.Locked, StepState.Locked, StepState.Locked, StepState.Locked], wizard.Shell.Steps.Select(s => s.State));
        Assert.False(wizard.Shell.GoToStepCommand.CanExecute(wizard.Shell.Tables));

        await wizard.ConnectSourceAsync();
        Assert.Equal(StepState.Available, wizard.Shell.Target.State);
        Assert.Equal(StepState.Locked, wizard.Shell.Tables.State);

        await wizard.NextAsync();
        await wizard.ConnectTargetAsync();
        await wizard.Shell.GoToStepCommand.ExecuteAsync(wizard.Shell.Source);
        Assert.Same(wizard.Shell.Source, wizard.Shell.Current);
        Assert.Equal(StepState.Available, wizard.Shell.Tables.State);

        // Forward through the rail passes each step in turn, as Next would.
        await wizard.Shell.GoToStepCommand.ExecuteAsync(wizard.Shell.Tables);
        Assert.Same(wizard.Shell.Tables, wizard.Shell.Current);
    });

    [Fact]
    public Task A_new_target_keeps_the_selection_and_plans_again() => UiThreadContext.RunAsync(async () =>
    {
        var wizard = new WizardDriver(TestServices.Create());
        await wizard.ToReviewAsync();
        await wizard.Shell.GoToStepCommand.ExecuteAsync(wizard.Shell.Tables);
        wizard.Shell.Tables.Rows.First(r => r.Name == TestServices.A).IsSelected = false;
        await wizard.Shell.GoToStepCommand.ExecuteAsync(wizard.Shell.Target);

        WizardDriver.Field(wizard.Shell.Target.Editor!, "Initial Catalog").Value = "shop2";
        await wizard.Shell.Target.ConnectCommand.ExecuteAsync(null);
        await wizard.NextAsync();
        Assert.False(wizard.Shell.Tables.Rows.First(r => r.Name == TestServices.A).IsSelected);
        await wizard.NextAsync();

        Assert.Same(wizard.Shell.Review, wizard.Shell.Current);
        Assert.Equal(3, wizard.Shell.Review.Prepared!.Plan.Tables.Count);
        Assert.Equal(1, wizard.Test.SourceDb.ReadSchemaCount);
    });

    [Fact]
    public Task Script_only_ends_at_review_with_a_saved_script() => UiThreadContext.RunAsync(async () =>
    {
        var test = TestServices.Create();
        var wizard = new WizardDriver(test);
        await wizard.StartAsync();
        await wizard.ConnectSourceAsync();
        await wizard.NextAsync();
        wizard.Shell.Target.ScriptOnly = true;
        await wizard.NextAsync();
        await wizard.NextAsync();

        Assert.Same(wizard.Shell.Review, wizard.Shell.Current);
        Assert.False(wizard.Shell.Run.IsApplicable);
        Assert.False(wizard.Shell.Report.IsApplicable);
        Assert.Equal("Save script...", wizard.Shell.Review.NextLabel);
        Assert.Equal([1, 2, 3, 4], wizard.Shell.Steps.Where(s => s.IsApplicable).Select(s => s.Number));

        await wizard.NextAsync();

        Assert.Same(wizard.Shell.Review, wizard.Shell.Current);
        var saved = Assert.Single(test.Files.Saved);
        var prepared = wizard.Shell.Review.Prepared!;
        Assert.Equal(prepared.Script.ToText(prepared.Plan), saved.Text);
        Assert.Equal(0, test.Provider.OpenTargetCount);
        Assert.Empty(test.TargetDb.Statements);
    });

    [Fact]
    public Task Closing_during_a_run_asks_and_stops_it() => UiThreadContext.RunAsync(async () =>
    {
        var test = TestServices.Create();
        var gate = new TestSupport.Gate();
        test.SourceDb.Pauses[TestServices.B] = (2, gate);
        var wizard = new WizardDriver(test);
        await wizard.ToRunAsync();
        var run = wizard.Shell.Run.StartCommand.ExecuteAsync(null);
        await Wait.ForAsync(gate.Reached, "the copy to reach dbo.B");

        test.Dialogs.Confirm(false);
        Assert.False(await wizard.Shell.ConfirmCloseAsync());
        Assert.True(wizard.Shell.IsRunning);
        Assert.Equal("Stop the migration and close?", test.Dialogs.LastConfirm!.Title);

        test.Dialogs.Confirm(true);
        Assert.True(await wizard.Shell.ConfirmCloseAsync());
        await Wait.ForAsync(run, "the run to stop");

        Assert.False(wizard.Shell.IsRunning);
        Assert.True(wizard.Shell.Session.Report!.Cancelled);
        Assert.Single(test.Reports.Saved);
    });
}
