using DbMigrator.Core.Planning;
using DbMigrator.Core.Providers;
using DbMigrator.Desktop.Platform;
using DbMigrator.Desktop.Testing;
using DbMigrator.Desktop.ViewModels;
using DbMigrator.Providers.PostgreSql;
using DbMigrator.Providers.SqlServer;

namespace DbMigrator.Desktop.Tests.Platform;

public sealed class JsonProfileStoreTests : IDisposable
{
    private readonly TempHome _home = new();
    private readonly ManualTimeProvider _time = new();
    private readonly SqlServerProvider _sqlServer = new();
    private readonly ProviderCatalog _catalog;

    public JsonProfileStoreTests()
    {
        _catalog = new ProviderCatalog([_sqlServer, new PostgreSqlProvider()]);
    }

    public void Dispose() => _home.Dispose();

    private JsonProfileStore Store() => new(_home.Paths, _catalog, _time);

    private ConnectionProfile Profile(string name, string extra = "", DateTimeOffset? used = null) => ProfileFactory.Create(
        Guid.NewGuid(),
        name,
        _sqlServer,
        new Dictionary<string, string> { ["Data Source"] = "sql01", ["Initial Catalog"] = "shop", ["User ID"] = "sa", ["Password"] = "hunter2" },
        extra,
        rememberPassword: true,
        used ?? DateTimeOffset.UnixEpoch).Profile;

    [Fact]
    public void Profiles_round_trip_newest_first_without_their_passwords()
    {
        var older = Profile("older", "Packet Size=8192;PWD=hunter2", DateTimeOffset.UnixEpoch);
        var newer = Profile("newer", used: DateTimeOffset.UnixEpoch.AddDays(1));
        Store().Save(older);
        Store().Save(newer);

        var loaded = Store().Load();

        Assert.Equal(["newer", "older"], loaded.Profiles.Select(p => p.Name));
        Assert.Equal("sql01", loaded.Profiles[1].Values["Data Source"]);
        Assert.Contains("Packet Size", loaded.Profiles[1].AdditionalSettings, StringComparison.OrdinalIgnoreCase);
        Assert.True(loaded.Profiles[1].RememberPassword);
        Assert.Null(loaded.Notice);
        Assert.DoesNotContain("hunter2", File.ReadAllText(_home.Paths.ProfilesFile), StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(_home.Paths.ConfigFolder, "*.tmp"));
    }

    [Fact]
    public void Writes_merge_by_profile_so_two_windows_keep_each_others_changes()
    {
        var first = Store();
        var second = Store();
        first.Load();
        second.Load();

        first.Save(Profile("one"));
        second.Save(Profile("two"));
        var two = Store().Load().Profiles.Single(p => p.Name == "two");
        first.Delete(two.Id);

        Assert.Equal(["one"], Store().Load().Profiles.Select(p => p.Name));
    }

    [Fact]
    public void A_hand_edited_password_is_stripped_on_load()
    {
        Directory.CreateDirectory(_home.Paths.ConfigFolder);
        File.WriteAllText(_home.Paths.ProfilesFile, """
            { "version": 1, "profiles": [ { "id": "3f2b0000-0000-0000-0000-000000000001", "name": "edited", "provider": "sqlserver",
              "values": { "Data Source": "sql01", "Password": "hunter2" }, "additionalSettings": "pwd=hunter2;Packet Size=8192",
              "rememberPassword": false, "lastUsedUtc": "2026-10-10T08:00:00Z" } ] }
            """);

        var profile = Assert.Single(Store().Load().Profiles);

        Assert.False(profile.Values.ContainsKey("Password"));
        Assert.DoesNotContain("hunter2", profile.AdditionalSettings, StringComparison.Ordinal);
    }

