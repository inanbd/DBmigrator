using DbMigrator.Core.Diagnostics;
using DbMigrator.Core.Execution;
using DbMigrator.Core.Planning;
using Npgsql;

namespace DbMigrator.IntegrationTests;

/// <summary>Migrates Sql/kitchen-sink.sql once (snake_case names) and checks the result from several angles.</summary>
public sealed class KitchenSinkMigration(DatabaseServers servers) : IAsyncLifetime
{
    public string Target { get; private set; } = "";

    public MigrationReport Report { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        var source = await servers.CreateSqlServerDatabaseAsync("kitchen-sink.sql");
        Target = await servers.CreatePostgresDatabaseAsync();
        Report = await Migrations.RunAsync(source, Target, new MigrationOptions
        {
            Naming = new NamingOptions { Case = IdentifierCase.SnakeCase },
        });
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public class KitchenSinkTests(KitchenSinkMigration migration) : IClassFixture<KitchenSinkMigration>
{
    private string Target => migration.Target;

    [Fact]
    public void Copies_every_table_and_row()
    {
        Migrations.AssertSucceeded(migration.Report);
        var rows = migration.Report.Tables.ToDictionary(t => t.Table.Source.Name.ToString(), t => t.RowsCopied);

        Assert.Equal(2, rows["dbo.AllTypes"]);
        Assert.Equal(3, rows["dbo.Customers"]);
        Assert.Equal(3, rows["sales.Order Header"]);
        Assert.Equal(3, rows["sales.OrderLine"]);
        Assert.All(migration.Report.Tables, t => Assert.Equal(t.SourceRowCount, t.TargetRowCount));
        Assert.DoesNotContain("dbo.sysdiagrams", rows.Keys);
    }

    [Fact]
    public void Reports_everything_it_could_not_carry_over_exactly()
    {
        var codes = migration.Report.Issues.Select(i => i.Code).ToHashSet();

        Assert.DoesNotContain(migration.Report.Issues, i => i.Severity == IssueSeverity.Error);
        Assert.Subset(codes, new HashSet<string>
        {
            IssueCodes.FilteredIndex,
            IssueCodes.CheckConstraint,
            IssueCodes.ComputedColumn,
            IssueCodes.ForeignKeyNotValidated,
            IssueCodes.UnsupportedType,
            IssueCodes.OffsetDropped,
            IssueCodes.PrecisionReduced,
            IssueCodes.RowVersion,
            IssueCodes.CaseSensitivity,
            IssueCodes.NulCharacterRemoved,
            IssueCodes.NameShortened,
            IssueCodes.NameDeduplicated,
        });
        Assert.Equal(
            ["dbo.AllTypes.c_geography", "dbo.AllTypes.c_hierarchyid", "dbo.AllTypes.c_sql_variant"],
            migration.Report.Issues.Where(i => i.Code == IssueCodes.UnsupportedType).Select(i => i.Subject).Order());
    }

    [Fact]
    public async Task Creates_columns_with_mapped_types()
    {
        var types = await Postgres.PairsAsync(Target,
            "SELECT attname, format_type(atttypid, atttypmod) FROM pg_attribute WHERE attrelid = 'public.all_types'::regclass AND attnum > 0 AND NOT attisdropped");

        Assert.Equal("boolean", types["c_bit"]);
        Assert.Equal("smallint", types["c_tinyint"]);
        Assert.Equal("bigint", types["c_bigint"]);
        Assert.Equal("numeric(28,10)", types["c_decimal"]);
        Assert.Equal("numeric(19,4)", types["c_money"]);
        Assert.Equal("real", types["c_real"]);
        Assert.Equal("double precision", types["c_float"]);
        Assert.Equal("character(10)", types["c_char"]);
        Assert.Equal("character varying(50)", types["c_nvarchar"]);
        Assert.Equal("text", types["c_nvarcharmax"]);
        Assert.Equal("bytea", types["c_varbinary"]);
        Assert.Equal("date", types["c_date"]);
        Assert.Equal("time(6) without time zone", types["c_time"]);
        Assert.Equal("timestamp(3) without time zone", types["c_datetime"]);
        Assert.Equal("timestamp(6) without time zone", types["c_datetime2"]);
        Assert.Equal("timestamp(0) without time zone", types["c_smalldatetime"]);
        Assert.Equal("timestamp(6) with time zone", types["c_datetimeoffset"]);
        Assert.Equal("uuid", types["c_uniqueidentifier"]);
        Assert.Equal("xml", types["c_xml"]);
        Assert.Equal("text", types["c_geography"]);
        Assert.Equal("character varying(128)", types["c_sysname"]);
    }

    [Fact]
    public async Task Copies_values_exactly_including_edge_values()
    {
        await using var connection = new NpgsqlConnection(Target);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("SELECT * FROM public.all_types WHERE id = 2", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        T Get<T>(string column) => reader.GetFieldValue<T>(reader.GetOrdinal(column));

        Assert.True(Get<bool>("c_bit"));
        Assert.Equal(255, Get<short>("c_tinyint"));
        Assert.Equal(short.MinValue, Get<short>("c_smallint"));
        Assert.Equal(int.MaxValue, Get<int>("c_int"));
        Assert.Equal(long.MinValue, Get<long>("c_bigint"));
        Assert.Equal(123456789012345678.1234567890m, Get<decimal>("c_decimal"));
        Assert.Equal(999.99m, Get<decimal>("c_numeric"));
        Assert.Equal(922337203685477.5807m, Get<decimal>("c_money"));
        Assert.Equal(-214748.3648m, Get<decimal>("c_smallmoney"));
        Assert.Equal(3.25f, Get<float>("c_real"));
        Assert.Equal(2.718281828459045, Get<double>("c_float"));
        Assert.Equal("abc       ", Get<string>("c_char"));
        Assert.Equal("hello", Get<string>("c_varchar"));
        Assert.Equal(new string('x', 10000), Get<string>("c_varcharmax"));
        Assert.Equal("ñ    ", Get<string>("c_nchar"));
        Assert.Equal("Grüße 🎉", Get<string>("c_nvarchar"));
        Assert.Equal("line1line2", Get<string>("c_nvarcharmax"));
        Assert.Equal("legacy text", Get<string>("c_text"));
        Assert.Equal("legacy ntext ✓", Get<string>("c_ntext"));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, Get<byte[]>("c_binary"));
        Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, Get<byte[]>("c_varbinary"));
        Assert.Equal(10000, Get<byte[]>("c_varbinarymax").Length);
        Assert.Equal(new byte[] { 0xCA, 0xFE }, Get<byte[]>("c_image"));
        Assert.Equal(DateOnly.MinValue, Get<DateOnly>("c_date"));
        Assert.Equal(new DateTime(9999, 12, 31, 23, 59, 59, 999, 999), Get<DateTime>("c_datetime2"));
        Assert.Equal(new TimeOnly(23, 59, 59, 123, 456), Get<TimeOnly>("c_time"));
        Assert.Equal(new DateTime(1753, 1, 1, 0, 0, 0, 3), Get<DateTime>("c_datetime"));
        Assert.Equal(new DateTime(2079, 6, 6, 23, 59, 0), Get<DateTime>("c_smalldatetime"));
        Assert.Equal(new DateTimeOffset(2024, 2, 29, 7, 4, 56, 123, 456, TimeSpan.Zero), Get<DateTimeOffset>("c_datetimeoffset"));
        Assert.Equal(Guid.Parse("6F9619FF-8B86-D011-B42D-00C04FC964FF"), Get<Guid>("c_uniqueidentifier"));
        Assert.Equal("<root><item id=\"1\">x</item></root>", Get<string>("c_xml"));
        Assert.Equal("42", Get<string>("c_sql_variant"));
        Assert.Equal("/1/3/", Get<string>("c_hierarchyid"));
        Assert.Equal("POINT (-122.349 47.651)", Get<string>("c_geography"));
        Assert.Equal("my_sysname", Get<string>("c_sysname"));
    }

    [Fact]
    public async Task Stores_extreme_dates_as_real_values_not_infinity()
    {
        // Read as text: Npgsql maps ±infinity back to MinValue/MaxValue, which would hide the bug.
        var values = await Postgres.PairsAsync(Target,
            "SELECT 'date', c_date::text FROM public.all_types WHERE id = 2 UNION ALL SELECT 'datetime2', c_datetime2::text FROM public.all_types WHERE id = 2");

        Assert.Equal("0001-01-01", values["date"]);
        Assert.Equal("9999-12-31 23:59:59.999999", values["datetime2"]);
    }

    [Fact]
    public async Task Keeps_nulls_null()
    {
        var nonNull = await Postgres.ScalarAsync<long>(Target,
            "SELECT count(*) FROM (SELECT (jsonb_each(to_jsonb(t) - 'id')).value FROM public.all_types t WHERE id = 1) v WHERE v.value <> 'null'::jsonb");

        Assert.Equal(0, nonNull);
    }

    [Fact]
    public async Task New_rows_get_identity_and_default_values()
    {
        await using var connection = new NpgsqlConnection(Target);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);

        await using (var customer = new NpgsqlCommand(
            "INSERT INTO public.customers (name, email) VALUES ('New', 'new@example.com') RETURNING customer_id, is_active, credit_limit, row_guid, created_at", connection, transaction))
        await using (var reader = await customer.ExecuteReaderAsync(TestContext.Current.CancellationToken))
        {
            await reader.ReadAsync(TestContext.Current.CancellationToken);
            Assert.Equal(4, reader.GetInt32(0));
            Assert.True(reader.GetBoolean(1));
            Assert.Equal(0m, reader.GetDecimal(2));
            Assert.NotEqual(Guid.Empty, reader.GetGuid(3));
            Assert.InRange(reader.GetDateTime(4), DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow.AddMinutes(5));
        }

        await using (var order = new NpgsqlCommand(
            "INSERT INTO sales.order_header (customer_id) VALUES (1) RETURNING order_id, order_date, status, total", connection, transaction))
        await using (var reader = await order.ExecuteReaderAsync(TestContext.Current.CancellationToken))
        {
            await reader.ReadAsync(TestContext.Current.CancellationToken);
            Assert.Equal(1015, reader.GetInt64(0));
            Assert.InRange(reader.GetFieldValue<DateOnly>(1), DateOnly.FromDateTime(DateTime.Today.AddDays(-1)), DateOnly.FromDateTime(DateTime.Today.AddDays(1)));
            Assert.Equal("N", reader.GetString(2));
            Assert.Equal(0m, reader.GetDecimal(3));
        }

        await transaction.RollbackAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Creates_keys_indexes_and_foreign_keys()
    {
        var indexes = await Postgres.PairsAsync(Target,
            "SELECT indexname, indexdef FROM pg_indexes WHERE schemaname IN ('public', 'sales')");
        var constraints = await Postgres.PairsAsync(Target,
            "SELECT conname, contype::text || ':' || confdeltype::text || ':' || convalidated::text FROM pg_constraint c JOIN pg_namespace n ON n.oid = c.connamespace WHERE n.nspname IN ('public', 'sales')");

        Assert.Equal("p: :true", constraints["pk_customers"]);
        Assert.Equal("p: :true", constraints["pk_order_line"]);
        Assert.Contains("(order_id, line_number)", indexes["pk_order_line"]);
        Assert.Equal("f:c:true", constraints["fk_order_header_customers"]);
        Assert.Equal("f:c:true", constraints["fk_order_line_order"]);
        Assert.Equal("f:a:false", constraints["fk_audit_customers"]);
        Assert.Equal("u: :true", constraints["uq_customers_email"]);
        Assert.EndsWith("(email) NULLS NOT DISTINCT", indexes["uq_customers_email"]);
        Assert.EndsWith("(customer_id, order_date DESC) INCLUDE (total)", indexes["ix_order_header_customer"]);
        Assert.Contains("UNIQUE INDEX ix_code ON public.suppliers", indexes["ix_code"]);
        Assert.Contains("INDEX ix_code_1 ON public.warehouses", indexes["ix_code_1"]);
        Assert.DoesNotContain(indexes.Keys, name => name.Contains("open", StringComparison.Ordinal));
        Assert.DoesNotContain(indexes.Keys, name => name.Contains("hypothetical", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cascading_deletes_work_in_the_target()
    {
        await using var connection = new NpgsqlConnection(Target);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using (var delete = new NpgsqlCommand("DELETE FROM sales.order_header WHERE order_id = 1000", connection, transaction))
        {
            await delete.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await using var count = new NpgsqlCommand("SELECT count(*) FROM sales.order_line WHERE order_id = 1000", connection, transaction);
        Assert.Equal(0L, await count.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        await transaction.RollbackAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Shortens_names_past_the_limit()
    {
        var name = await Postgres.ScalarAsync<string>(Target,
            "SELECT tablename FROM pg_tables WHERE schemaname = 'public' AND tablename LIKE 'this_table_name_is_deliberately%'");

        Assert.True(name.Length <= 63);
    }
}
