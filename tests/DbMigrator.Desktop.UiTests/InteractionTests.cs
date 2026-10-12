using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DbMigrator.Core.Execution;
using DbMigrator.Core.Planning;
using DbMigrator.Core.Schema;
using DbMigrator.Desktop.Testing;
using DbMigrator.Desktop.ViewModels;
using DbMigrator.Providers.SqlServer;
using DbMigrator.TestSupport;
using static DbMigrator.TestSupport.SchemaBuilder;

namespace DbMigrator.Desktop.UiTests;

public sealed class ConnectionFormTests(AvaloniaFixture fixture)
{
    private static async Task<UiDriver> OpenSqlServerFormAsync(TestServices test)
    {
        var ui = await UiDriver.OpenAsync(test);
        ui.Shell.Source.SelectedProvider = ui.Shell.Source.Providers.First(p => p.Provider.Id == "sqlserver");
        UiDriver.Settle();
        return ui;
    }

    [Fact]
    public Task Windows_authentication_hides_the_login_and_the_password_is_masked() => fixture.RunAsync(async () =>
    {
        var ui = await OpenSqlServerFormAsync(TestServices.Create(null, new SqlServerProvider()));

        Assert.IsType<ComboBox>(ui.ByLabel<ComboBox>("Encrypt"));
        var password = ui.ByLabel<TextBox>("Password");
        Assert.Equal('●', password.PasswordChar);
        ui.Type(password, "hunter2");
        Assert.False(password.RevealPassword);
        ui.Click(ui.ByLabel<ToggleButton>("Show Password"));
        Assert.True(password.RevealPassword);

        ui.Click(ui.ByLabel<CheckBox>("Integrated Security"));

        Assert.False(password.IsEffectivelyVisible);
        Assert.DoesNotContain(ui.Window.GetVisualDescendants().OfType<TextBox>(), t => AutomationProperties.GetName(t) == "User ID" && t.IsEffectivelyVisible);
        Assert.False(ui.Find<CheckBox>("RememberPassword").IsEffectivelyEnabled);
        Assert.Equal("This connection has no password to remember.", ui.Find<TextBlock>("RememberPasswordNote").Text);
        ui.Window.Close();
    });

