using DbMigrator.Core.Execution;
using DbMigrator.Core.Planning;
using DbMigrator.Core.Providers;
using DbMigrator.Core.Schema;

namespace DbMigrator.Desktop.ViewModels;

/// <summary>A connection the user has entered. The connection string lives in memory only.</summary>
public sealed record ResolvedConnection(
    IDatabaseProvider Provider,
    IReadOnlyDictionary<string, string> Values,
    string AdditionalSettings,
    string ConnectionString,
    string Description,
    Guid? ProfileId);

/// <param name="Path">The saved Markdown file, or null when saving failed.</param>
/// <param name="Error">Why saving failed.</param>
public sealed record ReportAutosave(string? Path, string? Error);

/// <summary>
/// What the wizard has learned and decided, and the engine calls behind it. Every call into the
/// engine or a provider goes through <see cref="BackgroundWork"/>, so the UI thread never runs it.
/// The source is read once per connection and the plan is re-made from that cached schema.
/// </summary>
public sealed class MigrationSession(ProviderCatalog providers, TimeProvider time) : IAsyncDisposable
{
    private ISourceDatabase? _sourceDb;
    private ITargetDatabase? _targetDb;
    private Dictionary<QualifiedName, TableExistsAction> _conflictChoices = [];

    public ProviderCatalog Providers => providers;

    public ResolvedConnection? Source { get; private set; }

    public ConnectionCheck? SourceCheck { get; private set; }

    public SourceSchema? Schema { get; private set; }

    public DateTimeOffset? SchemaReadAt { get; private set; }

    public ResolvedConnection? Target { get; private set; }

    public IDatabaseProvider? TargetProvider { get; private set; }

    public ConnectionCheck? TargetCheck { get; private set; }

    public TargetInfo? TargetInfo { get; private set; }

    /// <summary>No target connection: the wizard ends with a saved script.</summary>
    public bool ScriptOnly { get; private set; }

    public IReadOnlyCollection<QualifiedName> SelectedTables { get; private set; } = [];

    public NamingOptions Naming { get; private set; } = new();

    public bool CopyData { get; private set; } = true;

    public bool CreateIndexes { get; private set; } = true;

    public bool CreateForeignKeys { get; private set; } = true;

    public bool VerifyRowCounts { get; private set; } = true;

    /// <summary>What to do with each table that exists in the target, keyed by source table.</summary>
    public IReadOnlyDictionary<QualifiedName, TableExistsAction> ConflictChoices => _conflictChoices;

    public PreparedMigration? Prepared { get; set; }

    public MigrationReport? Report { get; set; }

    /// <summary>An unexpected failure of the run itself (not a table or step failure, which the report holds).</summary>
    public Exception? RunFailure { get; set; }

    /// <summary>When the last run started, for the report and its file name.</summary>
    public DateTimeOffset? RunStartedAt { get; set; }

    /// <summary>Where the last report was saved automatically, or why it was not.</summary>
    public ReportAutosave? Autosave { get; set; }

    // Each version goes up when its part of the session changes, so steps know when to rebuild.
    public int SourceVersion { get; private set; }

    public int TargetVersion { get; private set; }

    public int SelectionVersion { get; private set; }

    public int OptionsVersion { get; private set; }

    public static Task<ConnectionCheck> TestAsync(IDatabaseProvider provider, string connectionString, ConnectionRole role, CancellationToken cancellationToken) =>
        BackgroundWork.RunAsync(ct => provider.CheckConnectionAsync(connectionString, role, ct), cancellationToken);

