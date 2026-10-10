using DbMigrator.Core.Diagnostics;
using DbMigrator.Core.Execution;
using DbMigrator.Core.Planning;
using DbMigrator.Core.Providers;
using DbMigrator.Core.Schema;
using DbMigrator.Providers.PostgreSql;

namespace DbMigrator.Tests.Execution;

/// <summary>The plan and reports whose text was captured from the CLI before the report code moved into Core.</summary>
internal static class GoldenData
{
    public sealed record Set(PreparedMigration Migration, MigrationReport Success, MigrationReport Problems, MigrationReport Fatal);

    public static Set Create()
    {
        ColumnDefinition Col(string n, DataTypeKind k, bool nullable = true) => new() { Name = n, Type = new DataType(k), NativeType = k.ToString().ToLowerInvariant(), IsNullable = nullable };
        var customers = new TableDefinition { Name = new("dbo", "Customers"), Columns = [Col("CustomerID", DataTypeKind.Int32, false) with { Identity = new(1, 1) }, Col("Name", DataTypeKind.Text, false)], PrimaryKey = new("PK_Customers", ["CustomerID"]), EstimatedRowCount = 1200 };
        var orders = new TableDefinition { Name = new("sales", "Orders"), Columns = [Col("OrderID", DataTypeKind.Int64, false), Col("CustomerID", DataTypeKind.Int32, false), Col("Placed", DataTypeKind.DateTimeOffset) with { Type = new DataType(DataTypeKind.DateTimeOffset) { Precision = 7 } }], PrimaryKey = new("PK_Orders", ["OrderID"]), ForeignKeys = [new ForeignKeyDefinition { Name = "FK_Orders_Customers", Columns = ["CustomerID"], ReferencedTable = new("dbo", "Customers"), ReferencedColumns = ["CustomerID"] }], EstimatedRowCount = 3_500_000 };
        var audit = new TableDefinition { Name = new("dbo", "Audit"), Columns = [Col("Message", DataTypeKind.Text)] };
        var wide = new TableDefinition { Name = new("dbo", "Wide"), Columns = Enumerable.Range(1, 7).Select(i => Col($"T{i}", DataTypeKind.Time) with { Type = new DataType(DataTypeKind.Time) { Precision = 7 } }).ToList() };
        var schema = new SourceSchema { ProviderId = "sqlserver", ServerVersion = "SQL Server 16.0 (Developer)", DefaultSchema = "dbo", Tables = [customers, orders, audit, wide], Issues = [new(IssueSeverity.Info, IssueCodes.CaseSensitivity, "Shop", "The source compares text case-insensitively.")] };
        var target = new TargetInfo("PostgreSQL 17.2", new PostgreSqlDialect(new Version(17, 2)), new TargetState([new("public", "audit")], ["public"]));
        var options = new MigrationOptions { IfTableExists = TableExistsAction.Truncate };
        var migration = MigrationEngine.Prepare(schema, target, options);
        var plan = migration.Plan;
        TableResult R(int i, TableStatus s, long rows, double seconds, string? error = null, long? sc = null, long? tc = null) => new() { Table = plan.Tables[i], Status = s, RowsCopied = rows, Elapsed = TimeSpan.FromSeconds(seconds), Error = error, SourceRowCount = sc, TargetRowCount = tc };
        var steps = migration.Script.AfterData.Select(s => new StepResult(s, TimeSpan.FromSeconds(1))).ToList();
        var success = new MigrationReport { Plan = plan, Tables = [R(0, TableStatus.Copied, 1200, 0.4, sc: 1200, tc: 1200), R(1, TableStatus.Copied, 0, 0.1, sc: 0, tc: 0), R(2, TableStatus.Copied, 3_500_000, 75.5, sc: 3_500_000, tc: 3_500_000), R(3, TableStatus.Copied, 10, 0.1, sc: 10, tc: 10)], Steps = steps, Issues = [.. plan.Issues, new MigrationIssue(IssueSeverity.Warning, IssueCodes.NulCharacterRemoved, "dbo.Audit.Message", "PostgreSQL text cannot contain NUL (\\0) characters; they were removed from 3 value(s).")], Elapsed = TimeSpan.FromSeconds(83.25) };
        var problemSteps = steps.Select((s, i) => i == 1 ? s with { Error = "relation \"pk_orders\" already exists" } : s).ToList();
        var problems = success with { Tables = [R(0, TableStatus.Copied, 1200, 0.4, sc: 1200, tc: 1200), R(1, TableStatus.Failed, 0, 0.2, "Failed after reading 17 row(s); none were kept. duplicate key"), R(2, TableStatus.CountMismatch, 3_500_000, 75.5, "The source has 3,500,001 row(s), 3,500,000 were copied, and the target now has 3,500,000.", 3_500_001, 3_500_000), R(3, TableStatus.NotCopied, 0, 0)], Steps = problemSteps, Elapsed = TimeSpan.FromMinutes(61.5) };
        var fatal = new MigrationReport { Plan = plan, Tables = [], Steps = [], Issues = plan.Issues, Elapsed = TimeSpan.FromSeconds(0.5), FatalError = "Preparing the target tables (drop, truncate, create) failed: permission denied for schema public" };
        return new Set(migration, success, problems, fatal);
    }
}
