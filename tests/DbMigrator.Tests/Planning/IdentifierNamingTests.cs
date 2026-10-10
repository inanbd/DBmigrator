using System.Text;
using DbMigrator.Core.Planning;
using DbMigrator.Core.Providers;

namespace DbMigrator.Tests.Planning;

public class IdentifierNamingTests
{
    private static readonly IdentifierLimit PostgresLimit = new(63, CountsUtf8Bytes: true);

    [Theory]
    [InlineData("OrderID", "order_id")]
    [InlineData("HTMLParser", "html_parser")]
    [InlineData("Order Details", "order_details")]
    [InlineData("customer_id", "customer_id")]
    [InlineData("Address2Line", "address2_line")]
    [InlineData("ID", "id")]
    [InlineData("_RowVersion", "_row_version")]
    [InlineData("Name ", "name")]
    [InlineData("A_Very_Long", "a_very_long")]
    [InlineData("pre-tax-amount", "pre_tax_amount")]
    [InlineData("trailing_", "trailing_")]
    public void ToSnakeCase_splits_words_and_lowercases(string input, string expected) =>
        Assert.Equal(expected, IdentifierNaming.ToSnakeCase(input));

    [Theory]
    [InlineData(IdentifierCase.Preserve, "OrderID")]
    [InlineData(IdentifierCase.Lower, "orderid")]
    [InlineData(IdentifierCase.SnakeCase, "order_id")]
    public void ApplyCase_follows_the_option(IdentifierCase identifierCase, string expected) =>
        Assert.Equal(expected, IdentifierNaming.ApplyCase("OrderID", identifierCase));

    [Fact]
    public void Shorten_leaves_names_that_fit_alone() =>
        Assert.Equal("orders", IdentifierNaming.Shorten("orders", PostgresLimit));

    [Fact]
    public void Shorten_keeps_distinct_long_names_distinct()
    {
        var first = IdentifierNaming.Shorten(new string('a', 70) + "_one", PostgresLimit);
        var second = IdentifierNaming.Shorten(new string('a', 70) + "_two", PostgresLimit);

        Assert.NotEqual(first, second);
        Assert.True(PostgresLimit.Fits(first));
        Assert.True(PostgresLimit.Fits(second));
        Assert.StartsWith("aaaa", first);
    }

    [Fact]
    public void Shorten_counts_bytes_and_never_splits_a_character()
    {
        var name = string.Concat(Enumerable.Repeat("é🎉", 20)); // 6 bytes per pair

        var shortened = IdentifierNaming.Shorten(name, PostgresLimit);

        Assert.True(Encoding.UTF8.GetByteCount(shortened) <= 63);
        Assert.DoesNotContain('�', Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(shortened)));
    }

    [Fact]
    public void NameScope_adds_a_suffix_on_clashes()
    {
        var scope = new NameScope(PostgresLimit, StringComparer.Ordinal);
        scope.Reserve("orders");

        Assert.Equal("ix_code", scope.Allocate("ix_code"));
        Assert.Equal("ix_code_1", scope.Allocate("ix_code"));
        Assert.Equal("ix_code_2", scope.Allocate("ix_code"));
        Assert.Equal("orders_1", scope.Allocate("orders"));
    }

    [Fact]
    public void NameScope_suffixes_still_fit_the_limit()
    {
        var scope = new NameScope(PostgresLimit, StringComparer.Ordinal);
        var name = new string('x', 63);

        var first = scope.Allocate(name);
        var second = scope.Allocate(name);

        Assert.Equal(name, first);
        Assert.EndsWith("_1", second);
        Assert.True(PostgresLimit.Fits(second));
    }
}
