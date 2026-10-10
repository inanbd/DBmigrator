using System.Text.RegularExpressions;
using DbMigrator.IntegrationTests;
using Microsoft.Data.SqlClient;
using Npgsql;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(DatabaseServers))]

namespace DbMigrator.IntegrationTests;

/// <summary>
/// One SQL Server and one PostgreSQL server for the whole test run, started in Docker.
/// Set DBMIGRATOR_TEST_SQLSERVER and DBMIGRATOR_TEST_POSTGRES to admin connection strings
/// to use existing servers instead. Each test creates its own databases on them.
/// </summary>
public sealed partial class DatabaseServers : IAsyncLifetime
{
    private MsSqlContainer? _sqlServer;
    private PostgreSqlContainer? _postgres;

    public string SqlServerAdmin { get; private set; } = "";

    public string PostgresAdmin { get; private set; } = "";

    public async ValueTask InitializeAsync()
    {
        var sqlServer = Environment.GetEnvironmentVariable("DBMIGRATOR_TEST_SQLSERVER");
        var postgres = Environment.GetEnvironmentVariable("DBMIGRATOR_TEST_POSTGRES");
        var starting = new List<Task>();
        if (string.IsNullOrEmpty(sqlServer))
        {
            _sqlServer = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
            starting.Add(_sqlServer.StartAsync());
        }

        if (string.IsNullOrEmpty(postgres))
        {
            _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
            starting.Add(_postgres.StartAsync());
        }

        await Task.WhenAll(starting);
        SqlServerAdmin = string.IsNullOrEmpty(sqlServer) ? _sqlServer!.GetConnectionString() : sqlServer;
        PostgresAdmin = string.IsNullOrEmpty(postgres) ? _postgres!.GetConnectionString() : postgres;
    }

    public async ValueTask DisposeAsync()
    {
        if (_sqlServer is not null)
        {
            await _sqlServer.DisposeAsync();
        }

        if (_postgres is not null)
        {
            await _postgres.DisposeAsync();
        }
    }

    /// <summary>Creates a SQL Server database, runs <paramref name="seedFile"/> (from Sql/) in it, and returns its connection string.</summary>
    public async Task<string> CreateSqlServerDatabaseAsync(string? seedFile = null, string? seedSql = null)
    {
        var name = "src_" + Guid.NewGuid().ToString("N")[..12];
        await using (var connection = new SqlConnection(SqlServerAdmin))
        {
            await connection.OpenAsync();
            await using var create = new SqlCommand($"CREATE DATABASE [{name}]", connection);
            await create.ExecuteNonQueryAsync();
        }

        var connectionString = new SqlConnectionStringBuilder(SqlServerAdmin) { InitialCatalog = name }.ConnectionString;
        var script = seedFile is null ? seedSql : await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Sql", seedFile));
        if (script is not null)
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            foreach (var batch in GoSeparator().Split(script).Where(b => !string.IsNullOrWhiteSpace(b)))
            {
                await using var command = new SqlCommand(batch, connection) { CommandTimeout = 300 };
                await command.ExecuteNonQueryAsync();
            }
        }

        return connectionString;
    }

    /// <summary>Creates an empty PostgreSQL database and returns its connection string.</summary>
    public async Task<string> CreatePostgresDatabaseAsync()
    {
        var name = "dst_" + Guid.NewGuid().ToString("N")[..12];
        await using (var connection = new NpgsqlConnection(PostgresAdmin))
        {
            await connection.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
            await create.ExecuteNonQueryAsync();
        }

        return new NpgsqlConnectionStringBuilder(PostgresAdmin) { Database = name }.ConnectionString;
    }

    [GeneratedRegex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex GoSeparator();
}