    [Fact]
    public Task The_connection_string_view_never_shows_the_password() => fixture.RunAsync(async () =>
    {
        var ui = await OpenSqlServerFormAsync(TestServices.Create(null, new SqlServerProvider()));
        ui.Type(ui.ByLabel<TextBox>("Data Source"), "sql01");
        ui.Type(ui.ByLabel<TextBox>("User ID"), "sa");
        ui.Type(ui.ByLabel<TextBox>("Password"), "hunter2");

        ui.Click(ui.Find<ToggleSwitch>("RawMode"));

        var raw = ui.Find<TextBox>("RawText");
        Assert.Contains("sql01", raw.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", raw.Text, StringComparison.Ordinal);
        ui.Screenshot("20-connection-string-mode");
        ui.Window.Close();
    });

    [Fact]
    public Task The_password_under_the_connection_string_takes_one_key_after_another() => fixture.RunAsync(async () =>
    {
        var ui = await OpenSqlServerFormAsync(TestServices.Create(null, new SqlServerProvider()));
        ui.Click(ui.Find<ToggleSwitch>("RawMode"));
        var password = ui.ByLabel<TextBox>("Password");
        password.Focus();
        UiDriver.Settle();

        // Key by key, as a person types: each key re-validates the form.
        foreach (var key in "hunter2")
        {
            ui.Window.KeyTextInput(key.ToString());
            UiDriver.Settle();
        }

        Assert.Equal("hunter2", WizardDriver.Field(ui.Shell.Source.Editor!, "Password").Value);
        Assert.Same(password, ui.ByLabel<TextBox>("Password"));
        Assert.True(password.IsFocused);
        ui.Window.Close();
    });

    [Fact]
    public Task A_missing_database_in_the_connection_string_is_said_where_it_can_be_seen() => fixture.RunAsync(async () =>
    {
        var ui = await OpenSqlServerFormAsync(TestServices.Create(null, new SqlServerProvider()));
        ui.Click(ui.Find<ToggleSwitch>("RawMode"));
        ui.Type(ui.Find<TextBox>("RawText"), "Data Source=sql01;User ID=sa");

        ui.Click(ui.Find<Button>("ConnectButton"));

        Assert.Equal("Database is required.", ui.Find<TextBlock>("HiddenFieldErrors").Text);
        Assert.Equal("Check the connection settings: Database is required.", ui.Find<SelectableTextBlock>("ConnectionError").Text);
        ui.Screenshot("22-connection-string-problem");
        ui.Window.Close();
    });

    [Fact]
    public Task A_problem_inside_advanced_opens_it() => fixture.RunAsync(async () =>
    {
        var ui = await OpenSqlServerFormAsync(TestServices.Create(null, new SqlServerProvider()));
        var editor = ui.Shell.Source.Editor!;
        WizardDriver.Fill(editor, "sql01", "shop", "sa", "hunter2");
        WizardDriver.Field(editor, "Connect Timeout").Value = "5000";
        UiDriver.Settle();
        Assert.False(ui.Find<Expander>("Advanced").IsExpanded);

        ui.Click(ui.Find<Button>("ConnectButton"));

        Assert.True(ui.Find<Expander>("Advanced").IsExpanded);
        Assert.Contains("between 1 and 600", ui.Find<SelectableTextBlock>("ConnectionError").Text, StringComparison.Ordinal);
        ui.Window.Close();
    });

    [Fact]
    public Task An_unusable_password_store_disables_remember_and_says_why() => fixture.RunAsync(async () =>
    {
        var test = TestServices.Create();
        test.Credentials.UnavailableReason = "libsecret is not installed.";
        var ui = await UiDriver.OpenAsync(test);

        Assert.False(ui.Find<CheckBox>("RememberPassword").IsEffectivelyEnabled);
        Assert.Equal("libsecret is not installed. You will be asked for the password each time.", ui.Find<TextBlock>("RememberPasswordNote").Text);
        ui.Window.Close();
    });

    [Fact]
    public Task A_failed_connection_shows_the_message_and_suggestions() => fixture.RunAsync(async () =>
    {
        var test = TestServices.Create();
        test.Provider.CheckError = () => new TimeoutException("Connection timed out after 15 seconds.");
        var ui = await UiDriver.OpenAsync(test);
        WizardDriver.Fill(ui.Shell.Source.Editor!, "src01", "shop");

        ui.Click(ui.Find<Button>("ConnectButton"));
        await UiDriver.UntilAsync(() => ui.Shell.Source.IsFailed, "the failure");

        Assert.Contains("timed out", ui.Find<SelectableTextBlock>("ConnectionError").Text, StringComparison.Ordinal);
        Assert.NotEmpty(ui.Find<ItemsControl>("Hints").Items);
        Assert.False(ui.IsShown("ErrorBanner"));
        ui.Screenshot("21-source-failed");
        ui.Window.Close();
    });
}

public sealed class TablesViewTests(AvaloniaFixture fixture)
{
    [Fact]
    public Task Typing_filters_and_one_click_toggles_a_row_once() => fixture.RunAsync(async () =>
    {
        var ui = await UiDriver.OpenAsync();
        await ui.Wizard.ConnectSourceAsync();
        await ui.Wizard.NextAsync();
        await ui.Wizard.ConnectTargetAsync();
        await ui.NextAsync();

        ui.Type(ui.Find<TextBox>("TableFilter"), "dbo.C");
        Assert.Equal(["dbo.C"], ui.Shell.Tables.VisibleRows.Select(r => r.FullName));

        var grid = ui.Find<DataGrid>("TablesGrid");
        var box = grid.GetVisualDescendants().OfType<CheckBox>().First(c => c.IsEffectivelyVisible);
        ui.Click(box);

        Assert.False(ui.Shell.Tables.Rows.Single(r => r.Name == TestServices.C).IsSelected);
        Assert.Equal(3, ui.Shell.Tables.SelectedCount);
        ui.Window.Close();
    });

