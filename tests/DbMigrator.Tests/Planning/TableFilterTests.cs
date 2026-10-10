using DbMigrator.Core.Planning;
using DbMigrator.Core.Schema;

namespace DbMigrator.Tests.Planning;

public class TableFilterTests
{
    private static readonly QualifiedName DboOrders = new("dbo", "Orders");
    private static readonly QualifiedName SalesOrders = new("sales", "Orders");
    private static readonly QualifiedName SalesOrderLine = new("sales", "OrderLine");
    private static readonly QualifiedName DboAudit = new("dbo", "AuditLog");

    [Fact]
    public void No_patterns_match_everything() =>
        Assert.True(TableFilter.All.Matches(DboOrders));

    [Fact]
    public void A_dotted_pattern_matches_the_schema()
    {
        var filter = new TableFilter(["sales.*"], []);

        Assert.True(filter.Matches(SalesOrders));
        Assert.True(filter.Matches(SalesOrderLine));
        Assert.False(filter.Matches(DboOrders));
    }

    [Fact]
    public void A_bare_pattern_matches_the_table_in_any_schema()
    {
        var filter = new TableFilter(["Order*"], []);

        Assert.True(filter.Matches(DboOrders));
        Assert.True(filter.Matches(SalesOrderLine));
        Assert.False(filter.Matches(DboAudit));
    }

    [Fact]
    public void Exclusions_win_and_matching_ignores_case()
    {
        var filter = new TableFilter([], ["*audit*", "SALES.orderline"]);

        Assert.True(filter.Matches(DboOrders));
        Assert.False(filter.Matches(DboAudit));
        Assert.False(filter.Matches(SalesOrderLine));
    }

    [Fact]
    public void Question_mark_matches_one_character()
    {
        var filter = new TableFilter(["dbo.Order?"], []);

        Assert.True(filter.Matches(DboOrders));
        Assert.False(filter.Matches(new QualifiedName("dbo", "Order")));
    }

    [Fact]
    public void Exactly_selects_only_the_named_tables_even_with_wildcard_characters()
    {
        var odd = new QualifiedName("dbo", "Rate*Card?");
        var filter = TableFilter.Exactly([odd, SalesOrders]);

        Assert.True(filter.Matches(odd));
        Assert.True(filter.Matches(SalesOrders));
        Assert.False(filter.Matches(new QualifiedName("dbo", "RateXCardY")));
        Assert.False(filter.Matches(new QualifiedName("sales", "orders")));
        Assert.False(filter.Matches(DboOrders));
        Assert.Equal(2, filter.Tables!.Count);
        Assert.Null(TableFilter.All.Tables);
    }
}
