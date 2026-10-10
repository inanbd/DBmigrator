using System.Data.Common;
using DbMigrator.Core.Diagnostics;
using DbMigrator.Core.Providers;
using DbMigrator.Providers;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace DbMigrator.IntegrationTests;

public class ConnectionCheckTests(DatabaseServers servers)
{
    private static IDatabaseProvider SqlServer => BuiltInProviders.Catalog.Get("sqlserver");

    private static IDatabaseProvider PostgresProvider => BuiltInProviders.Catalog.Get("postgresql");

    [Fact]
    public async Task Checks_a_source_without_reading_its_catalog()
    {
        var source = await servers.CreateSqlServerDatabaseAsync("shop.sql");

        var check = await SqlServer.CheckConnectionAsync(source, ConnectionRole.Source, TestContext.Current.CancellationToken);

        Assert.StartsWith("SQL Server ", check.ServerVersion);
        Assert.Equal(new SqlConnectionStringBuilder(source).InitialCatalog, check.Database);
        Assert.Equal("sa", check.User);
        Assert.Empty(check.Findings);
    }

    [Fact]
    public async Task Checks_a_target()
    {
        var target = await servers.CreatePostgresDatabaseAsync();

        var check = await PostgresProvider.CheckConnectionAsync(target, ConnectionRole.Target, TestContext.Current.CancellationToken);

        Assert.StartsWith("PostgreSQL 17", check.ServerVersion);
        Assert.Equal(new NpgsqlConnectionStringBuilder(target).Database, check.Database);
        Assert.Empty(check.Findings);
    }

    [Fact]
    public async Task A_wrong_password_is_the_drivers_error()
    {
        var source = new SqlConnectionStringBuilder(await servers.CreateSqlServerDatabaseAsync()) { Password = "wrong" }.ConnectionString;

        await Assert.ThrowsAnyAsync<DbException>(() =>
            SqlServer.CheckConnectionAsync(source, ConnectionRole.Source, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Engines_refuse_roles_they_do_not_support()
    {
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            SqlServer.CheckConnectionAsync("Data Source=x", ConnectionRole.Target, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            PostgresProvider.CheckConnectionAsync("Host=x", ConnectionRole.Source, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_login_without_view_definition_is_warned_that_tables_are_hidden()
    {
        var admin = await servers.CreateSqlServerDatabaseAsync("shop.sql");
        var login = "limited_" + Guid.NewGuid().ToString("N")[..8];
        const string password = "Limited_Passw0rd!";
        await using (var connection = new SqlConnection(admin))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var setup = new SqlCommand(
                $"CREATE LOGIN [{login}] WITH PASSWORD = '{password}', CHECK_POLICY = OFF; CREATE USER [{login}] FOR LOGIN [{login}]; GRANT SELECT ON dbo.Customers TO [{login}];",
                connection);
            await setup.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var limited = new SqlConnectionStringBuilder(admin) { UserID = login, Password = password }.ConnectionString;

        var check = await SqlServer.CheckConnectionAsync(limited, ConnectionRole.Source, TestContext.Current.CancellationToken);
        await using var source = SqlServer.OpenSource(limited);
        var schema = await source.ReadSchemaAsync(TestContext.Current.CancellationToken);

        Assert.Contains(check.Findings, f => f is { Code: IssueCodes.LimitedCatalogAccess, Severity: IssueSeverity.Warning });
        // The warning is real: this login sees one of the three tables.
        Assert.Equal(["Customers"], schema.Tables.Select(t => t.Name.Name));
    }

    [Fact]
    public async Task A_target_login_that_cannot_create_is_warned()
    {
        var admin = await servers.CreatePostgresDatabaseAsync();
        var role = "reader_" + Guid.NewGuid().ToString("N")[..8];
        await Postgres.ExecuteAsync(admin, $"CREATE ROLE {role} LOGIN PASSWORD 'reader'");
        var limited = new NpgsqlConnectionStringBuilder(admin) { Username = role, Password = "reader" }.ConnectionString;

        var check = await PostgresProvider.CheckConnectionAsync(limited, ConnectionRole.Target, TestContext.Current.CancellationToken);

        Assert.Equal(role, check.User);
        Assert.Equal(2, check.Findings.Count(f => f.Code == IssueCodes.MissingPrivilege));
    }
}
