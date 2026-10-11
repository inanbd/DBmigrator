using DbMigrator.Core.Providers;
using DbMigrator.Core.Schema;
using DbMigrator.Desktop.ViewModels;
using DbMigrator.TestSupport;
using static DbMigrator.TestSupport.SchemaBuilder;

namespace DbMigrator.Desktop.Testing;

/// <summary>
/// The desktop app's services, all faked, over one in-memory engine that serves as both source
/// and target. The real <see cref="Core.Execution.MigrationEngine"/> runs against it.
/// </summary>
public sealed class TestServices
{
    public static readonly QualifiedName A = new("dbo", "A");
    public static readonly QualifiedName B = new("dbo", "B");
    public static readonly QualifiedName C = new("dbo", "C");
    public static readonly QualifiedName D = new("dbo", "D");

    private TestServices(SourceSchema schema, IReadOnlyList<IDatabaseProvider> more)
    {
        SourceDb = new InMemorySourceDatabase(schema) { DefaultRowCount = 5 };
        TargetDb = new InMemoryTargetDatabase();
        Provider = new InMemoryProvider(SourceDb, TargetDb);
        Catalog = new ProviderCatalog([Provider, .. more]);
        Time = new ManualTimeProvider();
        Scheduler = new ManualUiScheduler();
        DialogHost = new DialogHostViewModel();
        Dialogs = new ScriptedDialogs(DialogHost);
        Files = new FakeFileService();
        Clipboard = new FakeClipboard();
        Credentials = new InMemoryCredentialStore();
        Profiles = new InMemoryProfileStore(Catalog);
        Settings = new InMemorySettingsStore();
        Reports = new InMemoryReportArchive();
        Log = new InMemoryAppLog();
        Services = new ShellServices(Catalog, DialogHost, Files, Clipboard, Scheduler, Credentials, Profiles, Settings, Reports, Log, Time)
        {
            Version = "test",
        };
    }

    public InMemorySourceDatabase SourceDb { get; }

    public InMemoryTargetDatabase TargetDb { get; }

    public InMemoryProvider Provider { get; }

    public ProviderCatalog Catalog { get; }

    public ManualTimeProvider Time { get; }

    public ManualUiScheduler Scheduler { get; }

    public DialogHostViewModel DialogHost { get; }

    public ScriptedDialogs Dialogs { get; }

    public FakeFileService Files { get; }

    public FakeClipboard Clipboard { get; }

    public InMemoryCredentialStore Credentials { get; }

    public InMemoryProfileStore Profiles { get; }

    public InMemorySettingsStore Settings { get; }

    public InMemoryReportArchive Reports { get; }

    public InMemoryAppLog Log { get; }

    public ShellServices Services { get; }

    /// <summary>Calls into fakes that arrived on the fake UI thread.</summary>
    public IReadOnlyList<string> ThreadViolations =>
        [.. SourceDb.Thread.Violations, .. TargetDb.Thread.Violations, .. Credentials.Thread.Violations,
         .. Profiles.Thread.Violations, .. Settings.Thread.Violations, .. Reports.Thread.Violations];

    /// <summary>
    /// Four tables, like the engine tests: D has a foreign key to C. Five rows each.
    /// <paramref name="schema"/> replaces them.
    /// </summary>
    /// <param name="more">Further engines offered after the in-memory one, e.g. to show a real connection form.</param>
    public static TestServices Create(SourceSchema? schema = null, params IDatabaseProvider[] more) => new(schema ?? DefaultSchema(), more);

    public static SourceSchema DefaultSchema()
    {
        static TableDefinition Keyed(string name) =>
            Table("dbo", name, Id(), Column("Name", DataTypeKind.Text)).WithPrimaryKey($"PK_{name}", "Id");

        var d = Table("dbo", "D", Id(), Column("CId", DataTypeKind.Int32)).WithPrimaryKey("PK_D", "Id").WithForeignKey("FK_D_C", "CId", C, "Id");
        return Source(Keyed("A"), Keyed("B"), Keyed("C"), d);
    }

    /// <summary>Every fake records I/O that reaches it on the fake UI thread.</summary>
    public void ForbidUiThread() => ForbidThread(() => UiThreadContext.IsOnUiThread);

    /// <summary>Every fake records I/O that arrives on a thread for which <paramref name="isForbidden"/> is true.</summary>
    public void ForbidThread(Func<bool> isForbidden)
    {
        SourceDb.Thread.IsForbidden = isForbidden;
        TargetDb.Thread.IsForbidden = isForbidden;
        Credentials.Thread.IsForbidden = isForbidden;
        Profiles.Thread.IsForbidden = isForbidden;
        Settings.Thread.IsForbidden = isForbidden;
        Reports.Thread.IsForbidden = isForbidden;
    }
}
