using DbMigrator.Core.Providers;
using DbMigrator.Desktop.Testing;
using DbMigrator.Desktop.ViewModels;
using DbMigrator.Providers.PostgreSql;
using DbMigrator.Providers.SqlServer;
using DbMigrator.TestSupport;

namespace DbMigrator.Desktop.Tests;

public sealed class ConnectionEditorTests
{
    private static ConnectionEditorViewModel SqlServerEditor() => new(new SqlServerProvider().Connection, new FakeFileService());

    [Fact]
    public void Fields_come_from_the_engine_format()
    {
        var format = new SqlServerProvider().Connection;
        var editor = new ConnectionEditorViewModel(format, new FakeFileService());

        Assert.Equal(format.Fields.Select(f => f.Keyword), editor.AllFields.Select(f => f.Keyword));
        Assert.All(editor.AdvancedFields, f => Assert.True(f.Field.IsAdvanced));
        Assert.IsType<SecretFieldViewModel>(WizardDriver.Field(editor, "Password"));
        Assert.IsType<ChoiceFieldViewModel>(WizardDriver.Field(editor, "Encrypt"));
    }

    [Fact]
    public void Windows_authentication_hides_the_login_and_its_requirement()
    {
        var editor = SqlServerEditor();
        WizardDriver.Field(editor, "Data Source").Value = "sql01";
        WizardDriver.Field(editor, "Initial Catalog").Value = "shop";
        Assert.False(editor.Validate());
        Assert.NotNull(WizardDriver.Field(editor, "User ID").Error);

        ((ToggleFieldViewModel)WizardDriver.Field(editor, "Integrated Security")).IsChecked = true;

        Assert.False(WizardDriver.Field(editor, "Password").IsVisible);
        Assert.False(editor.HasVisibleSecret);
        Assert.True(editor.Validate());
    }

