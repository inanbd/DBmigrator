namespace DbMigrator.Core.Diagnostics;

public enum IssueSeverity
{
    /// <summary>Behaviour changes the user should know about but need not act on.</summary>
    Info,
    /// <summary>Something was dropped or altered and may need manual follow-up.</summary>
    Warning,
    /// <summary>The migration cannot run until this is resolved.</summary>
    Error,
}

/// <param name="Code">Stable identifier for grouping similar issues (see <see cref="IssueCodes"/>).</param>
/// <param name="Subject">The object concerned, e.g. <c>dbo.Orders.Total</c>.</param>
public sealed record MigrationIssue(IssueSeverity Severity, string Code, string Subject, string Message)
{
    public override string ToString() => $"{Severity} [{Code}] {Subject}: {Message}";
}

/// <summary>A note produced without knowing which object it is about; the caller attaches the subject.</summary>
public sealed record MigrationNote(IssueSeverity Severity, string Code, string Message)
{
    public MigrationIssue For(string subject) => new(Severity, Code, subject, Message);
}

public static class IssueCodes
{
    public const string UnsupportedType = "unsupported-type";
    public const string PrecisionReduced = "precision-reduced";
    public const string OffsetDropped = "offset-dropped";
    public const string TypeChanged = "type-changed";
    public const string ComputedColumn = "computed-column";
    public const string RowVersion = "rowversion";
    public const string DefaultNotTranslated = "default-not-translated";
    public const string IdentityDropped = "identity-dropped";
    public const string CheckConstraint = "check-constraint";
    public const string FilteredIndex = "filtered-index";
    public const string UnsupportedIndex = "unsupported-index";
    public const string DisabledIndex = "disabled-index";
    public const string NullsDistinct = "nulls-distinct";
    public const string ForeignKeyNotValidated = "fk-not-validated";
    public const string ForeignKeyTargetMissing = "fk-target-missing";
    public const string NameShortened = "name-shortened";
    public const string NameDeduplicated = "name-deduplicated";
    public const string NameCollision = "name-collision";
    public const string TableExists = "table-exists";
    public const string DropCascade = "drop-cascade";
    public const string CaseSensitivity = "case-sensitivity";
    public const string DecimalRange = "decimal-range";
    public const string NulCharacterRemoved = "nul-removed";
    public const string SkippedColumn = "skipped-column";
    public const string NoTables = "no-tables";
    public const string LimitedCatalogAccess = "limited-catalog-access";
    public const string MissingPrivilege = "missing-privilege";
    public const string SystemDatabase = "system-database";
}
