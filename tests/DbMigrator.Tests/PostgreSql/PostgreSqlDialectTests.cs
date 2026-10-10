using DbMigrator.Core.Diagnostics;
using DbMigrator.Core.Planning;
using DbMigrator.Core.Schema;
using DbMigrator.Providers.PostgreSql;
using static DbMigrator.Tests.SchemaBuilder;

namespace DbMigrator.Tests.PostgreSql;

public class PostgreSqlDialectTests
{
    private static readonly PostgreSqlDialect Dialect = new(new Version(17, 0));

    public static TheoryData<DataType, string> TypeCases => new()
    {
        { new DataType(DataTypeKind.Boolean), "boolean" },
        { new DataType(DataTypeKind.Byte), "smallint" },
        { new DataType(DataTypeKind.Int32), "integer" },
        { new DataType(DataTypeKind.Decimal) { Precision = 18, Scale = 2 }, "numeric(18,2)" },
        { new DataType(DataTypeKind.Decimal), "numeric" },
        { new DataType(DataTypeKind.Double), "double precision" },
        { new DataType(DataTypeKind.FixedText) { Length = 10 }, "char(10)" },
        { new DataType(DataTypeKind.Text) { Length = 50 }, "varchar(50)" },
        { new DataType(DataTypeKind.Text), "text" },
        { new DataType(DataTypeKind.Binary) { Length = 16 }, "bytea" },
        { new DataType(DataTypeKind.Time) { Precision = 3 }, "time(3)" },
        { new DataType(DataTypeKind.DateTime) { Precision = 7 }, "timestamp(6)" },
        { new DataType(DataTypeKind.DateTimeOffset) { Precision = 0 }, "timestamptz(0)" },
        { new DataType(DataTypeKind.Guid), "uuid" },
        { new DataType(DataTypeKind.Json), "jsonb" },
        { new DataType(DataTypeKind.Xml), "xml" },
    };

    [Theory]
    [MemberData(nameof(TypeCases))]
    public void Maps_types(DataType type, string expected) =>
        Assert.Equal(expected, Dialect.MapType(type).SqlType);

    [Fact]
    public void Notes_lost_precision_and_offsets()
    {
        var timestamp = Dialect.MapType(new DataType(DataTypeKind.DateTime) { Precision = 7 });
        var offset = Dialect.MapType(new DataType(DataTypeKind.DateTimeOffset) { Precision = 3 });

        Assert.Equal(6, timestamp.Type.Precision);
        Assert.Equal(IssueCodes.PrecisionReduced, timestamp.Note?.Code);
        Assert.Equal(IssueCodes.OffsetDropped, offset.Note?.Code);
        Assert.Null(Dialect.MapType(new DataType(DataTypeKind.DateTime) { Precision = 3 }).Note);
    }

    public static TheoryData<DefaultValue, DataType, string?> DefaultCases => new()
    {
        { new DefaultValue.Literal(1L), new DataType(DataTypeKind.Boolean), "true" },
        { new DefaultValue.Literal(0L), new DataType(DataTypeKind.Boolean), "false" },
        { new DefaultValue.Literal(1.5m), new DataType(DataTypeKind.Decimal), "1.5" },
        { new DefaultValue.Literal("42"), new DataType(DataTypeKind.Int32), "42" },
        { new DefaultValue.Literal("it's"), new DataType(DataTypeKind.Text), "'it''s'" },
        { new DefaultValue.Literal(0L), new DataType(DataTypeKind.Text), "'0'" },
        { new DefaultValue.Literal(new byte[] { 0x00, 0xFF }), new DataType(DataTypeKind.Binary), "'\\x00FF'::bytea" },
        { new DefaultValue.Literal("19000101"), new DataType(DataTypeKind.DateTime), "'1900-01-01 00:00:00'" },
        { new DefaultValue.Literal("2024-02-29"), new DataType(DataTypeKind.Date), "'2024-02-29'" },
        { new DefaultValue.Literal("6F9619FF-8B86-D011-B42D-00C04FC964FF"), new DataType(DataTypeKind.Guid), "'6f9619ff-8b86-d011-b42d-00c04fc964ff'" },
        { new DefaultValue.Function(DefaultFunction.CurrentDateTime), new DataType(DataTypeKind.DateTime), "LOCALTIMESTAMP" },
        { new DefaultValue.Function(DefaultFunction.CurrentDateTime), new DataType(DataTypeKind.DateTimeOffset), "CURRENT_TIMESTAMP" },
        { new DefaultValue.Function(DefaultFunction.CurrentUtcDateTime), new DataType(DataTypeKind.DateTime), "(now() AT TIME ZONE 'utc')" },
        { new DefaultValue.Function(DefaultFunction.CurrentDate), new DataType(DataTypeKind.Date), "CURRENT_DATE" },
        { new DefaultValue.Function(DefaultFunction.NewGuid), new DataType(DataTypeKind.Guid), "gen_random_uuid()" },
    };

    [Theory]
    [MemberData(nameof(DefaultCases))]
    public void Renders_defaults(DefaultValue value, DataType type, string? expected)
    {
        var rendering = Dialect.RenderDefault(value, type);

        Assert.Null(rendering.Problem);
        Assert.Equal(expected, rendering.Sql);
    }

    [Fact]
    public void Null_defaults_need_no_clause() =>
        Assert.Equal(new(null, null), Dialect.RenderDefault(new DefaultValue.Literal(null), new DataType(DataTypeKind.Int32)));

    [Theory]
    [InlineData("not a date", DataTypeKind.Date)]
    [InlineData("abc", DataTypeKind.Int32)]
    public void Reports_literals_that_do_not_fit_the_column(string literal, DataTypeKind kind) =>
        Assert.NotNull(Dialect.RenderDefault(new DefaultValue.Literal(literal), new DataType(kind)).Problem);

