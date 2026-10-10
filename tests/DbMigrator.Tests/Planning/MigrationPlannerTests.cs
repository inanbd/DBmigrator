using DbMigrator.Core.Diagnostics;
using DbMigrator.Core.Planning;
using DbMigrator.Core.Providers;
using DbMigrator.Core.Schema;
using DbMigrator.Providers.PostgreSql;
using static DbMigrator.Tests.SchemaBuilder;

namespace DbMigrator.Tests.Planning;

public class MigrationPlannerTests
{
    private static readonly PostgreSqlDialect Dialect = new(new Version(17, 0));

    private static readonly TableDefinition Customers =
        Table("dbo", "Customers", Id("CustomerID"), Column("FullName", DataTypeKind.Text, nullable: false))
            .WithPrimaryKey("PK_Customers", "CustomerID");

    private static readonly TableDefinition Orders =
        Table("sales", "OrderHeader", Id("OrderID"), Column("CustomerID", DataTypeKind.Int32, nullable: false))
            .WithPrimaryKey("PK_OrderHeader", "OrderID")
            .WithForeignKey("FK_Orders_Customers", "CustomerID", Customers.Name, "CustomerID");

    private static MigrationPlan Plan(SourceSchema source, MigrationOptions? options = null, TargetState? target = null) =>
        MigrationPlanner.CreatePlan(source, Dialect, target ?? new TargetState([], ["public"]), options ?? new MigrationOptions());

    [Fact]
    public void Maps_the_default_schema_and_converts_names()
    {
        var plan = Plan(Source(Orders, Customers), new MigrationOptions { Naming = new NamingOptions { Case = IdentifierCase.SnakeCase } });

        Assert.Equal(["public.customers", "sales.order_header"], plan.Tables.Select(t => t.Target.ToString()));
        Assert.Equal(["customer_id", "full_name"], plan.Tables[0].Columns.Select(c => c.Name));
        Assert.Equal(["sales"], plan.SchemasToCreate);
        Assert.False(plan.HasErrors);
    }

    [Fact]
    public void Schema_map_sends_tables_where_asked()
    {
        var options = new MigrationOptions
        {
            Naming = new NamingOptions { SchemaMap = new Dictionary<string, string> { ["DBO"] = "app", ["sales"] = "Sales" } },
        };

        var plan = Plan(Source(Customers, Orders), options);

        Assert.Equal(["app.customers", "Sales.orderheader"], plan.Tables.Select(t => t.Target.ToString()));
    }

    [Fact]
    public void Constraints_reference_target_names()
    {
        var plan = Plan(Source(Customers, Orders), new MigrationOptions { Naming = new NamingOptions { Case = IdentifierCase.SnakeCase } });
        var orders = plan.Tables.Single(t => t.Source.Name == Orders.Name);

        Assert.Equal("pk_order_header", orders.PrimaryKey!.Name);
        Assert.Equal(["order_id"], orders.PrimaryKey.Columns);
        var fk = Assert.Single(orders.ForeignKeys);
        Assert.Equal("fk_orders_customers", fk.Name);
        Assert.Equal(new QualifiedName("public", "customers"), fk.ReferencedTable);
        Assert.Equal(["customer_id"], fk.ReferencedColumns);
    }

    [Fact]
    public void Generated_constraint_names_are_replaced_with_readable_ones()
    {
        var table = Table("dbo", "Audit", Id("AuditID"), Column("CustomerID", DataTypeKind.Int32), Column("Code", DataTypeKind.Text)) with
        {
            PrimaryKey = new PrimaryKeyDefinition("PK__Audit__A17F23B8D9B425B0", ["AuditID"]) { IsNameGenerated = true },
            Indexes =
            [
                new IndexDefinition
                {
                    Name = "UQ__Audit__A25C5AA7",
                    Columns = [new IndexColumn("Code")],
                    IsUnique = true,
                    IsUniqueConstraint = true,
                    IsNameGenerated = true,
                },
            ],
            ForeignKeys =
            [
                new ForeignKeyDefinition
                {
                    Name = "FK__Audit__Customer__4BAC3F29",
                    Columns = ["CustomerID"],
                    ReferencedTable = Customers.Name,
                    ReferencedColumns = ["CustomerID"],
                    IsNameGenerated = true,
                },
            ],
        };

        var audit = Plan(Source(Customers, table)).Tables.Single(t => t.Source.Name == table.Name);

        Assert.Equal("pk_audit", audit.PrimaryKey!.Name);
        Assert.Equal("uq_audit_code", Assert.Single(audit.Indexes).Name);
        Assert.Equal("fk_audit_customers", Assert.Single(audit.ForeignKeys).Name);
    }