    [Fact]
    public void A_corrupt_file_is_set_aside_and_explained()
    {
        Directory.CreateDirectory(_home.Paths.ConfigFolder);
        File.WriteAllText(_home.Paths.ProfilesFile, "{ not json");

        var loaded = Store().Load();

        Assert.Empty(loaded.Profiles);
        Assert.Contains("profiles.json.corrupt-20261010T080000Z", loaded.Notice, StringComparison.Ordinal);
        Assert.False(File.Exists(_home.Paths.ProfilesFile));
        Assert.True(File.Exists(_home.Paths.ProfilesFile + ".corrupt-20261010T080000Z"));
    }

    [Fact]
    public void A_file_from_a_newer_version_is_read_but_never_overwritten()
    {
        Directory.CreateDirectory(_home.Paths.ConfigFolder);
        const string newer = """
            { "version": 2, "somethingNew": true, "profiles": [ { "id": "3f2b0000-0000-0000-0000-000000000001", "name": "future",
              "provider": "sqlserver", "values": { "Data Source": "sql01" }, "additionalSettings": "", "rememberPassword": false,
              "lastUsedUtc": "2026-10-10T08:00:00Z", "colour": "blue" } ] }
            """;
        File.WriteAllText(_home.Paths.ProfilesFile, newer);
        var store = Store();

        var loaded = store.Load();

        Assert.True(loaded.IsReadOnly);
        Assert.NotNull(loaded.Notice);
        Assert.Equal("future", Assert.Single(loaded.Profiles).Name);
        Assert.Throws<InvalidOperationException>(() => store.Save(Profile("mine")));
        Assert.Equal(newer, File.ReadAllText(_home.Paths.ProfilesFile));
    }

    [Fact]
    public void A_newer_file_this_version_cannot_parse_is_kept_not_set_aside()
    {
        Directory.CreateDirectory(_home.Paths.ConfigFolder);
        const string newer = """{ "version": 3, "profiles": { "byName": {} } }""";
        File.WriteAllText(_home.Paths.ProfilesFile, newer);
        var store = Store();

        var loaded = store.Load();

        Assert.True(loaded.IsReadOnly);
        Assert.Empty(loaded.Profiles);
        Assert.Contains("newer version", loaded.Notice, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => store.Save(Profile("mine")));
        Assert.Equal(newer, File.ReadAllText(_home.Paths.ProfilesFile));
        Assert.Empty(Directory.GetFiles(_home.Paths.ConfigFolder, "*.corrupt-*"));
    }

    [Fact]
    public void A_file_a_newer_version_wrote_after_loading_is_never_downgraded()
    {
        var store = Store();
        store.Load();
        const string newer = """{ "version": 2, "profiles": [] }""";
        Directory.CreateDirectory(_home.Paths.ConfigFolder);
        File.WriteAllText(_home.Paths.ProfilesFile, newer);

        Assert.Throws<InvalidOperationException>(() => store.Save(Profile("mine")));
        Assert.Equal(newer, File.ReadAllText(_home.Paths.ProfilesFile));
    }

    [Fact]
    public void Null_entries_written_by_hand_are_dropped_not_crashed_on()
    {
        Directory.CreateDirectory(_home.Paths.ConfigFolder);
        File.WriteAllText(_home.Paths.ProfilesFile, """
            { "version": 1, "profiles": [ null, { "id": "3f2b0000-0000-0000-0000-000000000001", "name": "ok", "provider": "sqlserver",
              "values": { "Data Source": "sql01", "Initial Catalog": null }, "additionalSettings": "", "rememberPassword": false,
              "lastUsedUtc": "2026-10-10T08:00:00Z" } ] }
            """);

        var profile = Assert.Single(Store().Load().Profiles);

        Assert.Equal("ok", profile.Name);
        Assert.False(profile.Values.ContainsKey("Initial Catalog"));
    }