    /// <summary>Checks the connection, then reads the source catalog. Replaces any previous source.</summary>
    public async Task ConnectSourceAsync(ResolvedConnection source, CancellationToken cancellationToken)
    {
        var (check, database, schema) = await BackgroundWork.RunAsync(async ct =>
        {
            var check = await source.Provider.CheckConnectionAsync(source.ConnectionString, ConnectionRole.Source, ct);
            var database = source.Provider.OpenSource(source.ConnectionString);
            try
            {
                return (check, database, await database.ReadSchemaAsync(ct));
            }
            catch
            {
                await database.DisposeAsync();
                throw;
            }
        }, cancellationToken);

        var previous = _sourceDb;
        _sourceDb = database;
        Source = source;
        SourceCheck = check;
        Schema = schema;
        SchemaReadAt = time.GetUtcNow();
        Prepared = null;
        ClearConflictChoices();
        SourceVersion++;
        await DisposeLaterAsync(previous, database);
    }

    /// <summary>Reads the catalog again over the same connection.</summary>
    public async Task RereadSourceAsync(CancellationToken cancellationToken)
    {
        var database = _sourceDb ?? throw new InvalidOperationException("Connect to the source first.");
        Schema = await BackgroundWork.RunAsync(ct => database.ReadSchemaAsync(ct), cancellationToken);
        SchemaReadAt = time.GetUtcNow();
        Prepared = null;
        SourceVersion++;
    }

    /// <summary>Checks the connection, then inspects the target. Replaces any previous target.</summary>
    public async Task ConnectTargetAsync(ResolvedConnection target, CancellationToken cancellationToken)
    {
        var (check, database, info) = await BackgroundWork.RunAsync(async ct =>
        {
            var check = await target.Provider.CheckConnectionAsync(target.ConnectionString, ConnectionRole.Target, ct);
            var database = target.Provider.OpenTarget(target.ConnectionString);
            try
            {
                return (check, database, await database.InspectAsync(ct));
            }
            catch
            {
                await database.DisposeAsync();
                throw;
            }
        }, cancellationToken);

        var previous = _targetDb;
        _targetDb = database;
        Target = target;
        TargetProvider = target.Provider;
        TargetCheck = check;
        TargetInfo = info;
        ScriptOnly = false;
        Prepared = null;

        // A decision such as "drop and recreate" was made for the tables of the previous target.
        ClearConflictChoices();
        TargetVersion++;
        await DisposeLaterAsync(previous, database);
    }

    /// <summary>No target connection: plan against an empty target of this engine, for a script.</summary>
    public async Task UseOfflineTargetAsync(IDatabaseProvider provider)
    {
        var previous = _targetDb;
        _targetDb = null;
        Target = null;
        TargetProvider = provider;
        TargetCheck = null;
        TargetInfo = new TargetInfo("not connected; assuming an empty target", provider.CreateDialect(), TargetState.Unknown);
        ScriptOnly = true;
        Prepared = null;
        ClearConflictChoices();
        TargetVersion++;
        await DisposeLaterAsync(previous, null);
    }

    /// <summary>Inspects the target again, e.g. after a run created tables in it.</summary>
    public async Task ReinspectTargetAsync(CancellationToken cancellationToken)
    {
        var target = _targetDb ?? throw new InvalidOperationException("Connect to the target first.");
        TargetInfo = await BackgroundWork.RunAsync(ct => target.InspectAsync(ct), cancellationToken);
        Prepared = null;
        TargetVersion++;
    }

    /// <summary>Forgets the last run's outcome.</summary>
    public void ClearRun()
    {
        Report = null;
        RunFailure = null;
        RunStartedAt = null;
        Autosave = null;
    }

    public void SetSelection(IEnumerable<QualifiedName> tables)
    {
        SelectedTables = tables.ToList();
        Prepared = null;
        SelectionVersion++;
    }

    public void SetOptions(NamingOptions naming, bool copyData, bool createIndexes, bool createForeignKeys, bool verifyRowCounts)
    {
        Naming = naming;
        CopyData = copyData;
        CreateIndexes = createIndexes;
        CreateForeignKeys = createForeignKeys;
        VerifyRowCounts = verifyRowCounts;
        Prepared = null;
        OptionsVersion++;
    }

