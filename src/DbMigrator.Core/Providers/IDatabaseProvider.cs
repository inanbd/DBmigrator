using DbMigrator.Core.Diagnostics;
using DbMigrator.Core.Planning;
using DbMigrator.Core.Schema;

namespace DbMigrator.Core.Providers;

/// <summary>One database engine. Each engine can act as a source, a target, or both.</summary>
public interface IDatabaseProvider
{
    /// <summary>Short stable id used on the command line, e.g. <c>sqlserver</c>.</summary>
    string Id { get; }

    string DisplayName { get; }

    bool SupportsSource { get; }

    bool SupportsTarget { get; }

    ISourceDatabase OpenSource(string connectionString);

    ITargetDatabase OpenTarget(string connectionString);

    /// <summary>The target dialect without connecting, for generating scripts offline.</summary>
    ISqlDialect CreateDialect();

    /// <summary>How front ends describe, build, parse and redact this engine's connection strings.</summary>
    ConnectionStringFormat Connection { get; }

    /// <summary>
    /// Opens one connection and reports the server, database and login, plus findings about
    /// privileges that would make a migration in <paramref name="role"/> incomplete. Never reads
    /// the catalog. Throws the driver's exception when it cannot connect, and
    /// <see cref="NotSupportedException"/> for a role the engine does not support.
    /// </summary>
    Task<ConnectionCheck> CheckConnectionAsync(string connectionString, ConnectionRole role, CancellationToken cancellationToken);
}

/// <summary>The outcome of <see cref="IDatabaseProvider.CheckConnectionAsync"/>.</summary>
public sealed record ConnectionCheck(string ServerVersion, string Database, string User, IReadOnlyList<MigrationIssue> Findings);

/// <summary>A database being read from. Implementations open pooled connections per call and are safe to use from several tasks.</summary>
public interface ISourceDatabase : IAsyncDisposable
{
    Task<SourceSchema> ReadSchemaAsync(CancellationToken cancellationToken);

    /// <summary>Streams every row of <paramref name="table"/>, producing <paramref name="columns"/> in the given order.</summary>
    Task<IRowReader> OpenTableReaderAsync(TableDefinition table, IReadOnlyList<ColumnDefinition> columns, CancellationToken cancellationToken);

    Task<long> CountRowsAsync(QualifiedName table, CancellationToken cancellationToken);
}

/// <summary>A database being written to. Implementations open pooled connections per call.</summary>
public interface ITargetDatabase : IAsyncDisposable
{
    /// <summary>Connects and reports the server version, its dialect and what already exists.</summary>
    Task<TargetInfo> InspectAsync(CancellationToken cancellationToken);

    /// <summary>Runs statements in order; with <paramref name="inTransaction"/> they all apply or none do.</summary>
    Task ExecuteAsync(IReadOnlyList<string> statements, bool inTransaction, CancellationToken cancellationToken);

    Task<IBulkWriter> OpenBulkWriterAsync(TablePlan table, CancellationToken cancellationToken);

    Task<long> CountRowsAsync(QualifiedName table, CancellationToken cancellationToken);
}

public sealed record TargetInfo(string ServerVersion, ISqlDialect Dialect, TargetState State);

/// <summary>What exists in the target before the migration touches it.</summary>
/// <param name="ExistingSchemas">Null when unknown, as when scripting offline.</param>
public sealed record TargetState(IReadOnlyCollection<QualifiedName> ExistingTables, IReadOnlyCollection<string>? ExistingSchemas)
{
    public static TargetState Unknown { get; } = new([], null);
}

/// <summary>Forward-only rows from a source table.</summary>
public interface IRowReader : IAsyncDisposable
{
    Task<bool> ReadAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Copies the current row into <paramref name="values"/>: null for SQL NULL, otherwise the
    /// <see cref="CanonicalValues"/> CLR type of the column's <see cref="DataTypeKind"/>.
    /// </summary>
    void GetValues(object?[] values);
}

/// <summary>Bulk-loads rows into one target table. Disposing without completing discards the rows.</summary>
public interface IBulkWriter : IAsyncDisposable
{
    /// <summary>Writes one row; values follow the order of <see cref="TablePlan.Columns"/>.</summary>
    ValueTask WriteRowAsync(object?[] values, CancellationToken cancellationToken);

    Task<BulkWriteResult> CompleteAsync(CancellationToken cancellationToken);
}

/// <param name="Issues">Value changes the writer had to make, such as characters the target cannot store.</param>
public sealed record BulkWriteResult(long Rows, IReadOnlyList<MigrationIssue> Issues);
