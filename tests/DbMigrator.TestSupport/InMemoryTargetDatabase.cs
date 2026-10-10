using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using DbMigrator.Core.Diagnostics;
using DbMigrator.Core.Planning;
using DbMigrator.Core.Providers;
using DbMigrator.Core.Schema;
using DbMigrator.Providers.PostgreSql;

namespace DbMigrator.TestSupport;

/// <summary>
/// A target database held in memory, speaking the PostgreSQL dialect. It records every statement
/// and keeps the rows each completed bulk load wrote; an abandoned load keeps nothing, like a
/// rolled-back COPY.
/// </summary>
public sealed partial class InMemoryTargetDatabase : ITargetDatabase
{
    private readonly ConcurrentDictionary<QualifiedName, List<object?[]>> _rows = new();
    private readonly ConcurrentQueue<(string Sql, bool InTransaction)> _statements = new();
    private int _inspectCount;

    public HashSet<QualifiedName> ExistingTables { get; } = [];

    public HashSet<string> ExistingSchemas { get; } = ["public"];

    public PostgreSqlDialect Dialect { get; set; } = new(new Version(17, 0));

    /// <summary>Statements containing a key fail with its message.</summary>
    public Dictionary<string, string> FailingStatements { get; } = [];

    /// <summary>Pause a step whose SQL contains the key until its gate is released.</summary>
    public Dictionary<string, Gate> PausedStatements { get; } = [];

    /// <summary>Pause writing a table when it reaches the given row (zero-based).</summary>
    public Dictionary<QualifiedName, (long Row, Gate Gate)> WritePauses { get; } = [];

    public ThreadGuard Thread { get; } = new();

    public int InspectCount => _inspectCount;

    public IReadOnlyCollection<(string Sql, bool InTransaction)> Statements => _statements;

    public IReadOnlyList<object?[]> RowsOf(QualifiedName table) => _rows.TryGetValue(table, out var rows) ? rows : [];

    public Task<TargetInfo> InspectAsync(CancellationToken cancellationToken)
    {
        Thread.Check();
        Interlocked.Increment(ref _inspectCount);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new TargetInfo("InMemory PostgreSQL 17", Dialect, new TargetState(ExistingTables.ToList(), ExistingSchemas.ToList())));
    }

    public async Task ExecuteAsync(IReadOnlyList<string> statements, bool inTransaction, CancellationToken cancellationToken)
    {
        Thread.Check();
        foreach (var sql in statements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (PausedStatements.FirstOrDefault(p => sql.Contains(p.Key, StringComparison.Ordinal)) is { Value: { } gate } && !gate.IsReleased)
            {
                await gate.PassAsync(cancellationToken);
            }

            if (FailingStatements.FirstOrDefault(f => sql.Contains(f.Key, StringComparison.Ordinal)) is { Value: { } message })
            {
                throw new InvalidOperationException(message);
            }
        }

        foreach (var sql in statements)
        {
            _statements.Enqueue((sql, inTransaction));
            // Truncates and drops empty the tables they name, so re-runs count rows correctly.
            if (sql.StartsWith("TRUNCATE", StringComparison.Ordinal) || sql.StartsWith("DROP TABLE", StringComparison.Ordinal))
            {
                foreach (Match match in QuotedName().Matches(sql))
                {
                    _rows.TryRemove(new QualifiedName(Unquote(match.Groups[1].Value), Unquote(match.Groups[2].Value)), out _);
                }
            }
        }
    }

    private static string Unquote(string identifier) => identifier.Replace("\"\"", "\"", StringComparison.Ordinal);

    [GeneratedRegex("\"((?:[^\"]|\"\")+)\"\\.\"((?:[^\"]|\"\")+)\"")]
    private static partial Regex QuotedName();

    public Task<IBulkWriter> OpenBulkWriterAsync(TablePlan table, CancellationToken cancellationToken)
    {
        Thread.Check();
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IBulkWriter>(new Writer(this, table.Target));
    }

    public Task<long> CountRowsAsync(QualifiedName table, CancellationToken cancellationToken)
    {
        Thread.Check();
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult((long)RowsOf(table).Count);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed class Writer(InMemoryTargetDatabase target, QualifiedName table) : IBulkWriter
    {
        private readonly List<object?[]> _pending = [];

        public async ValueTask WriteRowAsync(object?[] values, CancellationToken cancellationToken)
        {
            target.Thread.Check();
            cancellationToken.ThrowIfCancellationRequested();
            if (target.WritePauses.TryGetValue(table, out var pause) && pause.Row == _pending.Count && !pause.Gate.IsReleased)
            {
                await pause.Gate.PassAsync(cancellationToken);
            }

            _pending.Add((object?[])values.Clone());
        }

        public Task<BulkWriteResult> CompleteAsync(CancellationToken cancellationToken)
        {
            target.Thread.Check();
            cancellationToken.ThrowIfCancellationRequested();
            target._rows.AddOrUpdate(table, _ => [.. _pending], (_, existing) => [.. existing, .. _pending]);
            target.ExistingTables.Add(table);
            return Task.FromResult(new BulkWriteResult(_pending.Count, Array.Empty<MigrationIssue>()));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
