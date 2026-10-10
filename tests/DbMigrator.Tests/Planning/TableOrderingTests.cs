using DbMigrator.Core.Planning;
using DbMigrator.Core.Schema;
using static DbMigrator.TestSupport.SchemaBuilder;

namespace DbMigrator.Tests.Planning;

public class TableOrderingTests
{
    [Fact]
    public void Referenced_tables_come_first()
    {
        var lines = Table("dbo", "A_Lines", Id(), Column("OrderId", DataTypeKind.Int32))
            .WithForeignKey("FK_Lines_Orders", "OrderId", new QualifiedName("dbo", "B_Orders"), "Id");
        var orders = Table("dbo", "B_Orders", Id(), Column("CustomerId", DataTypeKind.Int32))
            .WithForeignKey("FK_Orders_Customers", "CustomerId", new QualifiedName("dbo", "C_Customers"), "Id");
        var customers = Table("dbo", "C_Customers", Id());

        var ordered = TableOrdering.ByDependencies([lines, orders, customers]).Select(t => t.Name.Name);

        Assert.Equal(["C_Customers", "B_Orders", "A_Lines"], ordered);
    }

    [Fact]
    public void Self_references_and_missing_tables_are_ignored()
    {
        var employees = Table("dbo", "Employees", Id(), Column("ManagerId", DataTypeKind.Int32))
            .WithForeignKey("FK_Manager", "ManagerId", new QualifiedName("dbo", "Employees"), "Id")
            .WithForeignKey("FK_Elsewhere", "ManagerId", new QualifiedName("other", "NotMigrated"), "Id");

        Assert.Single(TableOrdering.ByDependencies([employees]));
    }

    [Fact]
    public void Cycles_still_yield_every_table_once()
    {
        var a = Table("dbo", "A", Id(), Column("BId", DataTypeKind.Int32)).WithForeignKey("FK_A_B", "BId", new QualifiedName("dbo", "B"), "Id");
        var b = Table("dbo", "B", Id(), Column("AId", DataTypeKind.Int32)).WithForeignKey("FK_B_A", "AId", new QualifiedName("dbo", "A"), "Id");
        var c = Table("dbo", "C", Id(), Column("AId", DataTypeKind.Int32)).WithForeignKey("FK_C_A", "AId", new QualifiedName("dbo", "A"), "Id");

        var ordered = TableOrdering.ByDependencies([c, b, a]).Select(t => t.Name.Name).ToList();

        Assert.Equal(3, ordered.Count);
        Assert.True(ordered.IndexOf("A") < ordered.IndexOf("C"));
    }
}