    [Fact]
    public Task Thousands_of_tables_realise_only_the_rows_on_screen() => fixture.RunAsync(async () =>
    {
        var tables = Enumerable.Range(0, 5000).Select(i => Table("dbo", $"T{i:D4}", Id())).ToArray();
        var ui = await UiDriver.OpenAsync(TestServices.Create(Source(tables)));
        await ui.Wizard.ConnectSourceAsync();
        await ui.Wizard.NextAsync();
        await ui.Wizard.ConnectTargetAsync();
        await ui.NextAsync();

        var realised = ui.Find<DataGrid>("TablesGrid").GetVisualDescendants().OfType<DataGridRow>().Count();
        Assert.InRange(realised, 1, 99);
        ui.Window.Close();
    });
}

public sealed class ReviewViewTests(AvaloniaFixture fixture)
{
    [Fact]
    public Task A_decision_can_be_made_with_the_keyboard() => fixture.RunAsync(async () =>
    {
        var test = TestServices.Create();
        test.TargetDb.ExistingTables.Add(new QualifiedName("public", "a"));
        var ui = await UiDriver.OpenAsync(test);
        await ui.Wizard.ToReviewAsync();
        Assert.False(ui.Find<Button>("NextButton").IsEffectivelyEnabled);

        var choice = ui.ByLabel<ComboBox>("What to do with public.a");
        choice.Focus();
        ui.Press(Key.Down);
        await Task.Delay(20);
        test.Time.Advance(TimeSpan.FromSeconds(1));
        await UiDriver.UntilAsync(() => !ui.Shell.Review.IsPlanning && ui.Shell.Review.UndecidedCount == 0, "the re-plan");

        Assert.NotNull(ui.Shell.Review.Conflicts[0].Choice.Action);
        Assert.True(ui.Find<Button>("NextButton").IsEffectivelyEnabled);
        ui.Window.Close();
    });

