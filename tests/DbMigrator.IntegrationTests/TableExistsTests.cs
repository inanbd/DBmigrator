using DbMigrator.Core.Execution;
using DbMigrator.Core.Planning;
using DbMigrator.Core.Schema;
using DbMigrator.Providers.PostgreSql;
using DbMigrator.Providers.SqlServer;

namespace DbMigrator.IntegrationTests;

/// <summary>Re-running into a target that already holds the tables, with each choice of what to do about them.</summary>
public class TableExistsTests(DatabaseServers servers)
{
    private static readonly QualifiedName Customers = new("dbo", "Customers");
    private static readonly QualifiedName Orders = new("dbo", "Orders");
    private static readonly QualifiedName EventLog = new("dbo", "EventLog");

    private async Task<(string Source, string Target)> MigratedOnceAsync()
    {
        var source = await servers.CreateSqlServerDatabaseAsync("shop.sql");
        var target = await servers.CreatePostgresDatabaseAsync();
        Migrations.AssertSucceeded(await Migrations.RunAsync(source, target));
        return (source, target);
    }

    [Fact]
    public async Task Fail_stops_before_changing_anything()
    {
        var (source, target) = await MigratedOnceAsync();
        await Postgres.ExecuteAsync(target, "INSERT INTO public.eventlog VALUES ('kept')");

        var migration = await Migrations.PrepareAsync(source, target);

        Assert.True(migration.Plan.HasErrors);
        Assert.Equal([Customers, EventLog, Orders], migration.Plan.UnresolvedConflicts.Select(t => t.Source.Name).Order(QualifiedNameComparer.Ordinal));
        await using var sourceDb = new SqlServerProvider().OpenSource(source);
        await using var targetDb = new PostgreSqlProvider().OpenTarget(target);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MigrationEngine.ExecuteAsync(migration, sourceDb, targetDb, null, TestContext.Current.CancellationToken));
        Assert.Equal(3, await Postgres.CountAsync(target, "public.eventlog"));
    }

    [Fact]
    public async Task Drop_recreates_tables_and_their_constraints()
    {
        var (source, target) = await MigratedOnceAsync();
        await Postgres.ExecuteAsync(target, "INSERT INTO public.customers (name) VALUES ('extra')");

        var report = await Migrations.RunAsync(source, target, new MigrationOptions { IfTableExists = TableExistsAction.DropAndRecreate });

        Migrations.AssertSucceeded(report);
        Assert.Equal(2, await Postgres.CountAsync(target, "public.customers"));
        Assert.Equal(1, await Postgres.ScalarAsync<long>(target, "SELECT count(*) FROM pg_constraint WHERE conname = 'fk_orders_customers'"));
    }

    [Fact]
    public async Task Truncate_replaces_the_rows_and_keeps_the_tables()
    {
        var (source, target) = await MigratedOnceAsync();
        await Postgres.ExecuteAsync(target, "INSERT INTO public.eventlog VALUES ('extra'); COMMENT ON TABLE public.eventlog IS 'kept'");

        var report = await Migrations.RunAsync(source, target, new MigrationOptions { IfTableExists = TableExistsAction.Truncate });

        Migrations.AssertSucceeded(report);
        Assert.Equal(2, await Postgres.CountAsync(target, "public.eventlog"));
        Assert.Equal(3, await Postgres.CountAsync(target, "public.orders"));
        Assert.Equal("kept", await Postgres.ScalarAsync<string>(target, "SELECT obj_description('public.eventlog'::regclass)"));
        Assert.Equal(4, await Postgres.ScalarAsync<int>(target, "INSERT INTO public.orders (customerid, amount) VALUES (1, 1) RETURNING orderid"));
    }

    [Fact]
    public async Task Append_adds_rows_and_per_table_choices_apply()
    {
        var (source, target) = await MigratedOnceAsync();

        var report = await Migrations.RunAsync(source, target, new MigrationOptions
        {
            IfTableExists = TableExistsAction.Skip,
            TableExistsOverrides = new Dictionary<QualifiedName, TableExistsAction> { [EventLog] = TableExistsAction.Append },
        });

        Migrations.AssertSucceeded(report);
        Assert.Equal(4, await Postgres.CountAsync(target, "public.eventlog"));
        Assert.Equal(2, await Postgres.CountAsync(target, "public.customers"));
        Assert.Equal(TableStatus.NotCopied, report.Tables.Single(t => t.Table.Source.Name == Customers).Status);
    }

    [Fact]
    public async Task A_table_that_fails_to_load_is_rolled_back_and_reported()
    {
        var (source, target) = await MigratedOnceAsync();

        // Appending customers again repeats their primary keys.
        var report = await Migrations.RunAsync(source, target, new MigrationOptions
        {
            IfTableExists = TableExistsAction.Skip,
            TableExistsOverrides = new Dictionary<QualifiedName, TableExistsAction> { [Customers] = TableExistsAction.Append },
        });

        Assert.False(report.Succeeded);
        var customers = report.Tables.Single(t => t.Table.Source.Name == Customers);
        Assert.Equal(TableStatus.Failed, customers.Status);
        Assert.Contains("duplicate key", customers.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, await Postgres.CountAsync(target, "public.customers"));
    }
}
