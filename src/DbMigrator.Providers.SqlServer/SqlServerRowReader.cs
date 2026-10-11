using System.Globalization;
using DbMigrator.Core.Providers;
using DbMigrator.Core.Schema;
using Microsoft.Data.SqlClient;

namespace DbMigrator.Providers.SqlServer;

/// <summary>Streams a table, converting SqlClient values to the canonical CLR types.</summary>
internal sealed class SqlServerRowReader : IRowReader
{
    private readonly SqlCommand _command;
    private readonly SqlDataReader _reader;
    private readonly Func<SqlDataReader, int, object>[] _getters;
    private bool _exhausted;

    public SqlServerRowReader(SqlCommand command, SqlDataReader reader, IReadOnlyList<ColumnDefinition> columns, ReadMode[] modes)
    {
        _command = command;
        _reader = reader;
        _getters = columns.Select((c, i) => GetterFor(c, modes[i])).ToArray();
    }

    public async Task<bool> ReadAsync(CancellationToken cancellationToken)
    {
        var more = await _reader.ReadAsync(cancellationToken);
        _exhausted = !more;
        return more;
    }

    public void GetValues(object?[] values)
    {
        // Sequential access: each column is read once, in order.
        for (var i = 0; i < _getters.Length; i++)
        {
            values[i] = _reader.IsDBNull(i) ? null : _getters[i](_reader, i);
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Closing a reader that has rows left reads and discards all of them, which for a large
        // table stopped part-way takes as long as copying it. Cancelling the query first ends it.
        var cancelled = false;
        if (!_exhausted)
        {
            try
            {
                _command.Cancel();
                cancelled = true;
            }
            catch (SqlException)
            {
                // The connection is already broken; closing it below is all that is left to do.
            }
        }

        try
        {
            // Closing the reader closes its connection (CommandBehavior.CloseConnection).
            await _reader.DisposeAsync();
        }
        catch (SqlException) when (cancelled)
        {
            // "Operation cancelled by user": the cancel above, not a failure of the copy.
        }

        await _command.DisposeAsync();
    }

    private static Func<SqlDataReader, int, object> GetterFor(ColumnDefinition column, ReadMode mode)
    {
        if (mode == ReadMode.Variant)
        {
            return (r, i) => FormatVariant(r.GetValue(i));
        }

        return column.Type.Kind switch
        {
            DataTypeKind.Boolean => (r, i) => r.GetBoolean(i),
            DataTypeKind.Byte => (r, i) => r.GetByte(i),
            DataTypeKind.Int16 => (r, i) => r.GetInt16(i),
            DataTypeKind.Int32 => (r, i) => r.GetInt32(i),
            DataTypeKind.Int64 => (r, i) => r.GetInt64(i),
            DataTypeKind.Decimal => (r, i) => GetDecimal(r, i, column),
            DataTypeKind.Single => (r, i) => r.GetFloat(i),
            DataTypeKind.Double => (r, i) => r.GetDouble(i),
            DataTypeKind.FixedText or DataTypeKind.Text or DataTypeKind.Json or DataTypeKind.Xml => (r, i) => r.GetString(i),
            DataTypeKind.FixedBinary or DataTypeKind.Binary => (r, i) => r.GetFieldValue<byte[]>(i),
            DataTypeKind.Date => (r, i) => DateOnly.FromDateTime(r.GetDateTime(i)),
            DataTypeKind.Time => (r, i) => TimeOnly.FromTimeSpan(r.GetTimeSpan(i)),
            DataTypeKind.DateTime => (r, i) => DateTime.SpecifyKind(r.GetDateTime(i), DateTimeKind.Unspecified),
            DataTypeKind.DateTimeOffset => (r, i) => r.GetDateTimeOffset(i),
            DataTypeKind.Guid => (r, i) => r.GetGuid(i),
            _ => (r, i) => r.GetValue(i),
        };
    }

    private static decimal GetDecimal(SqlDataReader reader, int ordinal, ColumnDefinition column)
    {
        try
        {
            return reader.GetDecimal(ordinal);
        }
        catch (OverflowException ex)
        {
            throw new InvalidOperationException(
                $"Column {column.Name} ({column.NativeType}) holds a value with more significant digits than .NET's decimal can carry (about 28).", ex);
        }
    }

    internal static string FormatVariant(object value) => value switch
    {
        string s => s,
        DateTime d => d.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
        DateTimeOffset d => d.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture),
        TimeSpan t => t.ToString("c", CultureInfo.InvariantCulture),
        byte[] b => "0x" + Convert.ToHexString(b),
        bool b => b ? "1" : "0",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };
}
