using DbMigrator.Core.Execution;
using DbMigrator.Core.Planning;
using DbMigrator.Core.Schema;
using DbMigrator.TestSupport;
using static DbMigrator.TestSupport.SchemaBuilder;

namespace DbMigrator.Tests.Execution;

/// <summary>
/// Four tables migrated through the real engine into the in-memory target:
/// dbo.A already exists and is truncated, dbo.B exists and is dropped and recreated,
/// dbo.C and dbo.D are created, and D has a foreign key to C.
/// </summary>
internal sealed class EngineHarness
{
    public static readonly QualifiedName A = new("dbo", "A");
    public static readonly QualifiedName B = new("dbo", "B");
    public static readonly QualifiedName C = new("dbo", "C");
    public static readonly QualifiedName D = new("dbo", "D");

    private EngineHarness(InMemorySourceDatabase source, InMemoryTargetDatabase target, PreparedMigration migration)
    {
        Source = source;
        Target = target;
        Migration = migration;
    }

    public InMemorySourceDatabase Source { get; }

    public InMemoryTargetDatabase Target { get; }

    public PreparedMigration Migration { get; }

    public static async Task<EngineHarness> CreateAsync(MigrationOptions? options = null)
    {
        static TableDefinition Keyed(string name) =>
            Table("dbo", name, Id(), Column("Name", DataTypeKind.Text)).WithPrimaryKey($"PK_{name}", "Id");

        var d = Table("dbo", "D", Id(), Column("CId", DataTypeKind.Int32)).WithPrimaryKey("PK_D", "Id").WithForeignKey("FK_D_C", "CId", C, "Id");
        var source = new InMemorySourceDatabase(Source(Keyed("A"), Keyed("B"), Keyed("C"), d)) { DefaultRowCount = 5 };
        var target = new InMemoryTargetDatabase();
        target.ExistingTables.Add(new QualifiedName("public", "a"));
        target.ExistingTables.Add(new QualifiedName("public", "b"));
        options ??= new MigrationOptions
        {
            TableExistsOverrides = new Dictionary<QualifiedName, TableExistsAction>
            {
                [A] = TableExistsAction.Truncate,
                [B] = TableExistsAction.DropAndRecreate,
            },
        };

        var schema = await source.ReadSchemaAsync(CancellationToken.None);
        var info = await target.InspectAsync(CancellationToken.None);
        return new EngineHarness(source, target, MigrationEngine.Prepare(schema, info, options));
    }

    public Task<MigrationReport> RunAsync(IProgress<MigrationEvent>? progress = null, CancellationToken cancellationToken = default) =>
        MigrationEngine.ExecuteAsync(Migration, Source, Target, progress, cancellationToken);
}

/// <summary>Reports synchronously on the engine's thread, unlike System.Progress.</summary>
internal sealed class SyncProgress(Action<MigrationEvent> onEvent) : IProgress<MigrationEvent>
{
    public void Report(MigrationEvent value) => onEvent(value);
}