    [Fact]
    public Task The_script_list_is_virtualised() => fixture.RunAsync(async () =>
    {
        var tables = Enumerable.Range(0, 2000).Select(i => Table("dbo", $"T{i:D4}", Id()).WithPrimaryKey($"PK_{i}", "Id")).ToArray();
        var ui = await UiDriver.OpenAsync(TestServices.Create(Source(tables)));
        await ui.Wizard.ToReviewAsync();
        ui.Shell.Review.SelectedTab = ReviewStepViewModel.ScriptTab;

        var list = ui.Find<ListBox>("ScriptSteps");
        Assert.True(ui.Shell.Review.Script.Rows.Count > 4000);
        Assert.InRange(list.GetVisualDescendants().OfType<ListBoxItem>().Count(), 1, 199);
        ui.Window.Close();
    });
}

public sealed class CloseGuardTests(AvaloniaFixture fixture)
{
    [Fact]
    public Task Closing_during_a_run_asks_then_stops_and_closes() => fixture.RunAsync(async () =>
    {
        var test = TestServices.Create();
        var gate = new Gate();
        test.SourceDb.Pauses[TestServices.B] = (2, gate);
        var ui = await UiDriver.OpenAsync(test);
        await ui.Wizard.ToRunAsync();
        ui.Click(ui.Find<Button>("StartButton"));
        await UiDriver.UntilAsync(() => gate.Reached.IsCompleted, "the copy to reach dbo.B");

        ui.Window.Close();
        await UiDriver.UntilAsync(() => ui.Shell.Dialogs.IsOpen, "the close question");
        Assert.True(ui.Window.IsVisible);
        Assert.Equal("Stop the migration and close?", ui.Find<TextBlock>("DialogTitle").Text);

        // Keep running: the window stays and the run goes on.
        ui.Click(ui.Find<Button>("DialogCancel"));
        Assert.True(ui.Window.IsVisible);
        Assert.True(ui.Shell.IsRunning);

        ui.Window.Close();
        await UiDriver.UntilAsync(() => ui.Shell.Dialogs.IsOpen, "the close question again");
        ui.Click(ui.Find<Button>("DialogConfirm"));
        await UiDriver.UntilAsync(() => !ui.Window.IsVisible, "the window to close");

        Assert.True(ui.Shell.Session.Report!.Cancelled);
        Assert.Single(test.Reports.Saved);
    });
}

public sealed class UiThreadingTests(AvaloniaFixture fixture)
{
    [Fact]
    public Task Through_the_view_models_no_database_work_runs_on_the_ui_thread() => fixture.RunAsync(async () =>
    {
        var test = TestServices.Create();
        test.ForbidThread(() => Dispatcher.UIThread.CheckAccess());
        var ui = await UiDriver.OpenAsync(test);

        await ui.Wizard.ToRunAsync();
        ui.Click(ui.Find<Button>("StartButton"));
        await UiDriver.UntilAsync(() => ui.Shell.Current == ui.Shell.Report, "the report");

        Assert.Empty(test.ThreadViolations);

        // The guard works: the same call made directly from the UI thread is caught.
        await MigrationEngine.PrepareAsync(test.SourceDb, test.TargetDb, new MigrationOptions(), CancellationToken.None);
        Assert.NotEmpty(test.ThreadViolations);
        ui.Window.Close();
    });
}

public sealed class WizardFlowTests(AvaloniaFixture fixture)
{
    [Fact]
    public Task A_whole_migration_with_mouse_and_keyboard() => fixture.RunAsync(async () =>
    {
        var test = TestServices.Create();
        test.TargetDb.ExistingTables.Add(new QualifiedName("public", "a"));
        var gate = new Gate();
        test.SourceDb.Pauses[TestServices.C] = (1, gate);
        var ui = await UiDriver.OpenAsync(test);

        // Source
        ui.Type(ui.ByLabel<TextBox>("Data Source"), "src01");
        ui.Type(ui.ByLabel<TextBox>("Initial Catalog"), "shop");
        ui.Type(ui.ByLabel<TextBox>("Password"), "s3cret");
        ui.Click(ui.Find<Button>("ConnectButton"));
        await UiDriver.UntilAsync(() => ui.Shell.Source.IsConnected, "the source to connect");
        await ui.NextAsync();

        // Target
        ui.Type(ui.ByLabel<TextBox>("Data Source"), "pg01");
        ui.Type(ui.ByLabel<TextBox>("Initial Catalog"), "shop");
        ui.Click(ui.Find<Button>("ConnectButton"));
        await UiDriver.UntilAsync(() => ui.Shell.Target.IsConnected, "the target to connect");
        await ui.NextAsync();

        // Tables: leave out dbo.D
        ui.Type(ui.Find<TextBox>("TableFilter"), "dbo.D");
        ui.Click(ui.Find<DataGrid>("TablesGrid").GetVisualDescendants().OfType<CheckBox>().First(c => c.IsEffectivelyVisible));
        Assert.Equal(3, ui.Shell.Tables.SelectedCount);
        await ui.NextAsync();

        // Review: public.a exists; choose with the keyboard, then continue
        Assert.Same(ui.Shell.Review, ui.Shell.Current);
        ui.ByLabel<ComboBox>("What to do with public.a").Focus();
        ui.Press(Key.Down);
        await Task.Delay(20);
        test.Time.Advance(TimeSpan.FromSeconds(1));
        await UiDriver.UntilAsync(() => !ui.Shell.Review.IsPlanning && ui.Shell.Review.UndecidedCount == 0, "the re-plan");
        await ui.NextAsync();

        // Run
        Assert.Same(ui.Shell.Run, ui.Shell.Current);
        if (ui.Shell.Run.RequiresAcknowledgement)
        {
            ui.Click(ui.Find<CheckBox>("AcknowledgeDestructive"));
        }

        ui.Click(ui.Find<Button>("StartButton"));
        await UiDriver.UntilAsync(() => gate.Reached.IsCompleted, "the copy to reach dbo.C");
        Assert.False(ui.IsShown("BackButton"));
        gate.Release();
        await UiDriver.UntilAsync(() => ui.Shell.Current == ui.Shell.Report, "the report");

        Assert.Equal("Migration succeeded", ui.Find<TextBlock>("Headline").Text);
        Assert.Equal(3, ui.Shell.Session.Report!.Tables.Count);
        Assert.DoesNotContain(test.TargetDb.Statements, s => s.Sql.Contains("\"public\".\"d\"", StringComparison.Ordinal));
        Assert.Contains(test.TargetDb.Statements, s => s.Sql.Contains("\"public\".\"c\"", StringComparison.Ordinal));
        Assert.Empty(test.TargetDb.RowsOf(new QualifiedName("public", "d")));
        ui.Window.Close();
    });
}
