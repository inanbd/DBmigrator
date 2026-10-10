using System.Data;
using System.Globalization;
using DbMigrator.Core.Diagnostics;
using DbMigrator.Core.Providers;
using DbMigrator.Core.Schema;
using Microsoft.Data.SqlClient;

namespace DbMigrator.Providers.SqlServer;

internal sealed class SqlServerSourceDatabase(string connectionString) : ISourceDatabase
{
    private const string DefaultSchema = "dbo";

    // One round trip; each SELECT is a result set read in order below.
    private const string CatalogQuery = """
        SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128)),
               CAST(SERVERPROPERTY('Edition') AS nvarchar(128)),
               CAST(DATABASEPROPERTYEX(DB_NAME(), 'Collation') AS nvarchar(128));

        SELECT t.object_id, s.name, t.name,
               (SELECT SUM(p.rows) FROM sys.partitions p WHERE p.object_id = t.object_id AND p.index_id IN (0, 1))
        FROM sys.tables t
        JOIN sys.schemas s ON s.schema_id = t.schema_id
        WHERE t.is_ms_shipped = 0 AND NOT (s.name = N'dbo' AND t.name = N'sysdiagrams');

        SELECT c.object_id, c.name,
               CASE WHEN ty.is_assembly_type = 0 THEN COALESCE(bt.name, ty.name)
                    WHEN ty.is_user_defined = 0 THEN ty.name
                    ELSE SCHEMA_NAME(ty.schema_id) + N'.' + ty.name END,
               CAST(CASE WHEN ty.is_user_defined = 1 AND ty.is_assembly_type = 1 THEN 1 ELSE 0 END AS bit),
               c.max_length, c.precision, c.scale, c.is_nullable, c.collation_name,
               TRY_CONVERT(bigint, ic.seed_value), TRY_CONVERT(bigint, ic.increment_value),
               dc.definition, cc.definition, c.is_column_set
        FROM sys.columns c
        JOIN sys.tables t ON t.object_id = c.object_id
        JOIN sys.types ty ON ty.user_type_id = c.user_type_id
        -- The system type behind alias types and sysname; CLR types have none.
        LEFT JOIN sys.types bt ON bt.user_type_id = c.system_type_id
        LEFT JOIN sys.identity_columns ic ON ic.object_id = c.object_id AND ic.column_id = c.column_id
        LEFT JOIN sys.default_constraints dc ON dc.object_id = c.default_object_id
        LEFT JOIN sys.computed_columns cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
        WHERE t.is_ms_shipped = 0
        ORDER BY c.object_id, c.column_id;

        SELECT i.object_id, i.index_id, i.name, i.type, i.type_desc, i.is_unique, i.is_primary_key,
               i.is_unique_constraint, i.filter_definition, i.is_disabled, CAST(ISNULL(kc.is_system_named, 0) AS bit)
        FROM sys.indexes i
        JOIN sys.tables t ON t.object_id = i.object_id
        LEFT JOIN sys.key_constraints kc ON kc.parent_object_id = i.object_id AND kc.unique_index_id = i.index_id
        WHERE t.is_ms_shipped = 0 AND i.type > 0 AND i.is_hypothetical = 0
        ORDER BY i.object_id, i.index_id;

        SELECT ic.object_id, ic.index_id, c.name, ic.key_ordinal, ic.is_descending_key, ic.is_included_column
        FROM sys.index_columns ic
        JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
        JOIN sys.tables t ON t.object_id = ic.object_id
        WHERE t.is_ms_shipped = 0
        ORDER BY ic.object_id, ic.index_id, ic.is_included_column, ic.key_ordinal, ic.index_column_id;

        SELECT fk.object_id, fk.parent_object_id, fk.name, rs.name, rt.name,
               fk.delete_referential_action, fk.update_referential_action, fk.is_disabled, fk.is_not_trusted, fk.is_system_named
        FROM sys.foreign_keys fk
        JOIN sys.tables pt ON pt.object_id = fk.parent_object_id
        JOIN sys.tables rt ON rt.object_id = fk.referenced_object_id
        JOIN sys.schemas rs ON rs.schema_id = rt.schema_id
        WHERE pt.is_ms_shipped = 0
        ORDER BY fk.parent_object_id, fk.name;

        SELECT fkc.constraint_object_id, pc.name, rc.name
        FROM sys.foreign_key_columns fkc
        JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
        JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
        ORDER BY fkc.constraint_object_id, fkc.constraint_column_id;

        SELECT cc.parent_object_id, cc.name, cc.definition
        FROM sys.check_constraints cc
        JOIN sys.tables t ON t.object_id = cc.parent_object_id
        WHERE t.is_ms_shipped = 0
        ORDER BY cc.parent_object_id, cc.name;
        """;

