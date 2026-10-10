using System.Globalization;
using DbMigrator.Core.Diagnostics;
using DbMigrator.Core.Planning;
using DbMigrator.Core.Providers;
using DbMigrator.Core.Schema;
using Npgsql;
using NpgsqlTypes;

namespace DbMigrator.Providers.PostgreSql;

/// <summary>Loads rows with binary COPY, converting canonical values to each target column's type.</summary>
internal sealed class PostgreSqlBulkWriter : IBulkWriter
{
    private readonly NpgsqlConnection _connection;
    private readonly NpgsqlBinaryImporter _importer;
    private readonly TablePlan _table;
    private readonly ColumnWriter[] _writers;
    private readonly long[] _nulsRemoved;

    public PostgreSqlBulkWriter(NpgsqlConnection connection, NpgsqlBinaryImporter importer, TablePlan table)
    {
        _connection = connection;
        _importer = importer;
        _table = table;
        _nulsRemoved = new long[table.Columns.Count];
        _writers = table.Columns.Select((c, i) => WriterFor(c.Type, i)).ToArray();
    }

    private delegate Task ColumnWriter(NpgsqlBinaryImporter importer, object value, CancellationToken cancellationToken);

    public async ValueTask WriteRowAsync(object?[] values, CancellationToken cancellationToken)
    {
        await _importer.StartRowAsync(cancellationToken);
        for (var i = 0; i < _writers.Length; i++)
        {
            if (values[i] is { } value)
            {
                await _writers[i](_importer, value, cancellationToken);
            }
            else
            {
                await _importer.WriteNullAsync(cancellationToken);
            }
        }
    }

    public async Task<BulkWriteResult> CompleteAsync(CancellationToken cancellationToken)
    {
        var rows = await _importer.CompleteAsync(cancellationToken);
        var issues = new List<MigrationIssue>();
        for (var i = 0; i < _nulsRemoved.Length; i++)
        {
            if (_nulsRemoved[i] > 0)
            {
                issues.Add(new(IssueSeverity.Warning, IssueCodes.NulCharacterRemoved, $"{_table.Source.Name}.{_table.Columns[i].Source.Name}",
                    $"PostgreSQL text cannot contain NUL (\\0) characters; they were removed from {_nulsRemoved[i]:N0} value(s)."));
            }
        }

        return new BulkWriteResult(checked((long)rows), issues);
    }

