using DbMigrator.Core.Schema;

namespace DbMigrator.Core.Planning;

public sealed record MigrationOptions
{
    public TableFilter Tables { get; init; } = TableFilter.All;

    public NamingOptions Naming { get; init; } = new();

    /// <summary>What to do when a table already exists in the target.</summary>
    public TableExistsAction IfTableExists { get; init; } = TableExistsAction.Fail;

    /// <summary>Per-table choices that override <see cref="IfTableExists"/>, keyed by source table name.</summary>
    public IReadOnlyDictionary<QualifiedName, TableExistsAction> TableExistsOverrides { get; init; } =
        new Dictionary<QualifiedName, TableExistsAction>();

    /// <summary>False creates the schema only.</summary>
    public bool CopyData { get; init; } = true;

    /// <summary>Secondary indexes and unique constraints. Primary keys are always created.</summary>
    public bool CreateIndexes { get; init; } = true;

    public bool CreateForeignKeys { get; init; } = true;

    /// <summary>Count rows on both sides after copying and flag any table whose counts differ.</summary>
    public bool VerifyRowCounts { get; init; } = true;
}

public enum TableExistsAction
{
    /// <summary>Stop before changing anything, listing the tables that exist.</summary>
    Fail,
    /// <summary>Drop the table (and, on engines that need it, objects depending on it) and create it afresh.</summary>
    DropAndRecreate,
    /// <summary>Keep the table and its constraints, delete its rows, then load.</summary>
    Truncate,
    /// <summary>Keep the table and its rows and add the source rows.</summary>
    Append,
    /// <summary>Leave the table alone and copy nothing into it.</summary>
    Skip,
}

public enum IdentifierCase
{
    /// <summary>Keep names exactly as the source spells them (they will need quoting on case-folding engines).</summary>
    Preserve,
    /// <summary><c>OrderDetails</c> becomes <c>orderdetails</c>.</summary>
    Lower,
    /// <summary><c>OrderDetails</c> becomes <c>order_details</c>.</summary>
    SnakeCase,
}

public sealed record NamingOptions
{
    public IdentifierCase Case { get; init; } = IdentifierCase.Lower;

    /// <summary>
    /// Source schema to target schema. Unmapped schemas keep their (case-converted) name,
    /// except the source's default schema, which maps to the target's default schema.
    /// Keys match case-insensitively; values are used verbatim.
    /// </summary>
    public IReadOnlyDictionary<string, string> SchemaMap { get; init; } = new Dictionary<string, string>();
}
