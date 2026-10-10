using DbMigrator.Core.Planning;
using DbMigrator.Core.Providers;
using DbMigrator.Core.Schema;
using Npgsql;

namespace DbMigrator.Providers.PostgreSql;

internal sealed class PostgreSqlTargetDatabase : ITargetDatabase
{
    private readonly NpgsqlDataSource _dataSource;
    private PostgreSqlDialect? _dialect;

    public PostgreSqlTargetDatabase(string connectionString)
    {
        // Index builds and COPYs of large tables take as long as they take.
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { CommandTimeout = 0 };
        _dataSource = NpgsqlDataSource.Create(builder);
    }

    public async Task<TargetInfo> InspectAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        var tables = new List<QualifiedName>();
        await using (var command = new NpgsqlCommand(
            """
            SELECT n.nspname, c.relname
            FROM pg_catalog.pg_class c
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            WHERE c.relkind IN ('r', 'p')
              AND n.nspname NOT IN ('pg_catalog', 'information_schema')
              AND n.nspname NOT LIKE 'pg\_toast%'
              AND n.nspname NOT LIKE 'pg\_temp\_%'
            """, connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                tables.Add(new QualifiedName(reader.GetString(0), reader.GetString(1)));
            }
        }

        var schemas = new List<string>();
        await using (var command = new NpgsqlCommand("SELECT nspname FROM pg_catalog.pg_namespace", connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                schemas.Add(reader.GetString(0));
            }
        }

        _dialect = new PostgreSqlDialect(connection.PostgreSqlVersion);
        return new TargetInfo($"PostgreSQL {connection.ServerVersion}", _dialect, new TargetState(tables, schemas));
    }

    public async Task ExecuteAsync(IReadOnlyList<string> statements, bool inTransaction, CancellationToken cancellationToken)
    {
        if (statements.Count == 0)
        {
            return;
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        // PostgreSQL DDL is transactional, so a failed batch leaves the target untouched.
        await using var transaction = inTransaction ? await connection.BeginTransactionAsync(cancellationToken) : null;
        foreach (var sql in statements)
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }
    }

    public async Task<IBulkWriter> OpenBulkWriterAsync(TablePlan table, CancellationToken cancellationToken)
    {
        var dialect = _dialect ?? new PostgreSqlDialect();
        var columns = string.Join(", ", table.Columns.Select(c => dialect.QuoteIdentifier(c.Name)));
        var copy = $"COPY {dialect.QuoteName(table.Target)} ({columns}) FROM STDIN (FORMAT BINARY)";

        var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        try
        {
            var importer = await connection.BeginBinaryImportAsync(copy, cancellationToken);
            return new PostgreSqlBulkWriter(connection, importer, table);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async Task<long> CountRowsAsync(QualifiedName table, CancellationToken cancellationToken)
    {
        var dialect = _dialect ?? new PostgreSqlDialect();
        await using var command = _dataSource.CreateCommand($"SELECT count(*) FROM {dialect.QuoteName(table)}");
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}