    [Fact]
    public void A_password_edited_into_another_profile_is_removed_by_any_write()
    {
        Directory.CreateDirectory(_home.Paths.ConfigFolder);
        File.WriteAllText(_home.Paths.ProfilesFile, """
            { "version": 1, "profiles": [ { "id": "3f2b0000-0000-0000-0000-000000000001", "name": "edited", "provider": "sqlserver",
              "values": { "Data Source": "sql01", "Password": "hunter2" }, "additionalSettings": "pwd=hunter2",
              "rememberPassword": false, "lastUsedUtc": "2026-10-10T08:00:00Z" } ] }
            """);

        Store().Save(Profile("another"));

        Assert.DoesNotContain("hunter2", File.ReadAllText(_home.Paths.ProfilesFile), StringComparison.Ordinal);
        Assert.Equal(2, Store().Load().Profiles.Count);
    }

    [Fact]
    public void Two_windows_writing_at_once_lose_nothing()
    {
        var first = Store();
        var second = Store();

        Parallel.For(0, 40, i => (i % 2 == 0 ? first : second).Save(Profile($"profile {i}")));

        Assert.Equal(40, Store().Load().Profiles.Count);
        Assert.Empty(Directory.GetFiles(_home.Paths.ConfigFolder, "*.tmp"));
    }

    [Fact]
    public void Only_the_user_can_read_the_file()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Unix file modes do not apply on Windows.");
        }
        else
        {
            Store().Save(Profile("private"));

            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(_home.Paths.ProfilesFile));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(_home.Paths.ConfigFolder));
        }
    }
}

public sealed class JsonSettingsStoreTests : IDisposable
{
    private readonly TempHome _home = new();
    private readonly ManualTimeProvider _time = new();
    private readonly InMemoryAppLog _log = new();

    public void Dispose() => _home.Dispose();

    [Fact]
    public void Settings_round_trip()
    {
        var settings = new AppSettings { Naming = IdentifierCase.SnakeCase, CreateIndexes = false, LastSourceProfileId = Guid.NewGuid() };
        new JsonSettingsStore(_home.Paths, _time, _log).Save(settings);

        Assert.Equal(settings, new JsonSettingsStore(_home.Paths, _time, _log).Load());
        Assert.Contains("\"naming\": \"SnakeCase\"", File.ReadAllText(_home.Paths.SettingsFile), StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_from_a_newer_version_are_kept()
    {
        Directory.CreateDirectory(_home.Paths.ConfigFolder);
        const string newer = """{ "version": 2, "naming": "SomethingNew" }""";
        File.WriteAllText(_home.Paths.SettingsFile, newer);
        var store = new JsonSettingsStore(_home.Paths, _time, _log);

        Assert.Equal(new AppSettings(), store.Load());
        Assert.Throws<InvalidOperationException>(() => store.Save(new AppSettings()));
        Assert.Equal(newer, File.ReadAllText(_home.Paths.SettingsFile));
    }

    [Fact]
    public void Missing_or_corrupt_settings_give_the_defaults()
    {
        var store = new JsonSettingsStore(_home.Paths, _time, _log);
        Assert.Equal(new AppSettings(), store.Load());

        Directory.CreateDirectory(_home.Paths.ConfigFolder);
        File.WriteAllText(_home.Paths.SettingsFile, "[1, 2");

        Assert.Equal(new AppSettings(), store.Load());
        Assert.True(File.Exists(_home.Paths.SettingsFile + ".corrupt-20261010T080000Z"));
        Assert.Contains(_log.Lines, l => l.Contains("set aside", StringComparison.Ordinal));
    }
}

public sealed class ReportArchiveAndLogTests : IDisposable
{
    private readonly TempHome _home = new();
    private readonly ManualTimeProvider _time = new();

    public void Dispose() => _home.Dispose();

    [Fact]
    public void Reports_never_overwrite_each_other()
    {
        var archive = new ReportArchive(_home.Paths);

        var first = archive.Save("dbmigrator-20261010-080000-succeeded", "# one", "{}");
        var second = archive.Save("dbmigrator-20261010-080000-succeeded", "# two", "{}");

        Assert.Equal(Path.Combine(_home.Paths.ReportsFolder, "dbmigrator-20261010-080000-succeeded.md"), first);
        Assert.Equal(Path.Combine(_home.Paths.ReportsFolder, "dbmigrator-20261010-080000-succeeded-2.md"), second);
        Assert.Equal("# two", File.ReadAllText(second));
        Assert.True(File.Exists(Path.ChangeExtension(second, ".json")));
    }

    [Fact]
    public void The_log_writes_a_file_per_day_and_keeps_ten_days()
    {
        Directory.CreateDirectory(_home.Paths.LogsFolder);
        var old = Path.Combine(_home.Paths.LogsFolder, "desktop-20260901.log");
        var recent = Path.Combine(_home.Paths.LogsFolder, "desktop-20261005.log");
        File.WriteAllText(old, "old");
        File.WriteAllText(recent, "recent");

        var log = new FileAppLog(_home.Paths.LogsFolder, _time);
        log.Info("Run started.");
        log.Error("Saving failed.", new IOException("disk full"));

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
        var text = File.ReadAllText(Path.Combine(_home.Paths.LogsFolder, "desktop-20261010.log"));
        Assert.Contains("2026-10-10T08:00:00.000Z INFO  Run started.", text, StringComparison.Ordinal);
        Assert.Contains("ERROR Saving failed.", text, StringComparison.Ordinal);
        Assert.Contains("disk full", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_the_user_can_read_the_log()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Unix file modes do not apply on Windows.");
        }
        else
        {
            var shared = Path.Combine(_home.Root, "logs");
            Directory.CreateDirectory(shared);
            File.SetUnixFileMode(shared, (UnixFileMode)0b111_101_101);

            new FileAppLog(shared, _time).Info("Run started.");

            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(shared, "desktop-20261010.log")));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(shared));
        }
    }

    [Fact]
    public void A_log_that_cannot_be_written_does_not_throw()
    {
        var file = Path.Combine(_home.Root, "not-a-folder");
        Directory.CreateDirectory(_home.Root);
        File.WriteAllText(file, "");

        var log = new FileAppLog(file, _time);
        log.Info("nowhere to go");
    }
}

