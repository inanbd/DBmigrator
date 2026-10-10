using System.Data.Common;
using DbMigrator.Core.Providers;
using DbMigrator.Providers.PostgreSql;

namespace DbMigrator.TestSupport;

/// <summary>
/// An engine whose databases live in memory. Its connection form has a server, database, user
/// and password; checks succeed unless <see cref="CheckError"/> is set.
/// </summary>
public sealed class InMemoryProvider(InMemorySourceDatabase source, InMemoryTargetDatabase target, string id = "memory") : IDatabaseProvider
{
    private int _openSourceCount;
    private int _openTargetCount;
    private int _checkCount;

    public static ConnectionStringFormat Format { get; } = new(() => new DbConnectionStringBuilder(),
    [
        new("Data Source", "Server", ConnectionFieldKind.Text) { Role = ConnectionFieldRole.Server, IsRequired = true },
        new("Initial Catalog", "Database", ConnectionFieldKind.Text) { Role = ConnectionFieldRole.Database, IsRequired = true },
        new("User ID", "User", ConnectionFieldKind.Text) { Role = ConnectionFieldRole.User },
        new("Password", "Password", ConnectionFieldKind.Secret),
    ]);

    public string Id => id;

    public string DisplayName => $"In-memory ({id})";

    public bool SupportsSource => true;

    public bool SupportsTarget => true;

    public InMemorySourceDatabase Source => source;

    public InMemoryTargetDatabase Target => target;

    public ConnectionStringFormat Connection => Format;

    /// <summary>When set, <see cref="CheckConnectionAsync"/> throws it, as a driver would for a bad password.</summary>
    public Func<Exception>? CheckError { get; set; }

    /// <summary>Findings the check reports, such as limited catalog access.</summary>
    public List<Core.Diagnostics.MigrationIssue> CheckFindings { get; } = [];

    /// <summary>Pauses the check until released, to test cancellation and busy states.</summary>
    public Gate? CheckGate { get; set; }

    public List<string> ConnectionStrings { get; } = [];

    public int OpenSourceCount => _openSourceCount;

    public int OpenTargetCount => _openTargetCount;

    public int CheckCount => _checkCount;

    public ThreadGuard Thread => source.Thread;

    public ISourceDatabase OpenSource(string connectionString)
    {
        source.Thread.Check();
        Interlocked.Increment(ref _openSourceCount);
        lock (ConnectionStrings)
        {
            ConnectionStrings.Add(connectionString);
        }

        return source;
    }

    public ITargetDatabase OpenTarget(string connectionString)
    {
        target.Thread.Check();
        Interlocked.Increment(ref _openTargetCount);
        lock (ConnectionStrings)
        {
            ConnectionStrings.Add(connectionString);
        }

        return target;
    }

    public ISqlDialect CreateDialect() => new PostgreSqlDialect(new Version(17, 0));

    public async Task<ConnectionCheck> CheckConnectionAsync(string connectionString, ConnectionRole role, CancellationToken cancellationToken)
    {
        source.Thread.Check();
        Interlocked.Increment(ref _checkCount);
        if (CheckGate is { } gate)
        {
            await gate.PassAsync(cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (CheckError is { } error)
        {
            throw error();
        }

        var values = Format.Parse(connectionString).Values;
        return new ConnectionCheck(
            "InMemory 1.0",
            values.GetValueOrDefault("Initial Catalog") ?? "",
            values.GetValueOrDefault("User ID") ?? "",
            CheckFindings.ToList());
    }
}
