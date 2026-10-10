using DbMigrator.Core.Diagnostics;
using DbMigrator.Core.Schema;
using DbMigrator.Providers.SqlServer;

namespace DbMigrator.Tests.SqlServer;

public class SqlServerTypesTests
{
    [Theory]
    [InlineData("bit", 1, 1, 0, DataTypeKind.Boolean, null, "bit")]
    [InlineData("tinyint", 1, 3, 0, DataTypeKind.Byte, null, "tinyint")]
    [InlineData("int", 4, 10, 0, DataTypeKind.Int32, null, "int")]
    [InlineData("float", 8, 53, 0, DataTypeKind.Double, null, "float")]
    [InlineData("real", 4, 24, 0, DataTypeKind.Single, null, "real")]
    [InlineData("char", 10, 0, 0, DataTypeKind.FixedText, 10, "char(10)")]
    [InlineData("nchar", 10, 0, 0, DataTypeKind.FixedText, 5, "nchar(5)")]
    [InlineData("varchar", 50, 0, 0, DataTypeKind.Text, 50, "varchar(50)")]
    [InlineData("nvarchar", 100, 0, 0, DataTypeKind.Text, 50, "nvarchar(50)")]
    [InlineData("nvarchar", -1, 0, 0, DataTypeKind.Text, null, "nvarchar(max)")]
    [InlineData("ntext", 16, 0, 0, DataTypeKind.Text, null, "ntext")]
    [InlineData("varbinary", -1, 0, 0, DataTypeKind.Binary, null, "varbinary(max)")]
    [InlineData("binary", 4, 0, 0, DataTypeKind.FixedBinary, 4, "binary(4)")]
    [InlineData("uniqueidentifier", 16, 0, 0, DataTypeKind.Guid, null, "uniqueidentifier")]
    [InlineData("xml", -1, 0, 0, DataTypeKind.Xml, null, "xml")]
    public void Maps_lengths_and_kinds(string name, short maxLength, byte precision, byte scale, DataTypeKind kind, int? length, string native)
    {
        var mapped = SqlServerTypes.Map(name, maxLength, precision, scale, isUserClrType: false);

        Assert.Equal(kind, mapped.Type.Kind);
        Assert.Equal(length, mapped.Type.Length);
        Assert.Equal(native, mapped.NativeType);
        Assert.Equal(ReadMode.Direct, mapped.Mode);
        Assert.Null(mapped.Note);
    }

    [Theory]
    [InlineData("datetime", 3, 3)]
    [InlineData("smalldatetime", 0, 0)]
    [InlineData("datetime2", 7, 7)]
    [InlineData("datetime2", 0, 0)]
    public void Maps_datetime_precision(string name, byte scale, int expected)
    {
        var mapped = SqlServerTypes.Map(name, 8, 0, scale, isUserClrType: false);

        Assert.Equal(DataTypeKind.DateTime, mapped.Type.Kind);
        Assert.Equal(expected, mapped.Type.Precision);
    }

    [Fact]
    public void Maps_money_to_fixed_decimal()
    {
        var money = SqlServerTypes.Map("money", 8, 19, 4, isUserClrType: false).Type;

        Assert.Equal(new DataType(DataTypeKind.Decimal) { Precision = 19, Scale = 4 }, money);
    }

    [Fact]
    public void Warns_about_decimals_wider_than_dotnet()
    {
        Assert.Null(SqlServerTypes.Map("decimal", 17, 28, 2, isUserClrType: false).Note);
        Assert.Equal(IssueCodes.DecimalRange, SqlServerTypes.Map("decimal", 17, 38, 2, isUserClrType: false).Note?.Code);
    }

    [Theory]
    [InlineData("hierarchyid", false, nameof(ReadMode.ToStringMethod))]
    [InlineData("geography", false, nameof(ReadMode.ToStringMethod))]
    [InlineData("geometry", false, nameof(ReadMode.ToStringMethod))]
    [InlineData("dbo.MyClrType", true, nameof(ReadMode.ToStringMethod))]
    [InlineData("sql_variant", false, nameof(ReadMode.Variant))]
    [InlineData("vector", false, nameof(ReadMode.ConvertToText))]
    public void Copies_types_without_an_equivalent_as_text(string name, bool isClr, string expectedMode)
    {
        var mode = Enum.Parse<ReadMode>(expectedMode);
        var mapped = SqlServerTypes.Map(name, -1, 0, 0, isClr);

        Assert.Equal(DataTypeKind.Text, mapped.Type.Kind);
        Assert.Equal(mode, mapped.Mode);
        Assert.Equal(IssueCodes.UnsupportedType, mapped.Note?.Code);
        Assert.Equal(mode, SqlServerTypes.ReadModeOf(mapped.NativeType));
    }

    [Fact]
    public void Rowversion_is_binary_with_a_note()
    {
        var mapped = SqlServerTypes.Map("timestamp", 8, 0, 0, isUserClrType: false);

        Assert.Equal(new DataType(DataTypeKind.FixedBinary) { Length = 8 }, mapped.Type);
        Assert.Equal("rowversion", mapped.NativeType);
        Assert.Equal(IssueCodes.RowVersion, mapped.Note?.Code);
    }

    [Fact]
    public void Formats_sql_variant_values_invariantly()
    {
        Assert.Equal("42", SqlServerRowReader.FormatVariant(42));
        Assert.Equal("1.5", SqlServerRowReader.FormatVariant(1.5m));
        Assert.Equal("2024-01-02 03:04:05.5", SqlServerRowReader.FormatVariant(new DateTime(2024, 1, 2, 3, 4, 5, 500)));
        Assert.Equal("2024-01-02", SqlServerRowReader.FormatVariant(new DateTime(2024, 1, 2)).Split(' ')[0]);
        Assert.Equal("0x00FF", SqlServerRowReader.FormatVariant(new byte[] { 0, 255 }));
    }
}
