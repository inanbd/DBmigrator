using DbMigrator.Core.Diagnostics;

namespace DbMigrator.Core.Schema;

public sealed record TableDefinition
{
    public required QualifiedName Name { get; init; }

    public required IReadOnlyList<ColumnDefinition> Columns { get; init; }

    public PrimaryKeyDefinition? PrimaryKey { get; init; }

    /// <summary>Secondary indexes and unique constraints (the primary key is separate).</summary>
    public IReadOnlyList<IndexDefinition> Indexes { get; init; } = [];

    public IReadOnlyList<ForeignKeyDefinition> ForeignKeys { get; init; } = [];

    public IReadOnlyList<CheckConstraintDefinition> CheckConstraints { get; init; } = [];

    /// <summary>The catalog's row estimate, for progress display. Not exact.</summary>
    public long? EstimatedRowCount { get; init; }

    /// <summary>Things the reader noticed about this table, such as index kinds it skipped.</summary>
    public IReadOnlyList<MigrationIssue> Issues { get; init; } = [];
}

public sealed record PrimaryKeyDefinition(string Name, IReadOnlyList<string> Columns)
{
    /// <summary>The source made the name up (e.g. <c>PK__Orders__C3905BAF</c>); targets get a readable one instead.</summary>
    public bool IsNameGenerated { get; init; }
}

public sealed record IndexColumn(string Name, bool IsDescending = false);

public sealed record IndexDefinition
{
    public required string Name { get; init; }

    public required IReadOnlyList<IndexColumn> Columns { get; init; }

    public IReadOnlyList<string> IncludedColumns { get; init; } = [];

    public bool IsUnique { get; init; }

    /// <summary>Declared as a UNIQUE constraint rather than a unique index.</summary>
    public bool IsUniqueConstraint { get; init; }

    /// <summary>The source made the constraint's name up; targets get a readable one instead.</summary>
    public bool IsNameGenerated { get; init; }

    /// <summary>
    /// Whether a unique index lets several rows hold NULL. False on SQL Server, where
    /// NULLs compare equal and only one is allowed.
    /// </summary>
    public bool NullsAreDistinct { get; init; } = true;

    /// <summary>Partial-index predicate in the source dialect, or null.</summary>
    public string? Filter { get; init; }
}

public enum ReferentialAction
{
    NoAction,
    Cascade,
    SetNull,
    SetDefault,
    Restrict,
}

public sealed record ForeignKeyDefinition
{
    public required string Name { get; init; }

    public required IReadOnlyList<string> Columns { get; init; }

    public required QualifiedName ReferencedTable { get; init; }

    public required IReadOnlyList<string> ReferencedColumns { get; init; }

    public ReferentialAction OnDelete { get; init; }

    public ReferentialAction OnUpdate { get; init; }

    /// <summary>False when the source never checked existing rows (disabled or created WITH NOCHECK).</summary>
    public bool IsTrusted { get; init; } = true;

    /// <summary>The source made the name up; targets get a readable one instead.</summary>
    public bool IsNameGenerated { get; init; }
}

/// <summary>A CHECK constraint; the expression is in the source dialect.</summary>
public sealed record CheckConstraintDefinition(string Name, string Expression);

/// <summary>Everything a source provider reports about the database it read.</summary>
public sealed record SourceSchema
{
    public required string ProviderId { get; init; }

    public required string ServerVersion { get; init; }

    /// <summary>The schema unqualified names resolve to (<c>dbo</c> on SQL Server).</summary>
    public required string DefaultSchema { get; init; }

    public required IReadOnlyList<TableDefinition> Tables { get; init; }

    /// <summary>Things the reader noticed about the database as a whole.</summary>
    public IReadOnlyList<MigrationIssue> Issues { get; init; } = [];
}
