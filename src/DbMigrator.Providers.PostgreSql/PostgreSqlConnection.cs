using DbMigrator.Core.Diagnostics;
using DbMigrator.Core.Providers;
using Npgsql;

namespace DbMigrator.Providers.PostgreSql;

/// <summary>PostgreSQL's connection form and connection check.</summary>
internal static class PostgreSqlConnection
{
    private const string SslMode = "SSL Mode";

    private const string CheckQuery = """
        SELECT current_database(), current_user,
               has_database_privilege(current_database(), 'CREATE'),
               (SELECT has_schema_privilege(n.oid, 'CREATE') FROM pg_catalog.pg_namespace n WHERE n.nspname = 'public')
        """;

    public static ConnectionStringFormat Format { get; } = new(() => new NpgsqlConnectionStringBuilder(),
    [
        new("Host", "Host", ConnectionFieldKind.Text) { Role = ConnectionFieldRole.Server, IsRequired = true, Placeholder = "pg01 or 10.0.0.5" },
        new("Port", "Port", ConnectionFieldKind.Integer) { Role = ConnectionFieldRole.Port, DefaultValue = "5432", Minimum = 1, Maximum = 65535 },
        new("Database", "Database", ConnectionFieldKind.Text) { Role = ConnectionFieldRole.Database, IsRequired = true },
        new("Username", "User", ConnectionFieldKind.Text) { Role = ConnectionFieldRole.User, IsRequired = true },
        new("Password", "Password", ConnectionFieldKind.Secret),
        new(SslMode, "SSL", ConnectionFieldKind.Choice)
        {
            DefaultValue = "Prefer",
            Choices =
            [
                new("Disable", "Disable"),
                new("Allow", "Allow"),
                new("Prefer", "Prefer (default)"),
                new("Require", "Require"),
                new("VerifyCA", "Verify CA"),
                new("VerifyFull", "Verify CA and host name"),
            ],
        },
        new("Root Certificate", "CA certificate", ConnectionFieldKind.FilePath)
        {
            FileExtensions = [".crt", ".pem"],
            VisibleWhen = new(SslMode, ["VerifyCA", "VerifyFull"]),
        },
        new("SSL Certificate", "Client certificate", ConnectionFieldKind.FilePath) { IsAdvanced = true, FileExtensions = [".crt", ".pem"] },
        new("SSL Key", "Client key", ConnectionFieldKind.FilePath) { IsAdvanced = true, FileExtensions = [".key", ".pem"] },
        new("SSL Password", "Client key password", ConnectionFieldKind.Secret) { IsAdvanced = true },
        new("Timeout", "Connect timeout (s)", ConnectionFieldKind.Integer) { IsAdvanced = true, DefaultValue = "15", Minimum = 1, Maximum = 1024 },
        new("Application Name", "Application name", ConnectionFieldKind.Text) { IsAdvanced = true, DefaultValue = "DbMigrator" },
    ]);

    public static async Task<ConnectionCheck> CheckAsync(string connectionString, ConnectionRole role, CancellationToken cancellationToken)
    {
        if (role != ConnectionRole.Target)
        {
            throw new NotSupportedException("PostgreSQL cannot be a migration source yet.");
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(CheckQuery, connection);
        command.CommandTimeout = 30;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var database = reader.GetString(0);
        var user = reader.GetString(1);
        var canCreateSchemas = reader.GetBoolean(2);
        bool? canCreateInPublic = reader.IsDBNull(3) ? null : reader.GetBoolean(3);

        var findings = new List<MigrationIssue>();
        if (!canCreateSchemas)
        {
            findings.Add(new(IssueSeverity.Warning, IssueCodes.MissingPrivilege, database,
                $"{user} cannot create schemas in {database}; the run works only if every target schema already exists."));
        }

        if (canCreateInPublic == false)
        {
            findings.Add(new(IssueSeverity.Warning, IssueCodes.MissingPrivilege, $"{database}.public",
                $"{user} cannot create tables in public, where tables from the source's default schema go unless you map it elsewhere."));
        }

        if (string.Equals(database, "postgres", StringComparison.Ordinal))
        {
            findings.Add(new(IssueSeverity.Info, IssueCodes.SystemDatabase, database,
                "postgres is the default maintenance database; most migrations go into a database of their own."));
        }

        return new ConnectionCheck($"PostgreSQL {connection.ServerVersion}", database, user, findings);
    }
}