    [Fact]
    public void Reports_source_expressions_and_functions_without_an_equivalent()
    {
        Assert.NotNull(Dialect.RenderDefault(new DefaultValue.Expression("([dbo].[f]())"), new DataType(DataTypeKind.Int32)).Problem);
        Assert.NotNull(Dialect.RenderDefault(new DefaultValue.Function(DefaultFunction.NewGuid), new DataType(DataTypeKind.Int32)).Problem);
    }

    [Fact]
    public void Quotes_identifiers() =>
        Assert.Equal("\"we\"\"ird\".\"order header\"", Dialect.QuoteName(new QualifiedName("we\"ird", "order header")));

    [Fact]
    public void Creates_tables_with_identity_defaults_and_nullability()
    {
        var table = TablePlanOf(
            new ColumnPlan { Source = Id(), Name = "id", Type = new DataType(DataTypeKind.Int32), SqlType = "integer", IsNullable = false, Identity = new IdentitySpec(0, 1) },
            new ColumnPlan { Source = Id(), Name = "status", Type = new DataType(DataTypeKind.FixedText), SqlType = "char(1)", IsNullable = false, DefaultSql = "'N'" },
            new ColumnPlan { Source = Id(), Name = "note", Type = new DataType(DataTypeKind.Text), SqlType = "text" });

        Assert.Equal(
            """
            CREATE TABLE "public"."orders" (
                "id" integer GENERATED BY DEFAULT AS IDENTITY (START WITH 0 INCREMENT BY 1 MINVALUE 0) NOT NULL,
                "status" char(1) DEFAULT 'N' NOT NULL,
                "note" text
            )
            """.ReplaceLineEndings("\n"),
            Dialect.CreateTable(table));
    }

    [Fact]
    public void Unique_indexes_keep_sql_server_null_semantics_where_supported()
    {
        var table = TablePlanOf(new ColumnPlan { Source = Id(), Name = "code", Type = new DataType(DataTypeKind.Text), SqlType = "text", IsNullable = true });
        var index = new IndexPlan { Name = "ix_code", Columns = [new IndexColumn("code")], IsUnique = true, NullsAreDistinct = false };

        Assert.Equal("CREATE UNIQUE INDEX \"ix_code\" ON \"public\".\"orders\" (\"code\") NULLS NOT DISTINCT", Dialect.CreateIndex(table, index));
        Assert.Equal("CREATE UNIQUE INDEX \"ix_code\" ON \"public\".\"orders\" (\"code\")", Dialect.CreateIndex(table, index with { NullsAreDistinct = true }));
        Assert.Equal("CREATE UNIQUE INDEX \"ix_code\" ON \"public\".\"orders\" (\"code\")", new PostgreSqlDialect(new Version(14, 0)).CreateIndex(table, index));
    }

    [Fact]
    public void Unique_constraints_with_descending_columns_become_indexes()
    {
        var table = TablePlanOf(new ColumnPlan { Source = Id(), Name = "a", Type = new DataType(DataTypeKind.Int32), SqlType = "integer", IsNullable = false });
        var constraint = new IndexPlan { Name = "uq_a", Columns = [new IndexColumn("a")], IsUnique = true, IsUniqueConstraint = true };

        Assert.Equal("ALTER TABLE \"public\".\"orders\" ADD CONSTRAINT \"uq_a\" UNIQUE (\"a\")", Dialect.CreateIndex(table, constraint));
        Assert.Equal(
            "CREATE UNIQUE INDEX \"uq_a\" ON \"public\".\"orders\" (\"a\" DESC)",
            Dialect.CreateIndex(table, constraint with { Columns = [new IndexColumn("a", IsDescending: true)] }));
    }

    [Fact]
    public void Foreign_keys_carry_actions_and_trust()
    {
        var fk = new ForeignKeyPlan
        {
            Name = "fk_orders_customers",
            Columns = ["customer_id"],
            ReferencedTable = new QualifiedName("public", "customers"),
            ReferencedColumns = ["id"],
            OnDelete = ReferentialAction.Cascade,
            OnUpdate = ReferentialAction.SetNull,
            IsTrusted = false,
        };

        Assert.Equal(
            "ALTER TABLE \"public\".\"orders\" ADD CONSTRAINT \"fk_orders_customers\" FOREIGN KEY (\"customer_id\") REFERENCES \"public\".\"customers\" (\"id\") ON DELETE CASCADE ON UPDATE SET NULL NOT VALID",
            Dialect.AddForeignKey(TablePlanOf(), fk));
    }

    [Fact]
    public void Identity_resets_follow_the_direction_of_the_increment()
    {
        var up = new ColumnPlan { Source = Id(), Name = "id", Type = new DataType(DataTypeKind.Int64), SqlType = "bigint", Identity = new IdentitySpec(1000, 5) };
        var down = up with { Identity = new IdentitySpec(-1, -1) };

        Assert.Equal(
            "SELECT setval(pg_get_serial_sequence('\"public\".\"orders\"', 'id'), GREATEST(COALESCE(MAX(\"id\"), 1000), 1000), COALESCE(MAX(\"id\") >= 1000, false)) FROM \"public\".\"orders\"",
            Dialect.ResetIdentity(TablePlanOf(up), up));
        Assert.Contains("LEAST(COALESCE(MIN(\"id\"), -1), -1)", Dialect.ResetIdentity(TablePlanOf(down), down));
    }

    private static TablePlan TablePlanOf(params ColumnPlan[] columns) => new()
    {
        Source = Table("dbo", "Orders"),
        Target = new QualifiedName("public", "orders"),
        Action = TableAction.Create,
        Columns = columns,
    };
}
