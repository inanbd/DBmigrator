using DbMigrator.Core.Providers;

namespace DbMigrator.Providers.PostgreSql;

public sealed class PostgreSqlProvider : IDatabaseProvider
{
    public const string ProviderId = "postgresql";

    public string Id => ProviderId;

    public string DisplayName => "PostgreSQL";

    public bool SupportsSource => false;

    public bool SupportsTarget => true;

    public ISourceDatabase OpenSource(string connectionString) =>
        throw new NotSupportedException("PostgreSQL cannot be a migration source yet.");

    public ITargetDatabase OpenTarget(string connectionString) => new PostgreSqlTargetDatabase(connectionString);

    public ISqlDialect CreateDialect() => new PostgreSqlDialect();

    public ConnectionStringFormat Connection => PostgreSqlConnection.Format;

    public Task<ConnectionCheck> CheckConnectionAsync(string connectionString, ConnectionRole role, CancellationToken cancellationToken) =>
        PostgreSqlConnection.CheckAsync(connectionString, role, cancellationToken);
}
