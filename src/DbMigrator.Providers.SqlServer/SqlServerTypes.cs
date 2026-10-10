using System.Globalization;
using DbMigrator.Core.Diagnostics;
using DbMigrator.Core.Schema;

namespace DbMigrator.Providers.SqlServer;

/// <summary>How a column's values are selected and turned into canonical CLR values.</summary>
internal enum ReadMode
{
    /// <summary>Selected as is; SqlClient returns a value of the canonical type or close to it.</summary>
    Direct,
    /// <summary>A CLR type (hierarchyid, spatial, user-defined): selected as <c>col.ToString()</c>.</summary>
    ToStringMethod,
    /// <summary>Selected as <c>CONVERT(nvarchar(max), col)</c>, for types SqlClient may not read.</summary>
    ConvertToText,
    /// <summary><c>sql_variant</c>: read as whatever SqlClient returns and formatted as text.</summary>
    Variant,
}

/// <param name="NativeType">Display form, e.g. <c>nvarchar(max)</c>; user CLR types are schema-qualified.</param>
internal sealed record SqlServerColumnType(DataType Type, string NativeType, ReadMode Mode, MigrationNote? Note = null);

internal static class SqlServerTypes
{
    /// <param name="typeName">The system type name, or for a CLR type its schema-qualified name.</param>
    /// <param name="maxLength">Bytes, as <c>sys.columns.max_length</c> reports it; -1 for (max).</param>
    public static SqlServerColumnType Map(string typeName, short maxLength, byte precision, byte scale, bool isUserClrType)
    {
        var name = typeName.ToLowerInvariant();
        if (isUserClrType)
        {
            return Text(typeName, ReadMode.ToStringMethod,
                $"CLR type {typeName} is copied as the text its ToString() returns.");
        }

        switch (name)
        {
            case "bit": return Simple(DataTypeKind.Boolean, name);
            case "tinyint": return Simple(DataTypeKind.Byte, name);
            case "smallint": return Simple(DataTypeKind.Int16, name);
            case "int": return Simple(DataTypeKind.Int32, name);
            case "bigint": return Simple(DataTypeKind.Int64, name);
            case "real": return Simple(DataTypeKind.Single, name);
            case "float": return Simple(DataTypeKind.Double, name);
            case "date": return Simple(DataTypeKind.Date, name);
            case "uniqueidentifier": return Simple(DataTypeKind.Guid, name);
            case "xml": return Simple(DataTypeKind.Xml, name);

            case "decimal" or "numeric":
                var note = precision > 28
                    ? new MigrationNote(IssueSeverity.Warning, IssueCodes.DecimalRange,
                        $"{name}({precision},{scale}) can hold more than the 28 significant digits .NET can carry; a row with such a value stops this table's copy.")
                    : null;
                return new(new DataType(DataTypeKind.Decimal) { Precision = precision, Scale = scale }, $"{name}({precision},{scale})", ReadMode.Direct, note);
            case "money": return new(new DataType(DataTypeKind.Decimal) { Precision = 19, Scale = 4 }, name, ReadMode.Direct);
            case "smallmoney": return new(new DataType(DataTypeKind.Decimal) { Precision = 10, Scale = 4 }, name, ReadMode.Direct);

            case "char": return Characters(DataTypeKind.FixedText, name, maxLength, unicode: false);
            case "nchar": return Characters(DataTypeKind.FixedText, name, maxLength, unicode: true);
            case "varchar": return Characters(DataTypeKind.Text, name, maxLength, unicode: false);
            case "nvarchar": return Characters(DataTypeKind.Text, name, maxLength, unicode: true);
            case "text": return new(new DataType(DataTypeKind.Text) { IsUnicode = false }, name, ReadMode.Direct);
            case "ntext": return new(new DataType(DataTypeKind.Text), name, ReadMode.Direct);

            case "binary": return new(new DataType(DataTypeKind.FixedBinary) { Length = maxLength }, $"binary({maxLength})", ReadMode.Direct);
            case "varbinary":
                return new(new DataType(DataTypeKind.Binary) { Length = maxLength == -1 ? null : maxLength }, $"varbinary({Size(maxLength)})", ReadMode.Direct);
            case "image": return new(new DataType(DataTypeKind.Binary), name, ReadMode.Direct);
            case "timestamp" or "rowversion":
                return new(new DataType(DataTypeKind.FixedBinary) { Length = 8 }, "rowversion", ReadMode.Direct,
                    new(IssueSeverity.Info, IssueCodes.RowVersion,
                        "rowversion values are copied as plain binary into a nullable column; they will no longer be set when a row is inserted or updated."));

            case "time": return new(new DataType(DataTypeKind.Time) { Precision = scale }, $"time({scale})", ReadMode.Direct);
            case "datetime": return new(new DataType(DataTypeKind.DateTime) { Precision = 3 }, name, ReadMode.Direct);
            case "smalldatetime": return new(new DataType(DataTypeKind.DateTime) { Precision = 0 }, name, ReadMode.Direct);
            case "datetime2": return new(new DataType(DataTypeKind.DateTime) { Precision = scale }, $"datetime2({scale})", ReadMode.Direct);
            case "datetimeoffset": return new(new DataType(DataTypeKind.DateTimeOffset) { Precision = scale }, $"datetimeoffset({scale})", ReadMode.Direct);

            case "json": return new(new DataType(DataTypeKind.Json), name, ReadMode.ConvertToText);
            case "sql_variant":
                return Text(name, ReadMode.Variant, "sql_variant values are copied as text.");
            case "hierarchyid":
                return Text(name, ReadMode.ToStringMethod, "hierarchyid values are copied as text paths such as /1/3/.");
            case "geometry" or "geography":
                return Text(name, ReadMode.ToStringMethod, $"{name} values are copied as well-known text (WKT), without their SRID.");
            default:
                return Text(name, ReadMode.ConvertToText, $"Type {name} is not recognised and is copied as text.");
        }
    }

    /// <summary>The read mode for a column described by its <see cref="SqlServerColumnType.NativeType"/>.</summary>
    public static ReadMode ReadModeOf(string nativeType)
    {
        var paren = nativeType.IndexOf('(', StringComparison.Ordinal);
        var baseName = paren < 0 ? nativeType : nativeType[..paren];
        // CLR types are reported schema-qualified; system type names never contain a dot.
        var isUserClrType = baseName.Contains('.', StringComparison.Ordinal);
        return Map(baseName, maxLength: 0, precision: 0, scale: 0, isUserClrType).Mode;
    }

    private static SqlServerColumnType Simple(DataTypeKind kind, string name) => new(new DataType(kind), name, ReadMode.Direct);

    private static SqlServerColumnType Characters(DataTypeKind kind, string name, short maxLength, bool unicode)
    {
        // max_length counts bytes; n-types store two per character.
        int? length = maxLength == -1 ? null : unicode ? maxLength / 2 : maxLength;
        var display = length is null ? "max" : length.Value.ToString(CultureInfo.InvariantCulture);
        return new(new DataType(kind) { Length = length, IsUnicode = unicode }, $"{name}({display})", ReadMode.Direct);
    }

    private static SqlServerColumnType Text(string nativeType, ReadMode mode, string message) =>
        new(new DataType(DataTypeKind.Text), nativeType, mode, new MigrationNote(IssueSeverity.Warning, IssueCodes.UnsupportedType, message));

    private static string Size(short maxLength) => maxLength == -1 ? "max" : maxLength.ToString(CultureInfo.InvariantCulture);
}
