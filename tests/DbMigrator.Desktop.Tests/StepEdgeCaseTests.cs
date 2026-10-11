using DbMigrator.Core.Execution;
using DbMigrator.Core.Planning;
using DbMigrator.Core.Schema;
using DbMigrator.Desktop.Testing;
using DbMigrator.Desktop.ViewModels;
using DbMigrator.Providers.SqlServer;
using DbMigrator.TestSupport;

namespace DbMigrator.Desktop.Tests;

/// <summary>Cases the review of milestone 2 found: going back, changing your mind, slow stores, failing files.</summary>
public sealed class ConnectionEdgeCaseTests
{
    [Fact]
    public Task A_slow_password_read_never_overwrites_a_later_choice() => UiThreadContext.RunAsync(async () =>
    {
        var test = TestServices.Create();
        var wizard = new WizardDriver(test);
        await wizard.StartAsync();
        var source = wizard.Shell.Source;
        var slow = await SaveProfileAsync(test, source, "slow", "server-a", remember: true);
        var quick = await SaveProfileAsync(test, source, "quick", "server-b", remember: false);

        var again = new WizardDriver(test);
        await again.StartAsync();
        var step = again.Shell.Source;
        test.Credentials.FindGate = new Gate();
        step.SelectedProfile = step.Profiles.Single(p => p.Profile.Id == slow);
        await Wait.ForAsync(test.Credentials.FindGate.Reached, "the slow read");
        step.SelectedProfile = step.Profiles.Single(p => p.Profile.Id == quick);
        await Wait.UntilAsync(() => WizardDriver.Field(step.Editor!, "Data Source").Value == "server-b", "the quick profile");

        test.Credentials.FindGate.Release();
        await Task.Delay(50);

        Assert.Equal("server-b", WizardDriver.Field(step.Editor!, "Data Source").Value);
        Assert.False(step.RememberPassword);
    });

    [Fact]
    public Task Saving_under_a_new_name_keeps_the_form_and_the_notice() => UiThreadContext.RunAsync(async () =>
    {
        var test = TestServices.Create();
        test.Credentials.SaveFailure = "The keychain is locked.";
        var wizard = new WizardDriver(test);
        await wizard.StartAsync();
        var source = wizard.Shell.Source;
        await SaveProfileAsync(test, source, "first", "src01", remember: false);
        await wizard.ConnectSourceAsync();
        source.RememberPassword = true;

        test.Dialogs.Prompt("second");
        await source.SaveProfileCommand.ExecuteAsync(null);

        Assert.Equal("second", source.SelectedProfile!.Name);
        Assert.Equal(WizardDriver.Password, WizardDriver.Field(source.Editor!, "Password").Value);
        Assert.True(source.IsConnected);
        Assert.Contains("The keychain is locked.", source.ProfileNotice, StringComparison.Ordinal);
    });

    [Fact]
    public Task A_profile_file_that_cannot_be_written_leaves_the_password_store_alone() => UiThreadContext.RunAsync(async () =>
    {
        var test = TestServices.Create();
        test.Profiles.WriteFailure = "The disk is full.";
        var wizard = new WizardDriver(test);
        await wizard.StartAsync();
        WizardDriver.Fill(wizard.Shell.Source.Editor!, "src01", "shop");
        wizard.Shell.Source.RememberPassword = true;

        test.Dialogs.Prompt("shop");
        await wizard.Shell.Source.SaveProfileCommand.ExecuteAsync(null);

        Assert.Equal("The disk is full.", wizard.Shell.Source.Error);
        Assert.Empty(test.Credentials.Secrets);
    });

    [Fact]
    public Task A_read_only_profile_file_is_shown_but_never_changed() => UiThreadContext.RunAsync(async () =>
    {
        var test = TestServices.Create();
        var wizard = new WizardDriver(test);
        await wizard.StartAsync();
        var id = await SaveProfileAsync(test, wizard.Shell.Source, "shop", "src01", remember: true);
        test.Profiles.IsReadOnly = true;

        var again = new WizardDriver(test);
        await again.StartAsync();
        var step = again.Shell.Source;
        step.SelectedProfile = step.Profiles.Single();
        await Wait.UntilAsync(() => WizardDriver.Field(step.Editor!, "Password").Value.Length > 0, "the profile to load");

        Assert.False(step.SaveProfileCommand.CanExecute(null));
        Assert.False(step.DeleteProfileCommand.CanExecute(null));
        await step.ConnectCommand.ExecuteAsync(null);
        Assert.True(step.IsConnected);
        Assert.Single(test.Credentials.Secrets);
        Assert.Equal(id, Assert.Single(test.Profiles.Stored).Id);
    });

