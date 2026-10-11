using Avalonia.Controls;
using Avalonia.VisualTree;
using DbMigrator.Core.Planning;
using DbMigrator.Desktop.Testing;
using DbMigrator.Desktop.ViewModels;
using DbMigrator.TestSupport;

namespace DbMigrator.Desktop.UiTests;

/// <summary>Every step renders over fakes without binding errors, and leaves a screenshot.</summary>
public sealed class StepRenderTests(AvaloniaFixture fixture)
{
    [Fact]
    public Task Every_step_renders_without_binding_errors() => fixture.RunAsync(async () =>
    {
        fixture.Bindings.Clear();
        var test = TestServices.Create();
        test.TargetDb.ExistingTables.Add(new Core.Schema.QualifiedName("public", "c"));
        test.TargetDb.ExistingTables.Add(new Core.Schema.QualifiedName("public", "d"));
        var gate = new Gate();
        test.SourceDb.Pauses[TestServices.B] = (2, gate);
        var ui = await UiDriver.OpenAsync(test);
        ui.Screenshot("01-source");

        await ui.Wizard.ConnectSourceAsync();
        ui.Screenshot("02-source-connected");
        await ui.NextAsync();
        await ui.Wizard.ConnectTargetAsync();
        await UiDriver.UntilAsync(() => ui.Shell.Target.MatchingTableCount is not null, "the matching count");
        ui.Screenshot("03-target-connected");
        await ui.NextAsync();
        ui.Screenshot("04-tables");
        await ui.NextAsync();
        Assert.Same(ui.Shell.Review, ui.Shell.Current);
        ui.Screenshot("05-review-decisions");

        ui.Shell.Review.Conflicts[0].Choice = ConflictChoice.For(TableExistsAction.Truncate);
        ui.Shell.Review.Conflicts[1].Choice = ConflictChoice.For(TableExistsAction.DropAndRecreate);
        await Task.Delay(20);
        test.Time.Advance(TimeSpan.FromSeconds(1));
        await UiDriver.UntilAsync(() => !ui.Shell.Review.IsPlanning && ui.Shell.Review.UndecidedCount == 0, "the re-plan");
        ui.Shell.Review.SelectedTab = ReviewStepViewModel.TablesTab;
        ui.Screenshot("06-review-tables");
        ui.Shell.Review.SelectedTab = ReviewStepViewModel.ScriptTab;
        ui.Screenshot("07-review-script");

        await ui.NextAsync();
        Assert.Same(ui.Shell.Run, ui.Shell.Current);
        ui.Shell.Run.Acknowledged = true;
        ui.Screenshot("08-run-ready");

        ui.Click(ui.Find<Button>("StartButton"));
        await UiDriver.UntilAsync(() => gate.Reached.IsCompleted, "the copy to reach dbo.B");
        test.Scheduler.Tick();
        ui.Screenshot("09-run-running");

        ui.Click(ui.Find<Button>("StopButton"));
        await UiDriver.UntilAsync(() => ui.Shell.Dialogs.IsOpen, "the stop question");
        ui.Screenshot("10-run-stop-question");
        ui.Click(ui.Find<Button>("DialogConfirm"));
        await UiDriver.UntilAsync(() => ui.Shell.Current == ui.Shell.Report, "the report");
        ui.Screenshot("11-report-cancelled");

        Assert.True(fixture.Bindings.Errors.Count == 0, string.Join(Environment.NewLine, fixture.Bindings.Errors.Distinct()));
        ui.Window.Close();
    });

    [Fact]
    public Task A_successful_report_renders() => fixture.RunAsync(async () =>
    {
        fixture.Bindings.Clear();
        var ui = await UiDriver.OpenAsync();
        await ui.Wizard.ToRunAsync();
        ui.Click(ui.Find<Button>("StartButton"));
        await UiDriver.UntilAsync(() => ui.Shell.Current == ui.Shell.Report, "the report");

        Assert.Equal("Migration succeeded", ui.Find<TextBlock>("Headline").Text);
        ui.Screenshot("12-report-succeeded");
        Assert.True(fixture.Bindings.Errors.Count == 0, string.Join(Environment.NewLine, fixture.Bindings.Errors.Distinct()));
        ui.Window.Close();
    });

    [Fact]
    public Task Script_only_mode_renders() => fixture.RunAsync(async () =>
    {
        fixture.Bindings.Clear();
        var ui = await UiDriver.OpenAsync();
        await ui.Wizard.ConnectSourceAsync();
        await ui.NextAsync();
        ui.Click(ui.Find<RadioButton>("ScriptOnly"));
        ui.Screenshot("13-target-script-only");
        await ui.NextAsync();
        await ui.NextAsync();

        Assert.Equal("Save script...", ui.Find<Button>("NextButton").Content);
        var rail = ui.Find<ItemsControl>("StepRail").GetVisualDescendants().OfType<Button>().ToList();
        Assert.Equal(["Go to Source", "Go to Target", "Go to Tables & options", "Go to Review"],
            rail.Where(b => b.IsEffectivelyVisible).Select(b => Avalonia.Automation.AutomationProperties.GetName(b)));
        ui.Screenshot("14-review-script-only");
        Assert.True(fixture.Bindings.Errors.Count == 0, string.Join(Environment.NewLine, fixture.Bindings.Errors.Distinct()));
        ui.Window.Close();
    });
}
