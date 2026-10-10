using DbMigrator.Core.Diagnostics;
using DbMigrator.Core.Schema;

namespace DbMigrator.Core.Planning;

public enum TableAction
{
    Create,
    DropAndCreate,
    Truncate,
    Append,
    Skip,
}

/// <summary>A source column and what it becomes in the target.</summary>
public sealed record ColumnPlan
{
    public required ColumnDefinition Source { get; init; }

    public required string Name { get; init; }

    /// <summary>The target column's type, which bulk writers convert source values into.</summary>
    public required DataType Type { get; init; }

    /// <summary>The type as written in the target's DDL.</summary>
    public required string SqlType { get; init; }

    public bool IsNullable { get; init; } = true;

    public IdentitySpec? Identity { get; init; }

    /// <summary>The default as target SQL, or null for none.</summary>
    public string? DefaultSql { get; init; }
}

public sealed record PrimaryKeyPlan(string Name, IReadOnlyList<string> Columns);

public sealed record IndexPlan
{
    public required string Name { get; init; }

    public required IReadOnlyList<IndexColumn> Columns { get; init; }

    public IReadOnlyList<string> IncludedColumns { get; init; } = [];

    public bool IsUnique { get; init; }

    public bool IsUniqueConstraint { get; init; }

    public bool NullsAreDistinct { get; init; } = true;
}

public sealed record ForeignKeyPlan
{
    public required string Name { get; init; }

    public required IReadOnlyList<string> Columns { get; init; }

    public required QualifiedName ReferencedTable { get; init; }

    public required IReadOnlyList<string> ReferencedColumns { get; init; }

    public ReferentialAction OnDelete { get; init; }

    public ReferentialAction OnUpdate { get; init; }

    public bool IsTrusted { get; init; } = true;
}

public sealed record TablePlan
{
    public required TableDefinition Source { get; init; }

    public required QualifiedName Target { get; init; }

    public required TableAction Action { get; init; }

    /// <summary>Whether a table with the target name already existed when the plan was made.</summary>
    public bool TargetExists { get; init; }

    /// <summary>
    /// The table exists and the options say to fail rather than choose an action. The plan
    /// carries an error for it; set an action in <see cref="MigrationOptions.TableExistsOverrides"/> and plan again.
    /// </summary>
    public bool IsUnresolvedConflict { get; init; }

    public required IReadOnlyList<ColumnPlan> Columns { get; init; }

    /// <summary>Set only when this run creates the table; likewise indexes and foreign keys.</summary>
    public PrimaryKeyPlan? PrimaryKey { get; init; }

    public IReadOnlyList<IndexPlan> Indexes { get; init; } = [];

    public IReadOnlyList<ForeignKeyPlan> ForeignKeys { get; init; } = [];

    public bool CreatesTable => Action is TableAction.Create or TableAction.DropAndCreate;

    public bool ReceivesData => Action is not TableAction.Skip;
}

public sealed record MigrationPlan
{
    /// <summary>Tables in load order: referenced tables come before the tables referencing them.</summary>
    public required IReadOnlyList<TablePlan> Tables { get; init; }

    /// <summary>Target schemas that do not exist yet and must be created.</summary>
    public required IReadOnlyList<string> SchemasToCreate { get; init; }

    public required IReadOnlyList<MigrationIssue> Issues { get; init; }

    public bool HasErrors => Issues.Any(i => i.Severity == IssueSeverity.Error);

    public IEnumerable<TablePlan> UnresolvedConflicts => Tables.Where(t => t.IsUnresolvedConflict);
}
