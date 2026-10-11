using DbMigrator.Core.Execution;
using DbMigrator.Core.Planning;
using DbMigrator.Core.Providers;
using DbMigrator.Core.Schema;
using DbMigrator.Desktop.Testing;
using DbMigrator.Desktop.ViewModels;
using DbMigrator.Providers.PostgreSql;

namespace DbMigrator.Desktop.Tests;

/// <summary>Plans over the four test tables, made by the real planner.</summary>
internal static class Plans
{
    public static readonly QualifiedName TargetA = new("public", "a");
    public static readonly QualifiedName TargetB = new("public", "b");
    public static readonly QualifiedName TargetC = new("public", "c");
    public static readonly QualifiedName TargetD = new("public", "d");

    /// <param name="estimate">Catalog row estimate given to every table; null leaves them unknown.</param>
    /// <param name="decisions">Existing target tables and what to do with them, keyed by source table.</param>
    public static PreparedMigration Prepare(long? estimate = 1000, IReadOnlyDictionary<QualifiedName, TableExistsAction>? decisions = null, MigrationOptions? options = null)
    {
        var schema = TestServices.DefaultSchema();
        schema = schema with { Tables = schema.Tables.Select(t => t with { EstimatedRowCount = estimate }).ToList() };
        decisions ??= new Dictionary<QualifiedName, TableExistsAction>();
        var existing = decisions.Keys.Select(k => new QualifiedName("public", k.Name.ToLowerInvariant())).ToList();
        var info = new TargetInfo("test", new PostgreSqlDialect(new Version(17, 0)), new TargetState(existing, ["public"]));
        options ??= new MigrationOptions();
        return MigrationEngine.Prepare(schema, info, options with { TableExistsOverrides = decisions });
    }

    public static TablePlan Table(this PreparedMigration migration, QualifiedName source) =>
        migration.Plan.Tables.First(t => t.Source.Name == source);

    public static TimedEvent At(this MigrationEvent e, DateTimeOffset at) => new(e, at);
}