public sealed class AppPathsTests
{
    [Fact]
    public void The_home_variable_puts_everything_in_one_folder()
    {
        var paths = AppPaths.Resolve(v => v == AppPaths.HomeVariable ? "/portable" : null, _ => "/home/ana");

        Assert.Equal(Path.Combine("/portable", "config"), paths.ConfigFolder);
        Assert.Equal(Path.Combine("/portable", "data", "reports"), paths.ReportsFolder);
        Assert.Equal(Path.Combine("/portable", "logs"), paths.LogsFolder);
        Assert.Equal(Path.Combine("/portable", "config", "profiles.json"), paths.ProfilesFile);
    }

    [Fact]
    public void Linux_follows_the_xdg_base_directories()
    {
        var defaults = AppPaths.Xdg(_ => null, "/home/ana");
        Assert.Equal(Path.Combine("/home/ana", ".config", "dbmigrator"), defaults.ConfigFolder);
        Assert.Equal(Path.Combine("/home/ana", ".local", "share", "dbmigrator", "reports"), defaults.ReportsFolder);
        Assert.Equal(Path.Combine("/home/ana", ".local", "state", "dbmigrator", "logs"), defaults.LogsFolder);

        var custom = AppPaths.Xdg(v => v switch { "XDG_CONFIG_HOME" => "/cfg", "XDG_STATE_HOME" => "relative/ignored", _ => null }, "/home/ana");
        Assert.Equal(Path.Combine("/cfg", "dbmigrator"), custom.ConfigFolder);
        Assert.Equal(Path.Combine("/home/ana", ".local", "state", "dbmigrator", "logs"), custom.LogsFolder);
    }
}