    [Fact]
    public Task Test_and_connect_never_run_at_the_same_time() => UiThreadContext.RunAsync(async () =>
    {
        var test = TestServices.Create();
        test.Provider.CheckGate = new Gate();
        var wizard = new WizardDriver(test);
        await wizard.StartAsync();
        WizardDriver.Fill(wizard.Shell.Source.Editor!, "src01", "shop");

        var connect = wizard.Shell.Source.ConnectCommand.ExecuteAsync(null);
        await Wait.ForAsync(test.Provider.CheckGate.Reached, "the check");

        Assert.False(wizard.Shell.Source.TestConnectionCommand.CanExecute(null));
        test.Provider.CheckGate.Release();
        await Wait.ForAsync(connect, "the connect");
        Assert.True(wizard.Shell.Source.TestConnectionCommand.CanExecute(null));
    });

    [Fact]
    public Task Editing_the_form_while_connecting_shows_it_changed() => UiThreadContext.RunAsync(async () =>
    {
        var test = TestServices.Create();
        test.Provider.CheckGate = new Gate();
        var wizard = new WizardDriver(test);
        await wizard.StartAsync();
        var source = wizard.Shell.Source;
        WizardDriver.Fill(source.Editor!, "src01", "shop");

        var connect = source.ConnectCommand.ExecuteAsync(null);
        await Wait.ForAsync(test.Provider.CheckGate.Reached, "the check");
        WizardDriver.Field(source.Editor!, "Initial Catalog").Value = "other";
        test.Provider.CheckGate.Release();
        await Wait.ForAsync(connect, "the connect");

        Assert.Equal(ConnectionStatus.Changed, source.Status);
        Assert.False(source.CanAdvance);
    });

    [Fact]
    public void A_pasted_password_leaves_the_text_and_a_corrected_one_is_kept()
    {
        var editor = new ConnectionEditorViewModel(new SqlServerProvider().Connection, new FakeFileService());
        editor.IsRawMode = true;
        editor.RawText = "Server=sql01;Database=shop;User ID=sa;Password=wrong";
        Assert.True(editor.Validate());

        Assert.DoesNotContain("wrong", editor.RawText, StringComparison.Ordinal);
        WizardDriver.Field(editor, "Password").Value = "right";
        Assert.True(editor.Validate());
        Assert.Equal("right", WizardDriver.Field(editor, "Password").Value);
    }

    [Fact]
    public void A_password_typed_in_the_additional_settings_replaces_the_box()
    {
        var editor = new ConnectionEditorViewModel(new SqlServerProvider().Connection, new FakeFileService());
        WizardDriver.Field(editor, "Data Source").Value = "sql01";
        WizardDriver.Field(editor, "Initial Catalog").Value = "shop";
        WizardDriver.Field(editor, "User ID").Value = "sa";
        WizardDriver.Field(editor, "Password").Value = "old";
        editor.AdditionalSettings = "Password=new";

        Assert.True(editor.Validate());

        Assert.Equal("new", WizardDriver.Field(editor, "Password").Value);
        Assert.Equal("Moved the password from the additional settings into the password box.", editor.Notice);
    }

    [Fact]
    public void A_loaded_password_starts_hidden()
    {
        var editor = new ConnectionEditorViewModel(new SqlServerProvider().Connection, new FakeFileService());
        var password = (SecretFieldViewModel)WizardDriver.Field(editor, "Password");
        password.IsRevealed = true;

        editor.Load(new Dictionary<string, string> { ["Data Source"] = "sql02" }, "", new Dictionary<string, string> { ["Password"] = "stored" });

        Assert.False(password.IsRevealed);
    }

    /// <summary>Fills the form, saves it under <paramref name="name"/>, and returns the profile's id.</summary>
    internal static async Task<Guid> SaveProfileAsync(TestServices test, ConnectionStepViewModel step, string name, string server, bool remember)
    {
        step.SelectedProfile = null;
        WizardDriver.Fill(step.Editor!, server, "shop");
        step.RememberPassword = remember;
        test.Dialogs.Prompt(name);
        await step.SaveProfileCommand.ExecuteAsync(null);
        return test.Profiles.Stored.Single(p => p.Name == name).Id;
    }
}

public sealed class NavigationEdgeCaseTests
{
    [Fact]
    public Task Leaving_script_only_mode_asks_for_the_target_again() => UiThreadContext.RunAsync(async () =>
    {
        var wizard = new WizardDriver(TestServices.Create());
        await wizard.StartAsync();
        await wizard.ConnectSourceAsync();
        await wizard.NextAsync();
        await wizard.ConnectTargetAsync();
        wizard.Shell.Target.ScriptOnly = true;
        await wizard.NextAsync();
        await wizard.Shell.BackCommand.ExecuteAsync(null);

        wizard.Shell.Target.ScriptOnly = false;

        Assert.False(wizard.Shell.Target.CanAdvance);
        await wizard.ConnectTargetAsync();
        await wizard.NextAsync();
        await wizard.NextAsync();
        Assert.True(wizard.Shell.Run.IsApplicable);
        Assert.Equal("Continue to run", wizard.Shell.Review.NextLabel);
    });

