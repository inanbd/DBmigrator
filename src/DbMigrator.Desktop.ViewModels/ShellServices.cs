using DbMigrator.Core.Providers;

namespace DbMigrator.Desktop.ViewModels;

/// <summary>Everything the wizard needs from outside, composed once by the app (or by tests with fakes).</summary>
public sealed record ShellServices(
    ProviderCatalog Providers,
    DialogHostViewModel Dialogs,
    IFileService Files,
    IClipboardService Clipboard,
    IUiScheduler Scheduler,
    ICredentialStore Credentials,
    IProfileStore Profiles,
    ISettingsStore Settings,
    IReportArchive Reports,
    IAppLog Log,
    TimeProvider Time)
{
    /// <summary>The app's version, shown in the step rail.</summary>
    public string Version { get; init; } = "dev";
}
