using DbMigrator.Core.Providers;
using DbMigrator.Providers;

namespace DbMigrator.Tests.Providers;

public class ConnectionStringFormatTests
{
    private static ConnectionStringFormat SqlServer => BuiltInProviders.Catalog.Get("sqlserver").Connection;

    private static ConnectionStringFormat Postgres => BuiltInProviders.Catalog.Get("postgresql").Connection;

    [Fact]
    public void Parse_resolves_driver_synonyms_into_fields()
    {
        var parsed = SqlServer.Parse("server=sql01,1433;database=Shop;uid=migrator;pwd=hunter2;Encrypt=Mandatory;MultiSubnetFailover=True");

        Assert.Equal("sql01,1433", parsed.Values["Data Source"]);
        Assert.Equal("Shop", parsed.Values["Initial Catalog"]);
        Assert.Equal("migrator", parsed.Values["User ID"]);
        Assert.Equal("hunter2", parsed.Values["Password"]);
        Assert.Equal("True", parsed.Values["Encrypt"]);
        Assert.Equal("Multi Subnet Failover=True", parsed.AdditionalSettings);
    }

    [Theory]
    [InlineData("Host=pg01;Username=u;PSW=hunter2")]
    [InlineData("Host=pg01;Username=u;pwd=hunter2")]
    [InlineData("Host=pg01;Username=u;Password=hunter2;SSL Password=keypass;Include Error Detail=true")]
    public void Additional_settings_never_hold_a_secret(string connectionString)
    {
        var parsed = Postgres.Parse(connectionString);

        Assert.Equal("hunter2", parsed.Values["Password"]);
        Assert.DoesNotContain("hunter2", parsed.AdditionalSettings, StringComparison.Ordinal);
        Assert.DoesNotContain("keypass", parsed.AdditionalSettings, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_and_parse_round_trip()
    {
        var values = new Dictionary<string, string>
        {
            ["Host"] = "pg01",
            ["Port"] = "5433",
            ["Database"] = "shop",
            ["Username"] = "migrator",
            ["Password"] = "p;w'd=x",
            ["SSL Mode"] = "VerifyFull",
            ["Root Certificate"] = "/etc/ca.pem",
        };

        var built = Postgres.Build(values, "Include Error Detail=true");
        var parsed = Postgres.Parse(built);

        foreach (var (keyword, value) in values)
        {
            Assert.Equal(value, parsed.Values[keyword]);
        }

        Assert.Equal("Include Error Detail=True", parsed.AdditionalSettings);
    }

    [Fact]
    public void Build_skips_hidden_fields_and_lets_fields_win_over_additional_settings()
    {
        var values = new Dictionary<string, string>
        {
            ["Data Source"] = "sql01",
            ["Initial Catalog"] = "Shop",
            ["Integrated Security"] = "True",
            ["User ID"] = "ignored",
            ["Password"] = "ignored",
        };

        var parsed = SqlServer.Parse(SqlServer.Build(values, "Password=leaked;Data Source=elsewhere"));

        Assert.Equal("sql01", parsed.Values["Data Source"]);
        Assert.False(parsed.Values.ContainsKey("User ID"));
        Assert.False(parsed.Values.ContainsKey("Password"));
    }

    [Theory]
    [InlineData("sqlserver", "Server=x;Bogus=1")]
    [InlineData("postgresql", "Host=x;Bogus=1")]
    [InlineData("postgresql", "Host=x;SSL Mode=verify-full")]
    public void Unknown_keywords_and_values_are_rejected(string provider, string connectionString) =>
        Assert.ThrowsAny<ArgumentException>(() => BuiltInProviders.Catalog.Get(provider).Connection.Parse(connectionString));

    [Fact]
    public void Build_names_the_field_a_driver_rejects()
    {
        var error = Assert.ThrowsAny<ArgumentException>(() =>
            Postgres.Build(new Dictionary<string, string> { ["Host"] = "x", ["Port"] = "not a number" }, ""));

        Assert.Equal("Port", error.ParamName);
    }

    [Fact]
    public void Redact_removes_every_secret()
    {
        var redacted = Postgres.Redact("Host=pg01;Username=u;Password=hunter2;SSL Password=keypass");

        Assert.Equal("Host=pg01;Username=u", redacted);
        Assert.Equal("Data Source=sql01", SqlServer.Redact("Data Source=sql01;pwd=hunter2"));
    }

    [Fact]
    public void Visibility_and_validation_follow_conditions()
    {
        var integrated = new Dictionary<string, string> { ["Data Source"] = "sql01", ["Initial Catalog"] = "Shop", ["Integrated Security"] = "True" };
        var sqlLogin = new Dictionary<string, string> { ["Data Source"] = "sql01", ["Initial Catalog"] = "Shop" };
        var user = SqlServer.FindField("User ID")!;

        Assert.False(SqlServer.IsVisible(user, integrated));
        Assert.True(SqlServer.IsVisible(user, sqlLogin));
        Assert.Empty(SqlServer.Validate(integrated));
        Assert.Equal(["User ID"], SqlServer.Validate(sqlLogin).Select(e => e.Keyword));
        Assert.Contains(Postgres.Validate(new Dictionary<string, string> { ["Host"] = "h", ["Database"] = "d", ["Username"] = "u", ["Port"] = "70000" }),
            e => e.Keyword == "Port");
    }

    [Fact]
    public void Describe_reads_like_a_sentence_and_never_shows_a_password()
    {
        var values = new Dictionary<string, string> { ["Host"] = "pg01", ["Port"] = "5433", ["Database"] = "shop", ["Username"] = "migrator", ["Password"] = "hunter2" };

        Assert.Equal("shop on pg01:5433 as migrator", Postgres.Describe(values));
        Assert.Equal("Shop on sql01", SqlServer.Describe(new Dictionary<string, string> { ["Data Source"] = "sql01", ["Initial Catalog"] = "Shop", ["Integrated Security"] = "True", ["User ID"] = "hidden" }));
    }

    [Theory]
    [InlineData("sqlserver")]
    [InlineData("postgresql")]
    public void Every_field_keyword_is_the_drivers_canonical_spelling(string provider)
    {
        var format = BuiltInProviders.Catalog.Get(provider).Connection;
        foreach (var field in format.Fields)
        {
            var value = field.Kind switch
            {
                ConnectionFieldKind.Integer => "5",
                ConnectionFieldKind.Toggle => "True",
                ConnectionFieldKind.Choice => field.Choices[0].Value,
                _ => "x",
            };
            var values = new Dictionary<string, string> { [field.Keyword] = value };
            if (field.VisibleWhen is { } condition)
            {
                values[condition.Keyword] = condition.Values[0];
            }

            var built = format.Build(values, "");

            Assert.Contains(field.Keyword + "=", built, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("sqlserver")]
    [InlineData("postgresql")]
    public void Choice_values_survive_the_drivers_round_trip(string provider)
    {
        var format = BuiltInProviders.Catalog.Get(provider).Connection;
        foreach (var field in format.Fields.Where(f => f.Kind == ConnectionFieldKind.Choice))
        {
            foreach (var choice in field.Choices)
            {
                var parsed = format.Parse(format.Build(new Dictionary<string, string> { [field.Keyword] = choice.Value }, ""));
                Assert.Equal(choice.Value, parsed.Values[field.Keyword]);
            }
        }
    }
}
