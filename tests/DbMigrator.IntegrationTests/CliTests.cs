using DbMigrator.Cli;

namespace DbMigrator.IntegrationTests;

public class CliTests(DatabaseServers servers)
{
    private static async Task<(int ExitCode, string Output)> RunAsync(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = await CliApp.RunAsync(args, output, error, TestContext.Current.CancellationToken);
        return (exitCode, output + error.ToString());
    }

    [Fact]
    public async Task Lists_providers()
    {
        var (exitCode, output) = await RunAsync("providers");

        Assert.Equal(CliApp.Success, exitCode);
        Assert.Contains("sqlserver", output);
        Assert.Contains("postgresql", output);
    }

    [Fact]
    public async Task Plans_offline_and_prints_the_script()
    {
        var source = await servers.CreateSqlServerDatabaseAsync("shop.sql");

        var (exitCode, output) = await RunAsync("plan", "--from", "sqlserver", "--source", source, "--to", "postgresql", "--output", "-");

        Assert.Equal(CliApp.Success, exitCode);
        Assert.Contains("dbo.Customers  ->  public.customers  [create]", output);
        Assert.Contains("CREATE TABLE \"public\".\"customers\"", output);
        Assert.Contains("ADD CONSTRAINT \"fk_orders_customers\" FOREIGN KEY", output);
    }

    [Fact]
    public async Task Migrates_and_refuses_to_overwrite_without_being_told()
    {
        var source = await servers.CreateSqlServerDatabaseAsync("shop.sql");
        var target = await servers.CreatePostgresDatabaseAsync();
        string[] migrate = ["migrate", "--from", "sqlserver", "--source", source, "--to", "postgresql", "--target", target];

        var first = await RunAsync(migrate);
        var second = await RunAsync(migrate);
        var third = await RunAsync([.. migrate, "--if-exists", "drop"]);

        Assert.True(first.ExitCode == CliApp.Success, first.Output);
        Assert.Contains("Migration succeeded.", first.Output);
        Assert.Equal(CliApp.Failure, second.ExitCode);
        Assert.Contains("[table-exists] dbo.Customers", second.Output);
        Assert.Contains("nothing was changed", second.Output);
        Assert.True(third.ExitCode == CliApp.Success, third.Output);
        Assert.Equal(3, await Postgres.CountAsync(target, "public.orders"));
    }

    [Fact]
    public async Task Rejects_a_bad_schema_map()
    {
        var (exitCode, output) = await RunAsync("plan", "--from", "sqlserver", "--source", "Server=unused", "--to", "postgresql", "--schema-map", "dbo");

        Assert.Equal(CliApp.Failure, exitCode);
        Assert.Contains("--schema-map expects source=target", output);
    }
}
