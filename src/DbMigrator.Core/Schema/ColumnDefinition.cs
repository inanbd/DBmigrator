namespace DbMigrator.Core.Schema;

public sealed record ColumnDefinition
{
    public required string Name { get; init; }

    public required DataType Type { get; init; }

    /// <summary>The type as the source declares it, e.g. <c>nvarchar(50)</c>. Shown to users and used by the source provider.</summary>
    public required string NativeType { get; init; }

    public bool IsNullable { get; init; } = true;

    public IdentitySpec? Identity { get; init; }

    public DefaultValue? Default { get; init; }

    /// <summary>Expression of a computed column, in the source's SQL dialect.</summary>
    public string? ComputedExpression { get; init; }

    public string? Collation { get; init; }
}

/// <summary>An auto-incrementing column's first value and step.</summary>
public sealed record IdentitySpec(long Seed, long Increment);

/// <summary>
/// A column default, parsed into an engine-neutral form where the source provider could
/// recognise it, so targets can render it in their own dialect.
/// </summary>
public abstract record DefaultValue
{
    private DefaultValue()
    {
    }

    /// <summary>A constant: null, <see cref="bool"/>, <see cref="long"/>, <see cref="decimal"/>, <see cref="double"/>, <see cref="string"/> or <see cref="byte"/>[].</summary>
    public sealed record Literal(object? Value) : DefaultValue;

    public sealed record Function(DefaultFunction Name) : DefaultValue;

    /// <summary>Anything else, kept verbatim in the source dialect.</summary>
    public sealed record Expression(string Sql) : DefaultValue;
}

public enum DefaultFunction
{
    /// <summary>The server's local date and time (SQL Server <c>GETDATE()</c>).</summary>
    CurrentDateTime,
    /// <summary>The current UTC date and time (SQL Server <c>GETUTCDATE()</c>).</summary>
    CurrentUtcDateTime,
    /// <summary>The current date and time with offset (SQL Server <c>SYSDATETIMEOFFSET()</c>).</summary>
    CurrentDateTimeOffset,
    CurrentDate,
    CurrentUtcDate,
    NewGuid,
}
