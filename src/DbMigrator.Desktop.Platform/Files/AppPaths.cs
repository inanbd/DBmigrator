namespace DbMigrator.Desktop.Platform;

/// <summary>
/// Where the app keeps its files, following each platform's conventions.
/// <c>DBMIGRATOR_DESKTOP_HOME</c> puts everything under one folder, for tests and portable use.
/// </summary>
public sealed record AppPaths(string ConfigFolder, string ReportsFolder, string LogsFolder)
{
    public const string HomeVariable = "DBMIGRATOR_DESKTOP_HOME";

    public string ProfilesFile => Path.Combine(ConfigFolder, "profiles.json");

    public string SettingsFile => Path.Combine(ConfigFolder, "settings.json");

    public static AppPaths ForCurrentUser() => Resolve(Environment.GetEnvironmentVariable, Environment.GetFolderPath);

    /// <summary>Everything under one folder.</summary>
    public static AppPaths UnderHome(string home) =>
        new(Path.Combine(home, "config"), Path.Combine(home, "data", "reports"), Path.Combine(home, "logs"));

    internal static AppPaths Resolve(Func<string, string?> environment, Func<Environment.SpecialFolder, string> folders)
    {
        if (environment(HomeVariable) is { Length: > 0 } home)
        {
            return UnderHome(home);
        }

        var user = folders(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            var local = Path.Combine(folders(Environment.SpecialFolder.LocalApplicationData), "DbMigrator");
            return new(Path.Combine(folders(Environment.SpecialFolder.ApplicationData), "DbMigrator"), Path.Combine(local, "reports"), Path.Combine(local, "logs"));
        }

        if (OperatingSystem.IsMacOS())
        {
            var support = Path.Combine(user, "Library", "Application Support", "DbMigrator");
            return new(support, Path.Combine(support, "reports"), Path.Combine(user, "Library", "Logs", "DbMigrator"));
        }

        return Xdg(environment, user);
    }

    /// <summary>The XDG base directories, with their documented defaults.</summary>
    internal static AppPaths Xdg(Func<string, string?> environment, string user)
    {
        string Base(string variable, params string[] fallback) =>
            environment(variable) is { Length: > 0 } value && Path.IsPathRooted(value) ? value : Path.Combine([user, .. fallback]);

        return new(
            Path.Combine(Base("XDG_CONFIG_HOME", ".config"), "dbmigrator"),
            Path.Combine(Base("XDG_DATA_HOME", ".local", "share"), "dbmigrator", "reports"),
            Path.Combine(Base("XDG_STATE_HOME", ".local", "state"), "dbmigrator", "logs"));
    }
}
