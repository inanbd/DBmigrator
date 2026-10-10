using DbMigrator.Core.Providers;
using DbMigrator.Core.Schema;

namespace DbMigrator.TestSupport;

/// <summary>
/// A source database held in memory. Every table produces <see cref="RowCounts"/> rows of
/// canonical sample values, so the real engine can copy it into <see cref="InMemoryTargetDatabase"/>.
/// </summary>
public sealed class InMemorySourceDatabase(SourceSchema schema) : ISourceDatabase
{
    private int _readSchemaCount;
    private int _countRowsCount;

    public SourceSchema Schema { get; set; } = schema;

    /// <summary>Rows per table; tables not listed have <see cref="DefaultRowCount"/>.</summary>
    public Dictionary<QualifiedName, long> RowCounts { get; } = [];

    public long DefaultRowCount { get; set; } = 3;

    /// <summary>Pause the copy of a table when it reaches the given row (zero-based).</summary>
    public Dictionary<QualifiedName, (long Row, Gate Gate)> Pauses { get; } = [];

    /// <summary>Make reading a table fail at the given row (zero-based).</summary>
    public Dictionary<QualifiedName, (long Row, Exception Error)> Failures { get; } = [];

    /// <summary>When set, a cancelled read throws this instead of OperationCanceledException, as some drivers do.</summary>
    public Func<Exception>? CancellationError { get; set; }

    public ThreadGuard Thread { get; } = new();

    public int ReadSchemaCount => _readSchemaCount;

    public int CountRowsCount => _countRowsCount;

    public bool IsDisposed { get; private set; }

    public long RowsIn(QualifiedName table) => RowCounts.TryGetValue(table, out var rows) ? rows : DefaultRowCount;

    public Task<SourceSchema> ReadSchemaAsync(CancellationToken cancellationToken)
    {
        Thread.Check();
        Interlocked.Increment(ref _readSchemaCount);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Schema);
    }

    public Task<IRowReader> OpenTableReaderAsync(TableDefinition table, IReadOnlyList<ColumnDefinition> columns, CancellationToken cancellationToken)
    {
        Thread.Check();
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IRowReader>(new Reader(this, table.Name, columns));
    }

    public Task<long> CountRowsAsync(QualifiedName table, CancellationToken cancellationToken)
    {
        Thread.Check();
        Interlocked.Increment(ref _countRowsCount);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(RowsIn(table));
    }

    public ValueTask DisposeAsync()
    {
        IsDisposed = true;
        return ValueTask.CompletedTask;
    }

    /// <summary>A deterministic canonical value for a column kind and row number.</summary>
    public static object SampleValue(DataType type, long row) => type.Kind switch
    {
        DataTypeKind.Boolean => row % 2 == 0,
        DataTypeKind.Byte => (byte)(row % 256),
        DataTypeKind.Int16 => (short)(row % short.MaxValue),
        DataTypeKind.Int32 => (int)(row + 1),
        DataTypeKind.Int64 => row + 1,
        DataTypeKind.Decimal => (row + 1) / 100m,
        DataTypeKind.Single => (float)row / 2,
        DataTypeKind.Double => row / 3d,
        DataTypeKind.FixedText or DataTypeKind.Text => $"value {row}",
        DataTypeKind.Json => $"{{\"row\":{row}}}",
        DataTypeKind.Xml => $"<row n=\"{row}\"/>",
        DataTypeKind.FixedBinary or DataTypeKind.Binary => BitConverter.GetBytes(row),
        DataTypeKind.Date => DateOnly.FromDayNumber(730_000 + (int)(row % 1000)),
        DataTypeKind.Time => TimeOnly.FromTimeSpan(TimeSpan.FromSeconds(row % 86_400)),
        DataTypeKind.DateTime => new DateTime(2024, 1, 1).AddMinutes(row),
        DataTypeKind.DateTimeOffset => new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(row),
        DataTypeKind.Guid => new Guid((int)row, 0, 0, new byte[8]),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type.Kind, null),
    };

    private sealed class Reader(InMemorySourceDatabase source, QualifiedName table, IReadOnlyList<ColumnDefinition> columns) : IRowReader
    {
        private readonly long _rows = source.RowsIn(table);
        private long _next;

        public async Task<bool> ReadAsync(CancellationToken cancellationToken)
        {
            source.Thread.Check();
            if (cancellationToken.IsCancellationRequested)
            {
                throw source.CancellationError?.Invoke() ?? new OperationCanceledException(cancellationToken);
            }

            if (source.Pauses.TryGetValue(table, out var pause) && pause.Row == _next && !pause.Gate.IsReleased)
            {
                try
                {
                    await pause.Gate.PassAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (source.CancellationError is not null)
                {
                    throw source.CancellationError();
                }
            }

            if (source.Failures.TryGetValue(table, out var failure) && failure.Row == _next)
            {
                throw failure.Error;
            }

            if (_next >= _rows)
            {
                return false;
            }

            _next++;
            return true;
        }

        public void GetValues(object?[] values)
        {
            for (var i = 0; i < columns.Count; i++)
            {
                values[i] = columns[i].IsNullable && (_next + i) % 7 == 0 ? null : SampleValue(columns[i].Type, _next - 1);
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
