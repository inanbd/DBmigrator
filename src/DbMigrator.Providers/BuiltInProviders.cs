using DbMigrator.Core.Providers;
using DbMigrator.Providers.PostgreSql;
using DbMigrator.Providers.SqlServer;

namespace DbMigrator.Providers;

/// <summary>The engines both the CLI and the desktop app offer. New engines are added here once.</summary>
public static class BuiltInProviders
{
    public static ProviderCatalog Catalog { get; } = new([new SqlServerProvider(), new PostgreSqlProvider()]);
}