    public async ValueTask DisposeAsync()
    {
        // Disposing an importer that was not completed cancels the COPY, discarding its rows.
        await _importer.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private ColumnWriter WriterFor(DataType type, int ordinal) => type.Kind switch
    {
        DataTypeKind.Boolean => (w, v, ct) => w.WriteAsync(v is bool b ? b : Convert.ToBoolean(v, CultureInfo.InvariantCulture), NpgsqlDbType.Boolean, ct),
        DataTypeKind.Byte or DataTypeKind.Int16 => (w, v, ct) => w.WriteAsync(v is short s ? s : Convert.ToInt16(v, CultureInfo.InvariantCulture), NpgsqlDbType.Smallint, ct),
        DataTypeKind.Int32 => (w, v, ct) => w.WriteAsync(v is int n ? n : Convert.ToInt32(v, CultureInfo.InvariantCulture), NpgsqlDbType.Integer, ct),
        DataTypeKind.Int64 => (w, v, ct) => w.WriteAsync(v is long n ? n : Convert.ToInt64(v, CultureInfo.InvariantCulture), NpgsqlDbType.Bigint, ct),
        DataTypeKind.Decimal => (w, v, ct) => w.WriteAsync(v is decimal d ? d : Convert.ToDecimal(v, CultureInfo.InvariantCulture), NpgsqlDbType.Numeric, ct),
        DataTypeKind.Single => (w, v, ct) => w.WriteAsync(v is float f ? f : Convert.ToSingle(v, CultureInfo.InvariantCulture), NpgsqlDbType.Real, ct),
        DataTypeKind.Double => (w, v, ct) => w.WriteAsync(v is double d ? d : Convert.ToDouble(v, CultureInfo.InvariantCulture), NpgsqlDbType.Double, ct),
        DataTypeKind.FixedText => (w, v, ct) => w.WriteAsync(Text(v, ordinal), NpgsqlDbType.Char, ct),
        DataTypeKind.Text when type.Length is not null => (w, v, ct) => w.WriteAsync(Text(v, ordinal), NpgsqlDbType.Varchar, ct),
        DataTypeKind.Text => (w, v, ct) => w.WriteAsync(Text(v, ordinal), NpgsqlDbType.Text, ct),
        DataTypeKind.Json => (w, v, ct) => w.WriteAsync(Text(v, ordinal), NpgsqlDbType.Jsonb, ct),
        DataTypeKind.Xml => (w, v, ct) => w.WriteAsync(Text(v, ordinal), NpgsqlDbType.Xml, ct),
        DataTypeKind.FixedBinary or DataTypeKind.Binary => (w, v, ct) => w.WriteAsync((byte[])v, NpgsqlDbType.Bytea, ct),
        // Dates and timestamps go over the wire in PostgreSQL's own representation. Written as
        // DateTime/DateOnly, Npgsql would turn their MinValue/MaxValue (real values such as
        // 9999-12-31 23:59:59.9999999 in SQL Server) into -infinity/infinity.
        DataTypeKind.Date => (w, v, ct) => w.WriteAsync(DaysSince2000(ToDate(v)), NpgsqlDbType.Date, ct),
        DataTypeKind.Time => (w, v, ct) => w.WriteAsync(ToTime(v), NpgsqlDbType.Time, ct),
        DataTypeKind.DateTime => (w, v, ct) => w.WriteAsync(MicrosecondsSince2000(ToDateTime(v).Ticks), NpgsqlDbType.Timestamp, ct),
        DataTypeKind.DateTimeOffset => (w, v, ct) => w.WriteAsync(MicrosecondsSince2000(ToDateTimeOffset(v).UtcTicks), NpgsqlDbType.TimestampTz, ct),
        DataTypeKind.Guid => (w, v, ct) => w.WriteAsync(v is Guid g ? g : Guid.Parse((string)v), NpgsqlDbType.Uuid, ct),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type.Kind, null),
    };

    private string Text(object value, int ordinal)
    {
        var text = value as string ?? Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        if (text.Contains('\0', StringComparison.Ordinal))
        {
            _nulsRemoved[ordinal]++;
            text = text.Replace("\0", "", StringComparison.Ordinal);
        }

        return text;
    }

    private static readonly long Epoch2000Ticks = new DateTime(2000, 1, 1).Ticks;
    private static readonly int Epoch2000Day = new DateOnly(2000, 1, 1).DayNumber;

    internal static int DaysSince2000(DateOnly date) => date.DayNumber - Epoch2000Day;

    /// <summary>Drops sub-microsecond ticks, rounding down (not toward zero) so earlier dates keep their second.</summary>
    internal static long MicrosecondsSince2000(long ticks)
    {
        var sinceEpoch = ticks - Epoch2000Ticks;
        return sinceEpoch / 10 - (sinceEpoch % 10 < 0 ? 1 : 0);
    }

    private static DateOnly ToDate(object value) => value switch
    {
        DateOnly d => d,
        DateTime d => DateOnly.FromDateTime(d),
        _ => DateOnly.Parse((string)value, CultureInfo.InvariantCulture),
    };

    private static TimeOnly ToTime(object value) => value switch
    {
        TimeOnly t => t,
        TimeSpan t => TimeOnly.FromTimeSpan(t),
        _ => TimeOnly.Parse((string)value, CultureInfo.InvariantCulture),
    };

    private static DateTime ToDateTime(object value) => value switch
    {
        DateTime d => d,
        DateTimeOffset d => d.DateTime,
        _ => DateTime.Parse((string)value, CultureInfo.InvariantCulture),
    };

    private static DateTimeOffset ToDateTimeOffset(object value) => value switch
    {
        DateTimeOffset d => d,
        DateTime d => new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Utc)),
        _ => DateTimeOffset.Parse((string)value, CultureInfo.InvariantCulture).ToUniversalTime(),
    };
}
