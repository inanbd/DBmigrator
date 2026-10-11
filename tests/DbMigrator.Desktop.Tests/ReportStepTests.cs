using System.Text.Json;
using DbMigrator.Core.Planning;
using DbMigrator.Desktop.Testing;
using DbMigrator.Desktop.ViewModels;

namespace DbMigrator.Desktop.Tests;

public sealed class ReportStepTests
{
    [Fact]
    public Task A_successful_run_shows_its_numbers_and_is_saved_automatically() => UiThreadContext.RunAsync(async () =>
    {
        var test = TestServices.Create();
        var wizard = await RunAsync(test);
        var report = wizard.Shell.Report;

        Assert.Equal(ReportOutcome.Succeeded, report.Outcome);
        Assert.Equal("Migration succeeded", report.Headline);
        Assert.Equal(["Tables copied", "Rows copied", "Duration", "Average rate", "Keys and indexes built"], report.Kpis.Select(k => k.Label));
        Assert.Equal("4 of 4", report.Kpis[0].Value);
        Assert.Equal("20", report.Kpis[1].Value);
        Assert.False(report.HasAttention);
        Assert.Equal(4, report.Tables.Count);
        Assert.False(report.HasRemainingSql);

        var saved = Assert.Single(test.Reports.Saved);
        Assert.Equal("dbmigrator-20261010-080000-succeeded", saved.BaseName);
        Assert.Equal("Saved automatically to /reports/dbmigrator-20261010-080000-succeeded.md", report.AutosaveText);
        Assert.False(report.AutosaveFailed);
    });

    [Fact]
    public Task A_failed_autosave_says_why() => UiThreadContext.RunAsync(async () =>
    {
        var test = TestServices.Create();
        test.Reports.Failure = "The disk is full.";
        var wizard = await RunAsync(test);

        Assert.True(wizard.Shell.Report.AutosaveFailed);
        Assert.Contains("The disk is full.", wizard.Shell.Report.AutosaveText, StringComparison.Ordinal);
    });

    [Fact]
    public Task Failed_steps_are_listed_with_their_sql_still_to_run() => UiThreadContext.RunAsync(async () =>
    {
        var test = TestServices.Create();
        test.TargetDb.FailingStatements["fk_d_c"] = "violates foreign key";
        var wizard = await RunAsync(test);
        var report = wizard.Shell.Report;

        Assert.Equal(ReportOutcome.CompletedWithProblems, report.Outcome);
        Assert.Equal("Finished with problems", report.Headline);
        var failed = Assert.Single(report.Attention, g => g.Kind == AttentionKind.FailedSteps);
        var item = Assert.Single(failed.Items);
        Assert.Contains("violates foreign key", item.Detail, StringComparison.Ordinal);
        Assert.True(report.HasRemainingSql);

        report.ShowSqlCommand.Execute(item);

        Assert.Equal(ReportStepViewModel.SqlTab, report.SelectedTab);
        Assert.Equal(item.Step!.Sql, report.RemainingSql.SelectedRow!.Sql);
        await report.SaveRemainingSqlCommand.ExecuteAsync(null);
        Assert.Contains(item.Step.Sql, Assert.Single(test.Files.Saved).Text, StringComparison.Ordinal);
    });

    [Fact]
    public Task Run_again_inspects_the_target_and_asks_about_existing_tables_again() => UiThreadContext.RunAsync(async () =>
    {
        var test = TestServices.Create();
        test.TargetDb.ExistingTables.Add(Plans.TargetA);
        var wizard = new WizardDriver(test);
        await wizard.ToReviewAsync();
        wizard.Shell.Review.Conflicts[0].Choice = ConflictChoice.For(TableExistsAction.Truncate);
        await ReviewStepTests.ReplannedAsync(test, wizard.Shell.Review);
        await wizard.NextAsync();
        wizard.Shell.Run.Acknowledged = true;
        await wizard.Shell.Run.StartCommand.ExecuteAsync(null);
        var inspections = test.TargetDb.InspectCount;

        await wizard.Shell.Report.RunAgainCommand.ExecuteAsync(null);

        Assert.Same(wizard.Shell.Review, wizard.Shell.Current);
        Assert.Equal(inspections + 1, test.TargetDb.InspectCount);
        Assert.Empty(wizard.Shell.Session.ConflictChoices);
        Assert.Null(wizard.Shell.Session.Report);
        Assert.Equal(4, wizard.Shell.Review.UndecidedCount);
        Assert.Equal(RunState.Ready, wizard.Shell.Run.Stage);
        Assert.Equal(1, test.SourceDb.ReadSchemaCount);
    });

    [Fact]
    public Task New_migration_keeps_the_connections_and_starts_from_the_tables() => UiThreadContext.RunAsync(async () =>
    {
        var test = TestServices.Create();
        var wizard = await RunAsync(test);

        await wizard.Shell.Report.NewMigrationCommand.ExecuteAsync(null);

        Assert.Same(wizard.Shell.Tables, wizard.Shell.Current);
        Assert.True(wizard.Shell.Source.IsConnected);
        Assert.True(wizard.Shell.Target.IsConnected);
        Assert.All(wizard.Shell.Tables.Rows, r => Assert.True(r.IsSelected));
        Assert.Equal(1, test.SourceDb.ReadSchemaCount);
        Assert.Equal("", wizard.Shell.Report.Headline);
    });

    [Fact]
    public Task Exports_never_hold_a_password_or_a_connection_string() => UiThreadContext.RunAsync(async () =>
    {
        var test = TestServices.Create();
        var wizard = await RunAsync(test);
        var archived = Assert.Single(test.Reports.Saved);

        test.Files.ChooseExtension = ".json";
        await wizard.Shell.Report.SaveReportCommand.ExecuteAsync(null);
        test.Files.ChooseExtension = ".md";
        await wizard.Shell.Report.SaveReportCommand.ExecuteAsync(null);
        await wizard.Shell.Report.CopySummaryCommand.ExecuteAsync(null);

        string[] texts = [archived.Markdown, archived.Json, .. test.Files.Saved.Select(f => f.Text), test.Clipboard.Text!];
        foreach (var text in texts)
        {
            Assert.DoesNotContain(WizardDriver.Password, text, StringComparison.Ordinal);
            Assert.DoesNotContain("Password=", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Data Source=", text, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("shop on src01 as migrator", archived.Markdown, StringComparison.Ordinal);
        var document = ReportExporter.FromJson(archived.Json)!;
        Assert.Equal(ReportOutcome.Succeeded, document.Outcome);
        Assert.Equal(4, document.Tables.Count);
        Assert.Equal("succeeded", JsonDocument.Parse(archived.Json).RootElement.GetProperty("steps")[0].GetProperty("status").GetString());
        Assert.Equal(["/saved/dbmigrator-20261010-080000-succeeded.json", "/saved/dbmigrator-20261010-080000-succeeded.md"], test.Files.Saved.Select(f => "/saved/" + f.Name));
    });

    private static async Task<WizardDriver> RunAsync(TestServices test)
    {
        var wizard = new WizardDriver(test);
        await wizard.ToRunAsync();
        await wizard.Shell.Run.StartCommand.ExecuteAsync(null);
        Assert.Same(wizard.Shell.Report, wizard.Shell.Current);
        return wizard;
    }
}
