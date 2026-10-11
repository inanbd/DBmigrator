using System.Reflection;
using Avalonia.Controls;
using DbMigrator.Desktop.Platform;
using DbMigrator.Desktop.Services;
using DbMigrator.Desktop.ViewModels;
using DbMigrator.Providers;

namespace DbMigrator.Desktop;

/// <summary>Builds the app's services once. There is no container: this is the whole wiring.</summary>
internal static class CompositionRoot
{
    public static ShellViewModel CreateShell(TopLevel window)
    {
        var paths = AppPaths.ForCurrentUser();
        var time = TimeProvider.System;
        var log = new FileAppLog(paths.LogsFolder, time);
        var catalog = BuiltInProviders.Catalog;
        var services = new ShellServices(
            catalog,
            new DialogHostViewModel(),
            new AvaloniaFileService(() => window),
            new AvaloniaClipboardService(() => window),
            new AvaloniaUiScheduler(),
            CredentialStores.ForCurrentPlatform(),
            new JsonProfileStore(paths, catalog, time),
            new JsonSettingsStore(paths, time, log),
            new ReportArchive(paths),
            log,
            time)
        {
            Version = Version,
        };
        log.Info($"DbMigrator {services.Version} started on {Environment.OSVersion}.");
        return new ShellViewModel(services);
    }

    private static string Version =>
        typeof(CompositionRoot).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "dev";
}
