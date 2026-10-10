using System.Globalization;

namespace DbMigrator.Core.Schema;

/// <summary>
/// Engine-neutral column types. Source providers map their native types onto these;
/// target providers map these onto theirs. Each kind has one CLR representation that row
/// readers must produce and bulk writers accept (see <see cref="CanonicalValues"/>).
/// </summary>
public enum DataTypeKind
{
    Boolean,
    /// <summary>Unsigned 8-bit integer (0–255).</summary>
    Byte,
    Int16,
    Int32,
    Int64,
    /// <summary>Exact numeric with <see cref="DataType.Precision"/> and <see cref="DataType.Scale"/>.</summary>
    Decimal,
    Single,
    Double,
    /// <summary>Fixed-length, blank-padded text of <see cref="DataType.Length"/> characters.</summary>
    FixedText,
    /// <summary>Variable-length text; a null <see cref="DataType.Length"/> means unbounded.</summary>
    Text,
    FixedBinary,
    /// <summary>Variable-length binary; a null <see cref="DataType.Length"/> means unbounded.</summary>
    Binary,
    Date,
    /// <summary>Time of day; <see cref="DataType.Precision"/> is fractional-second digits.</summary>
    Time,
    /// <summary>Date and time without a time zone; <see cref="DataType.Precision"/> is fractional-second digits.</summary>
    DateTime,
    /// <summary>Date and time with a UTC offset; <see cref="DataType.Precision"/> is fractional-second digits.</summary>
    DateTimeOffset,
    Guid,
    Json,
    Xml,
}

public sealed record DataType(DataTypeKind Kind)
{
    /// <summary>Characters for text, bytes for binary; null means unbounded.</summary>
    public int? Length { get; init; }

    /// <summary>Total digits for <see cref="DataTypeKind.Decimal"/>; fractional-second digits for time types.</summary>
    public int? Precision { get; init; }

    public int? Scale { get; init; }

    /// <summary>Whether text holds Unicode (as opposed to a single-byte code page).</summary>
    public bool IsUnicode { get; init; } = true;

    public override string ToString() => Kind switch
    {
        DataTypeKind.Decimal => Precision is null ? "Decimal" : $"Decimal({Precision},{Scale ?? 0})",
        DataTypeKind.FixedText or DataTypeKind.Text or DataTypeKind.FixedBinary or DataTypeKind.Binary =>
            $"{Kind}({(Length is null ? "max" : Length.Value.ToString(CultureInfo.InvariantCulture))})",
        DataTypeKind.Time or DataTypeKind.DateTime or DataTypeKind.DateTimeOffset when Precision is not null =>
            $"{Kind}({Precision})",
        _ => Kind.ToString(),
    };
}

/// <summary>The CLR type every provider uses for a value of each <see cref="DataTypeKind"/>.</summary>
public static class CanonicalValues
{
    public static Type ClrTypeOf(DataTypeKind kind) => kind switch
    {
        DataTypeKind.Boolean => typeof(bool),
        DataTypeKind.Byte => typeof(byte),
        DataTypeKind.Int16 => typeof(short),
        DataTypeKind.Int32 => typeof(int),
        DataTypeKind.Int64 => typeof(long),
        DataTypeKind.Decimal => typeof(decimal),
        DataTypeKind.Single => typeof(float),
        DataTypeKind.Double => typeof(double),
        DataTypeKind.FixedText or DataTypeKind.Text or DataTypeKind.Json or DataTypeKind.Xml => typeof(string),
        DataTypeKind.FixedBinary or DataTypeKind.Binary => typeof(byte[]),
        DataTypeKind.Date => typeof(DateOnly),
        DataTypeKind.Time => typeof(TimeOnly),
        // Kind is always Unspecified.
        DataTypeKind.DateTime => typeof(DateTime),
        DataTypeKind.DateTimeOffset => typeof(DateTimeOffset),
        DataTypeKind.Guid => typeof(Guid),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}