    [Fact]
    public void An_existing_table_is_an_error_until_an_action_is_chosen()
    {
        var target = new TargetState([new QualifiedName("public", "customers")], ["public"]);

        var plan = Plan(Source(Customers), target: target);

        Assert.True(plan.HasErrors);
        var conflict = Assert.Single(plan.UnresolvedConflicts);
        Assert.Equal(Customers.Name, conflict.Source.Name);
        Assert.Contains(plan.Issues, i => i is { Code: IssueCodes.TableExists, Severity: IssueSeverity.Error });

        var resolved = Plan(Source(Customers), new MigrationOptions
        {
            TableExistsOverrides = new Dictionary<QualifiedName, TableExistsAction> { [Customers.Name] = TableExistsAction.DropAndRecreate },
        }, target);

        Assert.False(resolved.HasErrors);
        Assert.Equal(TableAction.DropAndCreate, resolved.Tables[0].Action);
        Assert.Contains(resolved.Issues, i => i.Code == IssueCodes.DropCascade);
    }

    [Theory]
    [InlineData(TableExistsAction.Truncate, TableAction.Truncate)]
    [InlineData(TableExistsAction.Append, TableAction.Append)]
    [InlineData(TableExistsAction.Skip, TableAction.Skip)]
    public void Kept_tables_get_no_new_constraints(TableExistsAction choice, TableAction expected)
    {
        var target = new TargetState([new QualifiedName("public", "customers")], ["public"]);

        var table = Plan(Source(Customers), new MigrationOptions { IfTableExists = choice }, target).Tables[0];

        Assert.Equal(expected, table.Action);
        Assert.Null(table.PrimaryKey);
        Assert.False(table.CreatesTable);
    }

    [Fact]
    public void Names_that_collide_after_conversion_are_an_error()
    {
        var plan = Plan(Source(Table("dbo", "Orders", Id()), Table("dbo", "ORDERS", Id())));

        Assert.Contains(plan.Issues, i => i is { Code: IssueCodes.NameCollision, Severity: IssueSeverity.Error });
    }

    [Fact]
    public void Foreign_keys_to_tables_left_out_are_reported_not_created()
    {
        var plan = Plan(Source(Customers, Orders), new MigrationOptions { Tables = new TableFilter(["OrderHeader"], []) });

        Assert.Empty(Assert.Single(plan.Tables).ForeignKeys);
        Assert.Contains(plan.Issues, i => i.Code == IssueCodes.ForeignKeyTargetMissing);
    }

    [Fact]
    public void Filtered_indexes_and_check_constraints_are_reported_not_created()
    {
        var table = Customers.WithIndex(new IndexDefinition { Name = "IX_Active", Columns = [new IndexColumn("FullName")], Filter = "([IsActive]=(1))" });
        table = table with { CheckConstraints = [new CheckConstraintDefinition("CK_Name", "(len([FullName])>(0))")] };

        var plan = Plan(Source(table));

        Assert.Empty(plan.Tables[0].Indexes);
        Assert.Contains(plan.Issues, i => i.Code == IssueCodes.FilteredIndex);
        Assert.Contains(plan.Issues, i => i.Code == IssueCodes.CheckConstraint);
    }

    [Fact]
    public void Computed_columns_become_nullable_regular_columns()
    {
        var table = Table("dbo", "Lines", Id(), Column("Total", DataTypeKind.Decimal, nullable: false) with { ComputedExpression = "([Qty]*[Price])" });

        var column = Plan(Source(table)).Tables[0].Columns[1];

        Assert.True(column.IsNullable);
    }

