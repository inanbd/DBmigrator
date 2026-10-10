using DbMigrator.Core.Schema;

namespace DbMigrator.TestSupport;

/// <summary>Terse construction of source schemas for planner and dialect tests.</summary>
public static class SchemaBuilder
{
    public static SourceSchema Source(params TableDefinition[] tables) => new()
    {
        ProviderId = "test",
        ServerVersion = "test",
        DefaultSchema = "dbo",
        Tables = tables,
    };

    public static TableDefinition Table(string schema, string name, params ColumnDefinition[] columns) => new()
    {
        Name = new QualifiedName(schema, name),
        Columns = columns,
    };

    public static ColumnDefinition Column(string name, DataTypeKind kind, bool nullable = true) => new()
    {
        Name = name,
        Type = new DataType(kind),
        NativeType = kind.ToString().ToLowerInvariant(),
        IsNullable = nullable,
    };

    public static ColumnDefinition Id(string name = "Id") =>
        Column(name, DataTypeKind.Int32, nullable: false) with { Identity = new IdentitySpec(1, 1) };

    public static TableDefinition WithPrimaryKey(this TableDefinition table, string name, params string[] columns) =>
        table with { PrimaryKey = new PrimaryKeyDefinition(name, columns) };

    public static TableDefinition WithForeignKey(this TableDefinition table, string name, string column, QualifiedName referenced, string referencedColumn) =>
        table with
        {
            ForeignKeys = [.. table.ForeignKeys, new ForeignKeyDefinition
            {
                Name = name,
                Columns = [column],
                ReferencedTable = referenced,
                ReferencedColumns = [referencedColumn],
            }],
        };

    public static TableDefinition WithIndex(this TableDefinition table, IndexDefinition index) =>
        table with { Indexes = [.. table.Indexes, index] };
}
