using DbMigrator.Core.Diagnostics;
using DbMigrator.Core.Planning;
using DbMigrator.Core.Schema;

namespace DbMigrator.Core.Providers;

/// <summary>How a target engine names, types and creates things. Statements carry no trailing terminator.</summary>
public interface ISqlDialect
{
    string ProviderId { get; }

    /// <summary>Where the source's default schema lands, e.g. <c>public</c>.</summary>
    string DefaultSchema { get; }

    IdentifierLimit IdentifierLimit { get; }

    /// <summary>How the engine compares quoted identifiers.</summary>
    StringComparer IdentifierComparer { get; }

    /// <summary>Whether a unique index can treat NULLs as equal, matching SQL Server.</summary>
    bool SupportsNullsNotDistinct { get; }

    string QuoteIdentifier(string name);

    string QuoteName(QualifiedName name);

    TypeMapping MapType(DataType type);

    /// <summary>The type an identity column of <paramref name="type"/> must use, or null if it cannot be an identity.</summary>
    TypeMapping? MapIdentityType(TypeMapping type);

    DefaultRendering RenderDefault(DefaultValue value, DataType columnType);

    string CreateSchema(string schema);

    string DropTable(QualifiedName table);

    string TruncateTables(IReadOnlyList<QualifiedName> tables);

    string CreateTable(TablePlan table);

    string AddPrimaryKey(TablePlan table);

    string CreateIndex(TablePlan table, IndexPlan index);

    string AddForeignKey(TablePlan table, ForeignKeyPlan foreignKey);

    /// <summary>Moves an identity column's next value past the copied rows, or null if the engine does not need it.</summary>
    string? ResetIdentity(TablePlan table, ColumnPlan column);
}

/// <param name="Type">The target column's engine-neutral type, which may differ from the source's (e.g. reduced precision).</param>
/// <param name="SqlType">The type as written in DDL.</param>
/// <param name="Note">Set when the mapping changes or loses something.</param>
public sealed record TypeMapping(DataType Type, string SqlType, MigrationNote? Note = null);

/// <param name="Sql">The default in target SQL, or null for no default clause.</param>
/// <param name="Problem">Set when the default could not be expressed, saying why.</param>
public sealed record DefaultRendering(string? Sql, string? Problem = null)
{
    public static DefaultRendering Unsupported(string problem) => new(null, problem);
}

/// <summary>An engine's maximum identifier length, in characters or UTF-8 bytes.</summary>
public sealed record IdentifierLimit(int MaxLength, bool CountsUtf8Bytes)
{
    public int Measure(string name) => CountsUtf8Bytes ? System.Text.Encoding.UTF8.GetByteCount(name) : name.Length;

    public bool Fits(string name) => Measure(name) <= MaxLength;
}
