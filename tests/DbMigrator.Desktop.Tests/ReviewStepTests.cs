using DbMigrator.Core.Planning;
using DbMigrator.Core.Schema;
using DbMigrator.Desktop.Testing;
using DbMigrator.Desktop.ViewModels;
using static DbMigrator.TestSupport.SchemaBuilder;

namespace DbMigrator.Desktop.Tests;

public sealed class ReviewStepTests
{
    [Fact]
    public Task An_existing_table_blocks_the_run_until_decided() => UiThreadContext.RunAsync(async () =>
    {
        var test = TestServices.Create();
        test.TargetDb.ExistingTables.Add(Plans.TargetA);
        var wizard = new WizardDriver(test);
        await wizard.ToReviewAsync();
        var review = wizard.Shell.Review;

        var conflict = Assert.Single(review.Conflicts);
        Assert.True(conflict.IsUndecided);
        Assert.True(review.HasErrors);
        Assert.False(wizard.Shell.NextCommand.CanExecute(null));
        Assert.Equal("1 table already exists in the target: choose what to do with it.", review.BlockingReason);
        Assert.Equal(ReviewStepViewModel.DecisionsTab, review.SelectedTab);

        conflict.Choice = ConflictChoice.For(TableExistsAction.Truncate);
        await ReplannedAsync(test, review);

        Assert.False(review.HasErrors);
        Assert.Equal(TableAction.Truncate, review.Prepared!.Plan.Tables.First(t => t.Source.Name == TestServices.A).Action);
        Assert.True(wizard.Shell.NextCommand.CanExecute(null));

        // Re-planning used the schema and target state read when connecting.
        Assert.Equal(1, test.SourceDb.ReadSchemaCount);
        Assert.Equal(1, test.TargetDb.InspectCount);
    });

    [Fact]
    public Task Apply_to_undecided_decides_every_open_table_at_once() => UiThreadContext.RunAsync(async () =>
    {
        var test = TestServices.Create();
        test.TargetDb.ExistingTables.Add(Plans.TargetA);
        test.TargetDb.ExistingTables.Add(Plans.TargetB);
        var wizard = new WizardDriver(test);
        await wizard.ToReviewAsync();
        var review = wizard.Shell.Review;
        Assert.Equal(2, review.UndecidedCount);

        review.BulkChoice = ConflictChoice.For(TableExistsAction.Skip);
        review.ApplyToUndecidedCommand.Execute(null);
        await ReplannedAsync(test, review);

        Assert.Equal(0, review.UndecidedCount);
        Assert.Equal(2, review.Prepared!.Plan.Tables.Count(t => t.Action == TableAction.Skip));
        Assert.Equal(1, test.SourceDb.ReadSchemaCount);
        Assert.Equal(1, test.TargetDb.InspectCount);
    });

    [Fact]
    public Task The_script_preview_is_capped_and_saving_writes_the_full_script() => UiThreadContext.RunAsync(async () =>
    {
        // One table wide enough that its CREATE TABLE is far over 64 KB.
        var wide = Table("dbo", "Wide", [Id(), .. Enumerable.Range(0, 3000).Select(i => Column($"A_rather_long_column_name_{i:D4}", DataTypeKind.Text))]);
        var test = TestServices.Create(Source(wide));
        var wizard = new WizardDriver(test);
        await wizard.ToReviewAsync();
        var script = wizard.Shell.Review.Script;

        script.SelectedRow = script.Rows.First(r => r.IsStep && r.Sql.Contains("CREATE TABLE", StringComparison.Ordinal));

        Assert.True(script.SelectedRow.Sql.Length > ScriptViewModel.MaxPreviewChars);
        Assert.True(script.PreviewText.Length <= ScriptViewModel.MaxPreviewChars);
        Assert.True(script.IsPreviewTruncated);

        await script.SaveCommand.ExecuteAsync(null);
        var prepared = wizard.Shell.Review.Prepared!;
        Assert.Equal(prepared.Script.ToText(prepared.Plan), Assert.Single(test.Files.Saved).Text);
    });

    [Fact]
    public Task Type_changes_and_issues_are_listed() => UiThreadContext.RunAsync(async () =>
    {
        var wizard = new WizardDriver(TestServices.Create());
        await wizard.ToReviewAsync();
        var review = wizard.Shell.Review;

        Assert.Equal(4, review.PlanTables.Count);
        Assert.NotEmpty(review.TypeMappings);
        Assert.Equal(review.Prepared!.Plan.Issues.Count, review.Issues.Groups.Sum(g => g.Count));
        Assert.NotNull(review.Detail);
    });

    /// <summary>Lets the debounced re-plan run: past its quiet period, then until it is applied.</summary>
    internal static async Task ReplannedAsync(TestServices test, ReviewStepViewModel review)
    {
        await Task.Delay(20);
        test.Time.Advance(TimeSpan.FromSeconds(1));
        await Wait.UntilAsync(() => !review.IsPlanning, "the re-plan");
    }
}
