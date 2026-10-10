using DbMigrator.Core.Schema;
using DbMigrator.Providers.SqlServer;

namespace DbMigrator.Tests.SqlServer;

public class SqlServerDefaultsTests
{
    public static TheoryData<string, object?> Literals => new()
    {
        { "((0))", 0L },
        { "((-1))", -1L },
        { "((1.5))", 1.5m },
        { "((-2.25))", -2.25m },
        { "((99999999999999999999))", 99999999999999999999m },
        { "((1e3))", 1000d },
        { "(N'abc')", "abc" },
        { "('it''s')", "it's" },
        { "('')", "" },
        { "(CONVERT([bit],(1)))", 1L },
        { "(CAST(N'2020-01-01' AS datetime2(0)))", "2020-01-01" },
        { "(NULL)", null },
    };

    [Theory]
    [MemberData(nameof(Literals))]
    public void Parses_literals(string definition, object? expected)
    {
        var literal = Assert.IsType<DefaultValue.Literal>(SqlServerDefaults.Parse(definition));
        Assert.Equal(expected, literal.Value);
    }

    [Fact]
    public void Parses_binary_literals() =>
        Assert.Equal(new byte[] { 0x00, 0xFF }, Assert.IsType<DefaultValue.Literal>(SqlServerDefaults.Parse("(0x00FF)")).Value);

    [Theory]
    [InlineData("(getdate())", DefaultFunction.CurrentDateTime)]
    [InlineData("(SYSDATETIME())", DefaultFunction.CurrentDateTime)]
    [InlineData("(CURRENT_TIMESTAMP)", DefaultFunction.CurrentDateTime)]
    [InlineData("(getutcdate())", DefaultFunction.CurrentUtcDateTime)]
    [InlineData("(sysutcdatetime())", DefaultFunction.CurrentUtcDateTime)]
    [InlineData("(sysdatetimeoffset())", DefaultFunction.CurrentDateTimeOffset)]
    [InlineData("(newid())", DefaultFunction.NewGuid)]
    [InlineData("(newsequentialid())", DefaultFunction.NewGuid)]
    [InlineData("(CONVERT([date],getdate()))", DefaultFunction.CurrentDate)]
    [InlineData("(CAST(GETUTCDATE() AS date))", DefaultFunction.CurrentUtcDate)]
    public void Parses_functions(string definition, DefaultFunction expected) =>
        Assert.Equal(expected, Assert.IsType<DefaultValue.Function>(SqlServerDefaults.Parse(definition)).Name);

    [Theory]
    [InlineData("([dbo].[NextNumber]())")]
    [InlineData("((1)+(2))")]
    [InlineData("(NEXT VALUE FOR [dbo].[Seq])")]
    [InlineData("(CONVERT([time],getdate()))")]
    [InlineData("(this is not sql")]
    public void Keeps_anything_else_verbatim(string definition) =>
        Assert.Equal(definition, Assert.IsType<DefaultValue.Expression>(SqlServerDefaults.Parse(definition)).Sql);
}
