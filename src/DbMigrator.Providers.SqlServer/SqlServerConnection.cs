using DbMigrator.Core.Diagnostics;
using DbMigrator.Core.Providers;
using Microsoft.Data.SqlClient;

namespace DbMigrator.Providers.SqlServer;

/// <summary>SQL Server's connection form and connection check.</summary>
internal static class SqlServerConnection
{
    private const string IntegratedSecurity = "Integrated Security";

    private static readonly ConnectionFieldCondition UsesSqlLogin = new(IntegratedSecurity, ["False"]);

    private static readonly string[] SystemDatabases = ["master", "model", "msdb", "tempdb"];

    private const string CheckQuery = """
        SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128)), CAST(SERVERPROPERTY('Edition') AS nvarchar(128)),
               DB_NAME(), SUSER_SNAME(),
               HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'VIEW DEFINITION'),
               HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'SELECT');
        """;

    public static ConnectionStringFormat Format { get; } = new(() => new SqlConnectionStringBuilder(),
    [
        new("Data Source", "Server", ConnectionFieldKind.Text)
        {
            Role = ConnectionFieldRole.Server,
            IsRequired = true,
            Placeholder = @"sql01, sql01\INSTANCE or sql01,1433",
        },
        new("Initial Catalog", "Database", ConnectionFieldKind.Text) { Role = ConnectionFieldRole.Database, IsRequired = true },
        new(IntegratedSecurity, "Windows authentication (integrated / Kerberos)", ConnectionFieldKind.Toggle) { DefaultValue = "False" },
        new("User ID", "User", ConnectionFieldKind.Text) { Role = ConnectionFieldRole.User, IsRequired = true, VisibleWhen = UsesSqlLogin },
        new("Password", "Password", ConnectionFieldKind.Secret) { VisibleWhen = UsesSqlLogin },
        new("Encrypt", "Encryption", ConnectionFieldKind.Choice)
        {
            DefaultValue = "True",
            Choices = [new("True", "Mandatory (default)"), new("Strict", "Strict (TDS 8)"), new("False", "Optional")],
        },
        new("Trust Server Certificate", "Trust server certificate", ConnectionFieldKind.Toggle)
        {
            DefaultValue = "False",
            Help = "Skips certificate validation. Use only on networks you trust.",
        },
        new("Application Intent", "Application intent", ConnectionFieldKind.Choice)
        {
            IsAdvanced = true,
            Choices = [new("ReadWrite", "Read/write"), new("ReadOnly", "Read-only")],
            Help = "Read-only reads from an Availability Group readable secondary.",
        },
        new("Connect Timeout", "Connect timeout (s)", ConnectionFieldKind.Integer) { IsAdvanced = true, DefaultValue = "15", Minimum = 1, Maximum = 600 },
        new("Application Name", "Application name", ConnectionFieldKind.Text) { IsAdvanced = true, DefaultValue = "DbMigrator" },
    ]);

    public static async Task<ConnectionCheck> CheckAsync(string connectionString, ConnectionRole role, CancellationToken cancellationToken)
    {
        if (role != ConnectionRole.Source)
        {
            throw new NotSupportedException("SQL Server cannot be a migration target yet.");
        }

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(CheckQuery, connection);
        command.CommandTimeout = 30;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var version = $"SQL Server {reader.GetString(0)} ({reader.GetString(1)})";
        var database = reader.GetString(2);
        var user = reader.IsDBNull(3) ? "" : reader.GetString(3);
        var canViewDefinitions = !reader.IsDBNull(4) && reader.GetInt32(4) == 1;
        var canSelect = !reader.IsDBNull(5) && reader.GetInt32(5) == 1;

        var findings = new List<MigrationIssue>();
        if (!canViewDefinitions)
        {
            findings.Add(new(IssueSeverity.Warning, IssueCodes.LimitedCatalogAccess, database,
                $"This login lacks VIEW DEFINITION on {database}, so tables it has no permission on are invisible and will not be migrated. Grant VIEW DEFINITION on the database to be sure every table is listed."));
        }

        if (!canSelect)
        {
            findings.Add(new(IssueSeverity.Warning, IssueCodes.MissingPrivilege, database,
                $"This login has no database-wide SELECT permission on {database}; tables it cannot read will fail to copy."));
        }

        if (SystemDatabases.Contains(database, StringComparer.OrdinalIgnoreCase))
        {
            findings.Add(new(IssueSeverity.Warning, IssueCodes.SystemDatabase, database,
                $"{database} is a SQL Server system database; you probably meant to pick a user database."));
        }

        return new ConnectionCheck(version, database, user, findings);
    }
}