    [Theory]
    [InlineData("Password")]
    [InlineData("PWD")]
    [InlineData("pwd")]
    public void A_pasted_password_moves_into_the_password_box(string spelling)
    {
        var editor = SqlServerEditor();
        editor.IsRawMode = true;
        editor.RawText = $"Server=sql01;Database=shop;User ID=sa;{spelling}=hunter2;Packet Size=8192";

        editor.IsRawMode = false;

        Assert.Equal("hunter2", WizardDriver.Field(editor, "Password").Value);
        Assert.Equal("sql01", WizardDriver.Field(editor, "Data Source").Value);
        Assert.DoesNotContain("hunter2", editor.AdditionalSettings, StringComparison.Ordinal);
        Assert.Contains("Packet Size", editor.AdditionalSettings, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(editor.Notice);

        // Back in raw mode the password is not shown.
        editor.IsRawMode = true;
        Assert.DoesNotContain("hunter2", editor.RawText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("PSW", "Password")]
    [InlineData("SSL Password", "SSL Password")]
    public void A_postgres_secret_typed_in_the_additional_settings_moves_into_its_box(string spelling, string field)
    {
        var editor = new ConnectionEditorViewModel(new PostgreSqlProvider().Connection, new FakeFileService());
        WizardDriver.Field(editor, "Host").Value = "pg01";
        WizardDriver.Field(editor, "Database").Value = "shop";
        WizardDriver.Field(editor, "Username").Value = "migrator";
        editor.AdditionalSettings = $"{spelling}=hunter2;Command Timeout=60";

        Assert.True(editor.Validate());

        Assert.Equal("hunter2", WizardDriver.Field(editor, field).Value);
        Assert.DoesNotContain("hunter2", editor.AdditionalSettings, StringComparison.Ordinal);
    }

    [Fact]
    public void A_password_is_used_exactly_as_typed()
    {
        var editor = SqlServerEditor();
        WizardDriver.Field(editor, "Data Source").Value = "  sql01 ";
        WizardDriver.Field(editor, "Password").Value = " pass word ";

        var values = editor.CurrentValues();

        Assert.Equal("sql01", values["Data Source"]);
        Assert.Equal(" pass word ", values["Password"]);
    }

    [Fact]
    public void The_fingerprint_changes_with_any_value()
    {
        var editor = SqlServerEditor();
        WizardDriver.Field(editor, "Data Source").Value = "sql01";
        var before = editor.Fingerprint;

        WizardDriver.Field(editor, "Password").Value = "x";
        var withPassword = editor.Fingerprint;
        editor.AdditionalSettings = "Packet Size=8192";

        Assert.NotEqual(before, withPassword);
        Assert.NotEqual(withPassword, editor.Fingerprint);
    }

    [Fact]
    public void The_password_boxes_under_the_connection_string_are_never_rebuilt()
    {
        var editor = SqlServerEditor();
        editor.IsRawMode = true;
        var secrets = editor.Secrets;
        var raised = new List<string?>();
        editor.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        WizardDriver.Field(editor, "Password").Value = "h";
        WizardDriver.Field(editor, "Password").Value = "hu";
        ((ToggleFieldViewModel)WizardDriver.Field(editor, "Integrated Security")).IsChecked = true;

        Assert.Same(secrets, editor.Secrets);
        Assert.DoesNotContain(nameof(ConnectionEditorViewModel.Secrets), raised);
        Assert.False(Assert.Single(editor.Secrets).IsVisible);
    }

    [Fact]
    public void A_problem_with_a_field_hidden_by_the_connection_string_is_said_under_the_text()
    {
        var editor = SqlServerEditor();
        editor.IsRawMode = true;
        editor.RawText = "Data Source=sql01;User ID=sa";

        Assert.False(editor.Validate());

        Assert.Equal("Database is required.", editor.HiddenFieldErrors);
        Assert.Contains("Database is required.", editor.Problems());

        // Editing the text takes the stale message away, as typing in a field clears its error.
        editor.RawText = "Data Source=sql01;Initial Catalog=shop;User ID=sa";
        Assert.Null(editor.HiddenFieldErrors);
        Assert.True(editor.Validate());
        Assert.Null(editor.HiddenFieldErrors);
        Assert.Empty(editor.Problems());
    }

    [Fact]
    public void A_problem_inside_advanced_opens_it()
    {
        var editor = new ConnectionEditorViewModel(new PostgreSqlProvider().Connection, new FakeFileService());
        WizardDriver.Field(editor, "Host").Value = "pg01";
        WizardDriver.Field(editor, "Database").Value = "shop";
        WizardDriver.Field(editor, "Username").Value = "migrator";
        var timeout = WizardDriver.Field(editor, "Timeout");
        Assert.True(timeout.Field.IsAdvanced);
        timeout.Value = "5000";
        Assert.False(editor.IsAdvancedExpanded);

        Assert.False(editor.Validate());

        Assert.True(editor.IsAdvancedExpanded);
        Assert.NotNull(timeout.Error);
        Assert.Contains(timeout.Error, editor.Problems());
    }

    [Fact]
    public void Unparseable_additional_settings_open_advanced()
    {
        var editor = SqlServerEditor();
        WizardDriver.Fill(editor, "sql01", "shop");
        editor.AdditionalSettings = "Packet Size";

        Assert.False(editor.Validate());

        Assert.True(editor.IsAdvancedExpanded);
        Assert.NotNull(editor.RawError);
        Assert.Contains(editor.RawError, editor.Problems());
    }
}

public sealed class ProfileTests
{
    public static TheoryData<string, string> Spellings() => new()
    {
        { "sqlserver", "Password=hunter2" },
        { "sqlserver", "PWD=hunter2" },
        { "sqlserver", "pwd=hunter2" },
        { "postgresql", "Password=hunter2" },
        { "postgresql", "PSW=hunter2" },
        { "postgresql", "SSL Password=hunter2" },
    };

    [Theory]
    [MemberData(nameof(Spellings))]
    public void A_saved_profile_never_holds_a_password(string providerId, string secret)
    {
        IDatabaseProvider provider = providerId == "sqlserver" ? new SqlServerProvider() : new PostgreSqlProvider();
        var catalog = new ProviderCatalog([provider]);
        var store = new InMemoryProfileStore(catalog);
        var values = provider.Connection.Parse($"{(providerId == "sqlserver" ? "Server=sql01;Database=shop;User ID=sa" : "Host=pg01;Database=shop;Username=sa")};{secret}").Values;

        // Through the field values, and through the additional settings.
        var fromFields = ProfileFactory.Create(Guid.NewGuid(), "fields", provider, values, "", rememberPassword: false, DateTimeOffset.UnixEpoch);
        var fromAdditional = ProfileFactory.Create(Guid.NewGuid(), "additional", provider, new Dictionary<string, string>(), secret, rememberPassword: false, DateTimeOffset.UnixEpoch);
        store.Save(fromFields.Profile);
        store.Save(fromAdditional.Profile);

        Assert.True(fromFields.DroppedSecret);
        Assert.True(fromAdditional.DroppedSecret);
        Assert.DoesNotContain("hunter2", store.Text, StringComparison.Ordinal);

        // Even a file edited by hand to hold one loses it on load.
        store.Add(fromFields.Profile.ToStored() with { AdditionalSettings = secret });
        Assert.All(store.Load().Profiles, p => Assert.DoesNotContain("hunter2", p.AdditionalSettings + string.Join(";", p.Values.Values), StringComparison.Ordinal));
    }

    [Fact]
    public Task Saving_from_the_form_keeps_the_password_out_of_the_file() => UiThreadContext.RunAsync(async () =>
    {
        var test = TestServices.Create();
        var wizard = new WizardDriver(test);
        await wizard.StartAsync();
        var editor = wizard.Shell.Source.Editor!;
        editor.IsRawMode = true;
        editor.RawText = "Data Source=src01;Initial Catalog=shop;User ID=sa;pwd=hunter2";
        editor.IsRawMode = false;

        test.Dialogs.Prompt("shop");
        await wizard.Shell.Source.SaveProfileCommand.ExecuteAsync(null);

        Assert.Single(test.Profiles.Stored);
        Assert.DoesNotContain("hunter2", test.Profiles.Text, StringComparison.Ordinal);
        Assert.Empty(test.Credentials.Secrets);
        Assert.Equal("Saved without the password.", wizard.Shell.Source.ProfileNotice);
    });

    [Fact]
    public Task Remember_password_puts_it_in_the_os_store_only() => UiThreadContext.RunAsync(async () =>
    {
        var test = TestServices.Create();
        var wizard = new WizardDriver(test);
        await wizard.StartAsync();
        WizardDriver.Fill(wizard.Shell.Source.Editor!, "src01", "shop");
        wizard.Shell.Source.RememberPassword = true;

        test.Dialogs.Prompt("shop");
        await wizard.Shell.Source.SaveProfileCommand.ExecuteAsync(null);

        var stored = Assert.Single(test.Profiles.Stored);
        Assert.True(stored.RememberPassword);
        Assert.Equal(WizardDriver.Password, test.Credentials.Secrets[ProfileFactory.SecretKey(stored.Id, "Password")]);
        Assert.DoesNotContain(WizardDriver.Password, test.Profiles.Text, StringComparison.Ordinal);

        // Loading the profile in a new window fills the password from the store.
        var again = new WizardDriver(test);
        await again.StartAsync();
        again.Shell.Source.SelectedProfile = again.Shell.Source.Profiles.Single();
        await Wait.UntilAsync(() => WizardDriver.Field(again.Shell.Source.Editor!, "Password").Value.Length > 0, "the password to load");
        Assert.Equal(WizardDriver.Password, WizardDriver.Field(again.Shell.Source.Editor!, "Password").Value);

        // Deleting the profile removes its password too.
        test.Dialogs.Confirm(true);
        await again.Shell.Source.DeleteProfileCommand.ExecuteAsync(null);
        Assert.Empty(test.Profiles.Stored);
        Assert.Empty(test.Credentials.Secrets);
    });

    [Fact]
    public Task An_unavailable_store_disables_remember_with_its_reason() => UiThreadContext.RunAsync(async () =>
    {
        var test = TestServices.Create();
        test.Credentials.UnavailableReason = "No Secret Service is running.";
        var wizard = new WizardDriver(test);
        await wizard.StartAsync();
        WizardDriver.Fill(wizard.Shell.Source.Editor!, "src01", "shop");

        Assert.False(wizard.Shell.Source.CanRememberPassword);
        Assert.Equal("No Secret Service is running.", wizard.Shell.Source.CredentialStoreUnavailableReason);
    });

    [Fact]
    public Task A_store_failure_saves_the_profile_without_the_password() => UiThreadContext.RunAsync(async () =>
    {
        var test = TestServices.Create();
        test.Credentials.SaveFailure = "The keychain is locked.";
        var wizard = new WizardDriver(test);
        await wizard.StartAsync();
        WizardDriver.Fill(wizard.Shell.Source.Editor!, "src01", "shop");
        wizard.Shell.Source.RememberPassword = true;

        test.Dialogs.Prompt("shop");
        await wizard.Shell.Source.SaveProfileCommand.ExecuteAsync(null);

        var stored = Assert.Single(test.Profiles.Stored);
        Assert.False(stored.RememberPassword);
        Assert.Empty(test.Credentials.Secrets);
        Assert.Contains("The keychain is locked.", wizard.Shell.Source.ProfileNotice, StringComparison.Ordinal);
        Assert.False(wizard.Shell.Source.RememberPassword);
    });
}

public sealed class ConnectionStepTests
{
    [Fact]
    public Task A_failed_connection_shows_the_driver_message_and_hints() => UiThreadContext.RunAsync(async () =>
    {
        var test = TestServices.Create();
        test.Provider.CheckError = () => new TimeoutException("Connection timed out after 15 seconds.");
        var wizard = new WizardDriver(test);
        await wizard.StartAsync();
        WizardDriver.Fill(wizard.Shell.Source.Editor!, "src01", "shop");

        await wizard.Shell.Source.ConnectCommand.ExecuteAsync(null);

        Assert.Equal(ConnectionStatus.Failed, wizard.Shell.Source.Status);
        Assert.Contains("timed out", wizard.Shell.Source.Error, StringComparison.Ordinal);
        Assert.NotEmpty(wizard.Shell.Source.Hints);
        Assert.False(wizard.Shell.Source.CanAdvance);
    });

    [Fact]
    public Task An_invalid_form_says_why_on_the_status_card() => UiThreadContext.RunAsync(async () =>
    {
        var test = TestServices.Create();
        var wizard = new WizardDriver(test);
        await wizard.StartAsync();
        var source = wizard.Shell.Source;
        source.Editor!.IsRawMode = true;
        source.Editor.RawText = "Data Source=src01;User ID=migrator";

        await source.ConnectCommand.ExecuteAsync(null);

        Assert.Equal("Check the connection settings: Database is required.", source.Error);
        Assert.Equal(ConnectionStatus.Idle, source.Status);
        Assert.Equal(0, test.Provider.CheckCount);

        // Saving a valid form takes the message away.
        source.Editor.RawText = "Data Source=src01;Initial Catalog=shop;User ID=migrator";
        test.Dialogs.Prompt("src01");
        await source.SaveProfileCommand.ExecuteAsync(null);
        Assert.Null(source.Error);
    });

    [Fact]
    public Task Cancelling_a_connect_is_silent() => UiThreadContext.RunAsync(async () =>
    {
        var test = TestServices.Create();
        test.Provider.CheckGate = new Gate();
        var wizard = new WizardDriver(test);
        await wizard.StartAsync();
        WizardDriver.Fill(wizard.Shell.Source.Editor!, "src01", "shop");

        var connect = wizard.Shell.Source.ConnectCommand.ExecuteAsync(null);
        await Wait.ForAsync(test.Provider.CheckGate.Reached, "the check to start");
        Assert.True(wizard.Shell.Source.IsBusy);
        wizard.Shell.Source.ConnectCancelCommand.Execute(null);
        await Wait.ForAsync(connect, "the connect to stop");

        Assert.False(wizard.Shell.Source.IsBusy);
        Assert.Null(wizard.Shell.Source.Error);
        Assert.False(wizard.Shell.Source.IsConnected);
    });

    [Fact]
    public Task Privilege_findings_are_shown_after_connecting() => UiThreadContext.RunAsync(async () =>
    {
        var test = TestServices.Create();
        test.Provider.CheckFindings.Add(new Core.Diagnostics.MigrationIssue(Core.Diagnostics.IssueSeverity.Warning, Core.Diagnostics.IssueCodes.LimitedCatalogAccess, "shop", "This login cannot see every table."));
        var wizard = new WizardDriver(test);
        await wizard.StartAsync();

        await wizard.ConnectSourceAsync();

        var finding = Assert.Single(wizard.Shell.Source.Findings);
        Assert.Equal(Core.Diagnostics.IssueCodes.LimitedCatalogAccess, finding.Code);
        Assert.True(wizard.Shell.Source.CanAdvance);
    });

    [Fact]
    public Task The_target_counts_tables_that_already_exist() => UiThreadContext.RunAsync(async () =>
    {
        var test = TestServices.Create();
        test.TargetDb.ExistingTables.Add(Plans.TargetA);
        test.TargetDb.ExistingTables.Add(new Core.Schema.QualifiedName("public", "unrelated"));
        var wizard = new WizardDriver(test);
        await wizard.StartAsync();
        await wizard.ConnectSourceAsync();
        await wizard.NextAsync();

        await wizard.ConnectTargetAsync();
        await Wait.UntilAsync(() => wizard.Shell.Target.MatchingTableCount is not null, "the matching count");

        Assert.Equal(1, wizard.Shell.Target.MatchingTableCount);
        Assert.Equal(2, wizard.Shell.Target.Summary!.ExistingTableCount);
    });
}
