using DbMigrator.Core.Providers;

namespace DbMigrator.Providers.SqlServer;

public sealed class SqlServerProvider : IDatabaseProvider
{
    public const string ProviderId = "sqlserver";

    public string Id => ProviderId;

    public string DisplayName => "SQL Server";

    public bool SupportsSource => true;

    public bool SupportsTarget => false;

    public ISourceDatabase OpenSource(string connectionString) => new SqlServerSourceDatabase(connectionString);

    public ITargetDatabase OpenTarget(string connectionString) =>
        throw new NotSupportedException("SQL Server cannot be a migration target yet.");

    public ISqlDialect CreateDialect() =>
        throw new NotSupportedException("SQL Server cannot be a migration target yet.");

    public ConnectionStringFormat Connection => SqlServerConnection.Format;

    public Task<ConnectionCheck> CheckConnectionAsync(string connectionString, ConnectionRole role, CancellationToken cancellationToken) =>
        SqlServerConnection.CheckAsync(connectionString, role, cancellationToken);
}