    public void SetConflictChoice(QualifiedName table, TableExistsAction? action)
    {
        var choices = new Dictionary<QualifiedName, TableExistsAction>(_conflictChoices);
        if (action is { } chosen)
        {
            choices[table] = chosen;
        }
        else
        {
            choices.Remove(table);
        }

        _conflictChoices = choices;
    }

    public void ClearConflictChoices() => _conflictChoices = [];

    /// <summary>The options for the current choices. The only place options are made.</summary>
    public MigrationOptions BuildOptions() => new()
    {
        Tables = TableFilter.Exactly(SelectedTables),
        Naming = Naming,
        IfTableExists = TableExistsAction.Fail,
        TableExistsOverrides = _conflictChoices,
        CopyData = CopyData,
        CreateIndexes = CreateIndexes,
        CreateForeignKeys = CreateForeignKeys,
        VerifyRowCounts = VerifyRowCounts,
    };

    /// <summary>Plans from the cached source schema and target state; reads neither database.</summary>
    public Task<PreparedMigration> PlanAsync(MigrationOptions options, CancellationToken cancellationToken)
    {
        var schema = Schema ?? throw new InvalidOperationException("Connect to the source first.");
        var target = TargetInfo ?? throw new InvalidOperationException("Choose a target first.");
        return BackgroundWork.RunAsync(() => MigrationEngine.Prepare(schema, target, options), cancellationToken);
    }

    /// <summary>
    /// Right before running: inspects the target again and re-plans, so the plan that runs is
    /// the plan that was reviewed. Differences send the user back to review.
    /// </summary>
    public async Task<PreflightResult> PreflightAsync(PreparedMigration reviewed, CancellationToken cancellationToken)
    {
        var target = _targetDb ?? throw new InvalidOperationException("Connect to the target first.");
        var info = await BackgroundWork.RunAsync(ct => target.InspectAsync(ct), cancellationToken);
        TargetInfo = info;
        var current = await BackgroundWork.RunAsync(() => MigrationEngine.Prepare(reviewed.Source, info, reviewed.Options), cancellationToken);
        var result = new PreflightResult(current, PlanFingerprint.Diff(reviewed, current));
        if (result.Differences.Count > 0)
        {
            // The target changed: steps built from the old inspection rebuild on their next visit.
            Prepared = null;
            TargetVersion++;
        }

        return result;
    }

    /// <summary>Runs the migration. A cancelled run returns its partial report instead of throwing.</summary>
    public async Task<MigrationReport> ExecuteAsync(PreparedMigration migration, IProgress<MigrationEvent> progress, CancellationToken cancellationToken)
    {
        var source = _sourceDb ?? throw new InvalidOperationException("Connect to the source first.");
        var target = _targetDb ?? throw new InvalidOperationException("Connect to the target first.");
        try
        {
            // CancellationToken.None: a run cancelled before it starts must still produce a report.
            return await BackgroundWork.RunAsync(ct => MigrationEngine.ExecuteAsync(migration, source, target, progress, cancellationToken), CancellationToken.None);
        }
        catch (MigrationCancelledException cancelled)
        {
            return cancelled.PartialReport;
        }
    }

    public static Task<string> BuildScriptTextAsync(PreparedMigration migration, CancellationToken cancellationToken) =>
        BackgroundWork.RunAsync(() => migration.Script.ToText(migration.Plan), cancellationToken);

    public async ValueTask DisposeAsync()
    {
        var source = _sourceDb;
        var target = _targetDb;
        _sourceDb = null;
        _targetDb = null;
        await BackgroundWork.RunAsync(async _ =>
        {
            if (source is not null)
            {
                await source.DisposeAsync();
            }

            if (target is not null)
            {
                await target.DisposeAsync();
            }
        }, CancellationToken.None);
    }

    private static Task DisposeLaterAsync(IAsyncDisposable? previous, IAsyncDisposable? current) =>
        previous is null || ReferenceEquals(previous, current)
            ? Task.CompletedTask
            : BackgroundWork.RunAsync(async _ => await previous.DisposeAsync(), CancellationToken.None);
}