    [Fact]
    public Task Decisions_are_asked_again_for_a_different_target() => UiThreadContext.RunAsync(async () =>
    {
        var test = TestServices.Create();
        test.TargetDb.ExistingTables.Add(Plans.TargetA);
        var wizard = new WizardDriver(test);
        await wizard.ToReviewAsync();
        wizard.Shell.Review.Conflicts[0].Choice = ConflictChoice.For(TableExistsAction.DropAndRecreate);
        await ReviewStepTests.ReplannedAsync(test, wizard.Shell.Review);
        Assert.Equal(0, wizard.Shell.Review.UndecidedCount);

        await wizard.Shell.GoToStepCommand.ExecuteAsync(wizard.Shell.Target);
        WizardDriver.Field(wizard.Shell.Target.Editor!, "Initial Catalog").Value = "another";
        await wizard.Shell.Target.ConnectCommand.ExecuteAsync(null);
        await wizard.NextAsync();
        await wizard.NextAsync();

        Assert.Equal(1, wizard.Shell.Review.UndecidedCount);
        Assert.True(Assert.Single(wizard.Shell.Review.Conflicts).IsUndecided);
        Assert.False(wizard.Shell.NextCommand.CanExecute(null));
    });

    [Fact]
    public Task A_new_source_fills_in_where_each_table_goes() => UiThreadContext.RunAsync(async () =>
    {
        var test = TestServices.Create();
        var wizard = new WizardDriver(test);
        await wizard.ToReviewAsync();
        await wizard.Shell.GoToStepCommand.ExecuteAsync(wizard.Shell.Source);
        await wizard.Shell.Source.ConnectCommand.ExecuteAsync(null);
        await wizard.NextAsync();
        await wizard.NextAsync();

        await Task.Delay(20);
        test.Time.Advance(TimeSpan.FromSeconds(1));
        await Wait.UntilAsync(() => wizard.Shell.Tables.Rows.All(r => r.TargetName.Length > 0), "the target names");
        Assert.Equal("public.a", wizard.Shell.Tables.Rows.First(r => r.Name == TestServices.A).TargetName);
    });

    [Fact]
    public Task Staying_after_a_changed_target_still_refreshes_the_review() => UiThreadContext.RunAsync(async () =>
    {
        var test = TestServices.Create();
        var wizard = new WizardDriver(test);
        await wizard.ToRunAsync();
        test.TargetDb.ExistingTables.Add(Plans.TargetC);

        test.Dialogs.Confirm(false);
        await wizard.Shell.Run.StartCommand.ExecuteAsync(null);
        Assert.Same(wizard.Shell.Run, wizard.Shell.Current);
        Assert.False(wizard.Shell.Run.StartCommand.CanExecute(null));

        await wizard.Shell.BackCommand.ExecuteAsync(null);

        Assert.Same(wizard.Shell.Review, wizard.Shell.Current);
        Assert.Equal(TestServices.C, Assert.Single(wizard.Shell.Review.Conflicts).Source);
    });

    [Fact]
    public Task A_script_that_cannot_be_saved_says_why() => UiThreadContext.RunAsync(async () =>
    {
        var test = TestServices.Create();
        test.Files.SaveFailure = "Access to the path is denied.";
        var wizard = new WizardDriver(test);
        await wizard.StartAsync();
        await wizard.ConnectSourceAsync();
        await wizard.NextAsync();
        wizard.Shell.Target.ScriptOnly = true;
        await wizard.NextAsync();
        await wizard.NextAsync();

        await wizard.NextAsync();

        Assert.Equal("Access to the path is denied.", wizard.Shell.Review.Error);
    });
}

public sealed class ReportEdgeCaseTests
{
    [Fact]
    public Task A_run_that_stopped_creating_tables_lists_every_table_as_not_started() => UiThreadContext.RunAsync(async () =>
    {
        var test = TestServices.Create();
        test.TargetDb.FailingStatements["CREATE TABLE"] = "permission denied for schema public";
        var wizard = new WizardDriver(test);
        await wizard.ToRunAsync();

        await wizard.Shell.Run.StartCommand.ExecuteAsync(null);

        var report = wizard.Shell.Report;
        Assert.Equal(ReportOutcome.Stopped, report.Outcome);
        Assert.Equal("0 of 4", report.Kpis[0].Value);
        Assert.Equal(4, report.Tables.Count);
        Assert.All(report.Tables, t => Assert.Equal(TableRunStatus.NotStarted, t.Status));
    });
}