    [Fact]
    public void Identity_types_are_adjusted_or_dropped()
    {
        var table = Table("dbo", "Odd",
            Column("Small", DataTypeKind.Decimal, nullable: false) with { Type = new DataType(DataTypeKind.Decimal) { Precision = 10, Scale = 0 }, Identity = new IdentitySpec(1, 1) },
            Column("Huge", DataTypeKind.Decimal, nullable: false) with { Type = new DataType(DataTypeKind.Decimal) { Precision = 38, Scale = 0 }, Identity = new IdentitySpec(1, 1) });

        var plan = Plan(Source(table));
        var columns = plan.Tables[0].Columns;

        Assert.Equal("bigint", columns[0].SqlType);
        Assert.NotNull(columns[0].Identity);
        Assert.Equal("numeric(38,0)", columns[1].SqlType);
        Assert.Null(columns[1].Identity);
        Assert.Contains(plan.Issues, i => i.Code == IssueCodes.IdentityDropped && i.Subject == "dbo.Odd.Huge");
    }

    [Fact]
    public void Untranslatable_defaults_are_reported_and_left_off()
    {
        var table = Table("dbo", "T",
            Column("A", DataTypeKind.Int32) with { Default = new DefaultValue.Expression("([dbo].[NextNumber]())") },
            Column("B", DataTypeKind.Int32) with { Default = new DefaultValue.Literal(5L) },
            Column("C", DataTypeKind.Int32) with { Default = new DefaultValue.Literal(null) });

        var plan = Plan(Source(table));
        var columns = plan.Tables[0].Columns;

        Assert.Null(columns[0].DefaultSql);
        Assert.Equal("5", columns[1].DefaultSql);
        Assert.Null(columns[2].DefaultSql);
        Assert.Single(plan.Issues, i => i.Code == IssueCodes.DefaultNotTranslated);
    }

    [Fact]
    public void Repeated_index_names_are_made_unique_per_schema()
    {
        var a = Table("dbo", "A", Id(), Column("Code", DataTypeKind.Int32)).WithIndex(new IndexDefinition { Name = "IX_Code", Columns = [new IndexColumn("Code")] });
        var b = Table("dbo", "B", Id(), Column("Code", DataTypeKind.Int32)).WithIndex(new IndexDefinition { Name = "IX_Code", Columns = [new IndexColumn("Code")] });
        var c = Table("other", "C", Id(), Column("Code", DataTypeKind.Int32)).WithIndex(new IndexDefinition { Name = "IX_Code", Columns = [new IndexColumn("Code")] });

        var plan = Plan(Source(a, b, c));

        Assert.Equal(["ix_code", "ix_code_1", "ix_code"], plan.Tables.Select(t => t.Indexes.Single().Name));
        Assert.Contains(plan.Issues, i => i.Code == IssueCodes.NameDeduplicated);
    }

    [Fact]
    public void Index_names_avoid_table_names()
    {
        var a = Table("dbo", "Orders", Id());
        var b = Table("dbo", "Lines", Id(), Column("Code", DataTypeKind.Int32)).WithIndex(new IndexDefinition { Name = "Orders", Columns = [new IndexColumn("Code")] });

        var plan = Plan(Source(a, b));

        Assert.Equal("orders_1", plan.Tables.Single(t => t.Source.Name.Name == "Lines").Indexes.Single().Name);
    }

    [Fact]
    public void Only_schemas_that_do_not_exist_are_created()
    {
        var plan = Plan(Source(Customers, Orders), target: new TargetState([], ["public", "sales"]));

        Assert.Empty(plan.SchemasToCreate);
    }

    [Fact]
    public void Script_creates_tables_before_data_and_constraints_after()
    {
        var plan = Plan(Source(Customers, Orders));

        var script = ScriptBuilder.Build(plan, Dialect);

        Assert.StartsWith("CREATE SCHEMA", script.BeforeData[0].Sql);
        Assert.All(script.BeforeData.Skip(1), s => Assert.StartsWith("CREATE TABLE", s.Sql));
        Assert.Collection(script.AfterData,
            s => Assert.Contains("PRIMARY KEY", s.Sql),
            s => Assert.Contains("PRIMARY KEY", s.Sql),
            s => Assert.Contains("FOREIGN KEY", s.Sql),
            s => Assert.StartsWith("SELECT setval", s.Sql),
            s => Assert.StartsWith("SELECT setval", s.Sql));
        Assert.Contains("bulk-loads 2 table(s)", script.ToText(plan));
    }

    [Fact]
    public void An_empty_selection_is_reported()
    {
        var plan = Plan(Source(Customers), new MigrationOptions { Tables = new TableFilter(["nothing"], []) });

        Assert.Empty(plan.Tables);
        Assert.Contains(plan.Issues, i => i.Code == IssueCodes.NoTables);
    }
}
