using DbMigrator.Core.Execution;
using DbMigrator.Core.Planning;
using DbMigrator.Providers.PostgreSql;
using DbMigrator.Providers.SqlServer;

namespace DbMigrator.IntegrationTests;

/// <summary>Runs the engine from SQL Server to PostgreSQL the way the CLI and the app do.</summary>
internal static class Migrations
{
    public static async Task<PreparedMigration> PrepareAsync(string source, string target, MigrationOptions? options = null)
    {
        await using var sourceDb = new SqlServerProvider().OpenSource(source);
        await using var targetDb = new PostgreSqlProvider().OpenTarget(target);
        return await MigrationEngine.PrepareAsync(sourceDb, targetDb, options ?? new MigrationOptions(), TestContext.Current.CancellationToken);
    }

    public static async Task<MigrationReport> RunAsync(string source, string target, MigrationOptions? options = null, IProgress<MigrationEvent>? progress = null)
    {
        await using var sourceDb = new SqlServerProvider().OpenSource(source);
        await using var targetDb = new PostgreSqlProvider().OpenTarget(target);
        var cancellationToken = TestContext.Current.CancellationToken;
        var migration = await MigrationEngine.PrepareAsync(sourceDb, targetDb, options ?? new MigrationOptions(), cancellationToken);
        Assert.False(migration.Plan.HasErrors, string.Join(Environment.NewLine, migration.Plan.Issues));
        return await MigrationEngine.ExecuteAsync(migration, sourceDb, targetDb, progress, cancellationToken);
    }

    /// <summary>Fails with every table and step error, rather than a bare "expected true".</summary>
    public static void AssertSucceeded(MigrationReport report)
    {
        var problems = new[] { report.FatalError }.OfType<string>()
            .Concat(report.Tables.Where(t => t.Error is not null).Select(t => $"{t.Table.Source.Name}: {t.Error}"))
            .Concat(report.Steps.Where(s => !s.Succeeded).Select(s => $"{s.Step.Description}: {s.Error}"));
        Assert.True(report.Succeeded, string.Join(Environment.NewLine, problems));
    }
}