    public async Task<SourceSchema> ReadSchemaAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(CatalogQuery, connection) { CommandTimeout = 0 };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        await reader.ReadAsync(cancellationToken);
        var serverVersion = $"SQL Server {reader.GetString(0)} ({reader.GetString(1)})";
        var databaseCollation = reader.IsDBNull(2) ? null : reader.GetString(2);

        var tables = new Dictionary<int, TableBuilder>();
        await reader.NextResultAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            tables[reader.GetInt32(0)] = new TableBuilder(
                new QualifiedName(reader.GetString(1), reader.GetString(2)),
                reader.IsDBNull(3) ? null : reader.GetInt64(3));
        }

        var collations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (databaseCollation is not null)
        {
            collations.Add(databaseCollation);
        }

        await reader.NextResultAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            // Rows for objects the table query left out (such as sysdiagrams) are skipped throughout.
            if (!tables.TryGetValue(reader.GetInt32(0), out var table))
            {
                continue;
            }

            var name = reader.GetString(1);
            if (reader.GetBoolean(13))
            {
                table.Issues.Add(new(IssueSeverity.Info, IssueCodes.SkippedColumn, $"{table.Name}.{name}",
                    "Sparse column sets are not copied; the sparse columns they cover are copied individually."));
                continue;
            }

            var type = SqlServerTypes.Map(reader.GetString(2), reader.GetInt16(4), reader.GetByte(5), reader.GetByte(6), reader.GetBoolean(3));
            if (type.Note is { } note)
            {
                table.Issues.Add(note.For($"{table.Name}.{name}"));
            }

            var collation = reader.IsDBNull(8) ? null : reader.GetString(8);
            if (collation is not null)
            {
                collations.Add(collation);
            }

            table.Columns.Add(new ColumnDefinition
            {
                Name = name,
                Type = type.Type,
                NativeType = type.NativeType,
                // SQL Server fills rowversion itself; no target will, so new rows must be allowed to leave it empty.
                IsNullable = reader.GetBoolean(7) || type.NativeType == "rowversion",
                Collation = collation,
                Identity = reader.IsDBNull(9) ? null : new IdentitySpec(reader.GetInt64(9), reader.IsDBNull(10) ? 1 : reader.GetInt64(10)),
                Default = reader.IsDBNull(11) ? null : SqlServerDefaults.Parse(reader.GetString(11)),
                ComputedExpression = reader.IsDBNull(12) ? null : reader.GetString(12),
            });
        }

        var indexes = new Dictionary<(int, int), IndexRow>();
        await reader.NextResultAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!tables.TryGetValue(reader.GetInt32(0), out var table))
            {
                continue;
            }

            indexes[(reader.GetInt32(0), reader.GetInt32(1))] = new IndexRow(
                Table: table,
                Name: reader.GetString(2),
                Type: reader.GetByte(3),
                TypeDescription: reader.GetString(4),
                IsUnique: reader.GetBoolean(5),
                IsPrimaryKey: reader.GetBoolean(6),
                IsUniqueConstraint: reader.GetBoolean(7),
                Filter: reader.IsDBNull(8) ? null : reader.GetString(8),
                IsDisabled: reader.GetBoolean(9),
                IsSystemNamed: reader.GetBoolean(10));
        }

        await reader.NextResultAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!indexes.TryGetValue((reader.GetInt32(0), reader.GetInt32(1)), out var index))
            {
                continue;
            }

            var column = reader.GetString(2);
            if (reader.GetBoolean(5))
            {
                index.Included.Add(column);
            }
            else if (reader.GetByte(3) > 0)
            {
                // key_ordinal 0 on a non-included column is a partitioning column SQL Server adds itself.
                index.Keys.Add(new IndexColumn(column, reader.GetBoolean(4)));
            }
        }

        foreach (var index in indexes.Values)
        {
            AddIndex(index);
        }

        var foreignKeys = new Dictionary<int, ForeignKeyRow>();
        await reader.NextResultAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!tables.TryGetValue(reader.GetInt32(1), out var table))
            {
                continue;
            }

            foreignKeys[reader.GetInt32(0)] = new ForeignKeyRow(
                Table: table,
                Name: reader.GetString(2),
                Referenced: new QualifiedName(reader.GetString(3), reader.GetString(4)),
                OnDelete: ToAction(reader.GetByte(5)),
                OnUpdate: ToAction(reader.GetByte(6)),
                IsTrusted: !reader.GetBoolean(7) && !reader.GetBoolean(8),
                IsSystemNamed: reader.GetBoolean(9));
        }

        await reader.NextResultAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!foreignKeys.TryGetValue(reader.GetInt32(0), out var fk))
            {
                continue;
            }

            fk.Columns.Add(reader.GetString(1));
            fk.ReferencedColumns.Add(reader.GetString(2));
        }

        foreach (var fk in foreignKeys.Values)
        {
            fk.Table.ForeignKeys.Add(new ForeignKeyDefinition
            {
                Name = fk.Name,
                Columns = fk.Columns,
                ReferencedTable = fk.Referenced,
                ReferencedColumns = fk.ReferencedColumns,
                OnDelete = fk.OnDelete,
                OnUpdate = fk.OnUpdate,
                IsTrusted = fk.IsTrusted,
                IsNameGenerated = fk.IsSystemNamed,
            });
        }

        await reader.NextResultAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (tables.TryGetValue(reader.GetInt32(0), out var table))
            {
                table.Checks.Add(new CheckConstraintDefinition(reader.GetString(1), reader.GetString(2)));
            }
        }

        var issues = new List<MigrationIssue>();
        var caseInsensitive = collations.Where(c => c.Contains("_CI", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal).ToList();
        if (caseInsensitive.Count > 0)
        {
            issues.Add(new(IssueSeverity.Info, IssueCodes.CaseSensitivity, connection.Database,
                $"The source compares text case-insensitively ({string.Join(", ", caseInsensitive)}). Targets that compare case-sensitively, such as PostgreSQL, treat 'abc' and 'ABC' as different in queries and unique indexes."));
        }

        return new SourceSchema
        {
            ProviderId = SqlServerProvider.ProviderId,
            ServerVersion = serverVersion,
            DefaultSchema = DefaultSchema,
            Tables = tables.Values.Select(t => t.Build()).OrderBy(t => t.Name, QualifiedNameComparer.Ordinal).ToList(),
            Issues = issues,
        };
    }

    public async Task<IRowReader> OpenTableReaderAsync(TableDefinition table, IReadOnlyList<ColumnDefinition> columns, CancellationToken cancellationToken)
    {
        var modes = columns.Select(c => SqlServerTypes.ReadModeOf(c.NativeType)).ToArray();
        var selectList = string.Join(", ", columns.Select((c, i) => SelectExpression(c, modes[i])));
        var sql = $"SELECT {selectList} FROM {Quote(table.Name)}";

        var connection = new SqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            var command = new SqlCommand(sql, connection) { CommandTimeout = 0 };
            var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess | CommandBehavior.CloseConnection, cancellationToken);
            return new SqlServerRowReader(command, reader, columns, modes);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async Task<long> CountRowsAsync(QualifiedName table, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand($"SELECT COUNT_BIG(*) FROM {Quote(table)}", connection) { CommandTimeout = 0 };
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    internal static string Quote(string identifier) => "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";

    internal static string Quote(QualifiedName name) => $"{Quote(name.Schema)}.{Quote(name.Name)}";

    private static string SelectExpression(ColumnDefinition column, ReadMode mode) => mode switch
    {
        ReadMode.ToStringMethod => $"{Quote(column.Name)}.ToString() AS {Quote(column.Name)}",
        ReadMode.ConvertToText => $"CONVERT(nvarchar(max), {Quote(column.Name)}) AS {Quote(column.Name)}",
        _ => Quote(column.Name),
    };

    private static ReferentialAction ToAction(byte action) => action switch
    {
        1 => ReferentialAction.Cascade,
        2 => ReferentialAction.SetNull,
        3 => ReferentialAction.SetDefault,
        _ => ReferentialAction.NoAction,
    };

    private static void AddIndex(IndexRow index)
    {
        var table = index.Table;
        var subject = $"{table.Name}.{index.Name}";
        if (index.IsPrimaryKey)
        {
            table.PrimaryKey = new PrimaryKeyDefinition(index.Name, index.Keys.Select(k => k.Name).ToList())
            {
                IsNameGenerated = index.IsSystemNamed,
            };
            return;
        }

        // 1 clustered, 2 nonclustered, 7 nonclustered hash (memory-optimized) are plain B-tree-like indexes.
        if (index.Type is not (1 or 2 or 7))
        {
            table.Issues.Add(new(IssueSeverity.Warning, IssueCodes.UnsupportedIndex, subject,
                $"{index.TypeDescription.ToLowerInvariant().Replace('_', ' ')} index was not migrated; it has no general equivalent."));
            return;
        }

        if (index.IsDisabled)
        {
            table.Issues.Add(new(IssueSeverity.Info, IssueCodes.DisabledIndex, subject, "The index is disabled in the source and was not migrated."));
            return;
        }

        table.Indexes.Add(new IndexDefinition
        {
            Name = index.Name,
            Columns = index.Keys,
            IncludedColumns = index.Included,
            IsUnique = index.IsUnique,
            IsUniqueConstraint = index.IsUniqueConstraint,
            IsNameGenerated = index.IsSystemNamed,
            // SQL Server unique indexes treat NULLs as equal: at most one NULL per key.
            NullsAreDistinct = !index.IsUnique,
            Filter = index.Filter,
        });
    }

    private sealed class TableBuilder(QualifiedName name, long? estimatedRows)
    {
        public QualifiedName Name { get; } = name;

        public List<ColumnDefinition> Columns { get; } = [];

        public PrimaryKeyDefinition? PrimaryKey { get; set; }

        public List<IndexDefinition> Indexes { get; } = [];

        public List<ForeignKeyDefinition> ForeignKeys { get; } = [];

        public List<CheckConstraintDefinition> Checks { get; } = [];

        public List<MigrationIssue> Issues { get; } = [];

        public TableDefinition Build() => new()
        {
            Name = Name,
            Columns = Columns,
            PrimaryKey = PrimaryKey,
            Indexes = Indexes,
            ForeignKeys = ForeignKeys,
            CheckConstraints = Checks,
            EstimatedRowCount = estimatedRows,
            Issues = Issues,
        };
    }

    private sealed record IndexRow(
        TableBuilder Table,
        string Name,
        byte Type,
        string TypeDescription,
        bool IsUnique,
        bool IsPrimaryKey,
        bool IsUniqueConstraint,
        string? Filter,
        bool IsDisabled,
        bool IsSystemNamed)
    {
        public List<IndexColumn> Keys { get; } = [];

        public List<string> Included { get; } = [];
    }

    private sealed record ForeignKeyRow(
        TableBuilder Table,
        string Name,
        QualifiedName Referenced,
        ReferentialAction OnDelete,
        ReferentialAction OnUpdate,
        bool IsTrusted,
        bool IsSystemNamed)
    {
        public List<string> Columns { get; } = [];

        public List<string> ReferencedColumns { get; } = [];
    }
}
