using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using DbMigrator.Core.Planning;
using DbMigrator.Core.Schema;
using DbMigrator.Desktop.Testing;
using DbMigrator.Desktop.ViewModels;
using DbMigrator.TestSupport;
using static DbMigrator.TestSupport.SchemaBuilder;

namespace DbMigrator.Desktop.UiTests;

/// <summary>Every column in every grid sorts by clicking its header, and numbers, ranks and names sort by value.</summary>
public sealed class GridSortTests(AvaloniaFixture fixture)
{
    /// <summary>
    /// The value each formatted column sorts by. Its text would sort "999" after "1,234", "10 W" before "2 W",
    /// and "Error" before "Note". Rate and elapsed time come from the engine's real stopwatch, so no test data
    /// can make their text order differ; for those columns this wiring is what is checked.
    /// </summary>
    private static readonly Dictionary<string, Dictionary<string, string>> SortKeys = new()
    {
        ["TablesGrid"] = new() { ["Rows (est.)"] = "SortRows", ["Keys"] = "SortKeys", ["In target"] = "ExistsInTarget" },
        ["ConflictsGrid"] = new() { ["Source table"] = "SortSource", ["Target table"] = "SortTarget", ["Rows (est.)"] = "SortRows" },
        ["PlanTablesGrid"] = new() { ["Source"] = "SortSource", ["Target"] = "SortTarget", ["Rows (est.)"] = "SortRows", ["Issues"] = "SortIssues" },
        ["TypeMappingsGrid"] = new() { ["Worst"] = "WorstSeverity" },
        ["RunTablesGrid"] = new() { ["Rows copied / est."] = "RowsCopied", ["Progress"] = "Percent", ["Rate"] = "SortRate", ["Elapsed"] = "Elapsed" },
        ["ReportTablesGrid"] = new()
        {
            ["Rows copied"] = "SortRowsCopied",
            ["Source count"] = "SortSourceCount",
            ["Target count"] = "SortTargetCount",
            ["Elapsed"] = "SortElapsed",
            ["Rate"] = "SortRate",
        },
    };

    private static readonly string[] AllTargets = ["a", "b", "c", "d"];

    [Fact]
    public Task Every_column_sorts_and_numbers_sort_by_value() => fixture.RunAsync(async () =>
    {
        // Counts whose text order is not their numeric order: "1,500" < "20" < "3" < "999", "~2 M" < "~50 K" < "~999".
        var estimates = new Dictionary<string, long?> { ["A"] = 2_000_000, ["B"] = 999, ["C"] = 50_000, ["D"] = null };
        var schema = TestServices.DefaultSchema();
        schema = schema with { Tables = schema.Tables.Select(t => t with { EstimatedRowCount = estimates[t.Name.Name] }).ToList() };
        var test = TestServices.Create(schema);
        test.SourceDb.RowCounts[TestServices.A] = 1_500;
        test.SourceDb.RowCounts[TestServices.B] = 999;
        test.SourceDb.RowCounts[TestServices.C] = 20;
        test.SourceDb.RowCounts[TestServices.D] = 3;
        foreach (var name in AllTargets)
        {
            test.TargetDb.ExistingTables.Add(new QualifiedName("public", name));
        }

        var gate = new Gate();
        test.SourceDb.Pauses[TestServices.D] = (2, gate);

        var ui = await UiDriver.OpenAsync(test);
        await ui.Wizard.ConnectSourceAsync();
        await ui.NextAsync();
        await ui.Wizard.ConnectTargetAsync();
        await UiDriver.UntilAsync(() => ui.Shell.Target.MatchingTableCount is not null, "the matching count");
        await ui.NextAsync();
        SortEveryColumn(ui, "TablesGrid", new() { ["Rows (est.)"] = r => ((TableSelectionRowViewModel)r).EstimatedRows ?? -1 });

        await ui.NextAsync();
        SortEveryColumn(ui, "ConflictsGrid", new()
        {
            ["Rows (est.)"] = r => ((ConflictRowViewModel)r).Table.Source.EstimatedRowCount ?? -1,
            ["Source table"] = r => ((ConflictRowViewModel)r).Source.ToString(),
            ["Target table"] = r => ((ConflictRowViewModel)r).Target.ToString(),
        });
        foreach (var conflict in ui.Shell.Review.Conflicts)
        {
            conflict.Choice = ConflictChoice.For(TableExistsAction.DropAndRecreate);
        }

        await Task.Delay(20);
        test.Time.Advance(TimeSpan.FromSeconds(1));
        await UiDriver.UntilAsync(() => !ui.Shell.Review.IsPlanning && ui.Shell.Review.UndecidedCount == 0, "the re-plan");
        ui.Shell.Review.SelectedTab = ReviewStepViewModel.TablesTab;
        SortEveryColumn(ui, "PlanTablesGrid", new()
        {
            ["Rows (est.)"] = r => ((PlanTableRowViewModel)r).Plan.Source.EstimatedRowCount ?? -1,
            ["#"] = r => ((PlanTableRowViewModel)r).Index,
            ["Source"] = r => ((PlanTableRowViewModel)r).Source.ToString(),
            ["Target"] = r => ((PlanTableRowViewModel)r).Target.ToString(),
        });
        SortEveryColumn(ui, "ColumnMappings", []);
        ui.Shell.Review.SelectedTab = ReviewStepViewModel.TypesTab;
        SortEveryColumn(ui, "TypeMappingsGrid", new() { ["Columns"] = r => ((TypeMappingRowViewModel)r).ColumnCount });
        ui.Shell.Review.SelectedTab = ReviewStepViewModel.IssuesTab;
        SortEveryColumn(ui, "IssueRows", []);

        await ui.NextAsync();
        ui.Shell.Run.Acknowledged = true;
        ui.Click(ui.Find<Button>("StartButton"));
        await UiDriver.UntilAsync(() => gate.Reached.IsCompleted, "the copy to reach dbo.D");
        test.Scheduler.Tick();
        SortEveryColumn(ui, "RunTablesGrid", new()
        {
            ["Rows copied / est."] = r => ((TableProgressRowViewModel)r).RowsCopied,
            ["#"] = r => ((TableProgressRowViewModel)r).Index,
            ["Progress"] = r => ((TableProgressRowViewModel)r).Percent,
        });

        gate.Release();
        await UiDriver.UntilAsync(() => ui.Shell.Current == ui.Shell.Report, "the report");
        SortEveryColumn(ui, "ReportTablesGrid", new()
        {
            ["Rows copied"] = r => ((ReportTableRowViewModel)r).Result.RowsCopied,
            ["Source count"] = r => ((ReportTableRowViewModel)r).Result.SourceRowCount ?? -1,
            ["Target count"] = r => ((ReportTableRowViewModel)r).Result.TargetRowCount ?? -1,
        });
        ui.Window.Close();
    });

    [Fact]
    public Task Keys_issues_and_severities_sort_by_rank_and_the_table_detail_lists_table_issues() => fixture.RunAsync(async () =>
    {
        fixture.Bindings.Clear();

        // Issues "1 E", "0 E · 10 W", "0 E · 2 W · 1 N"; keys "", "PK", "PK, 2 FK", "PK, 12 FK";
        // worst severities "", "Error" (two columns become "code"), "Note" (7 fractional digits become 6).
        var schema = Source(
            Keyed("E", Column("Code", DataTypeKind.Text), Column("code", DataTypeKind.Text)),
            Keyed("F") with { CheckConstraints = Checks("F", 10) },
            Keyed("G", Column("At", DataTypeKind.DateTime) with { Type = new DataType(DataTypeKind.DateTime) { Precision = 7 } }) with { CheckConstraints = Checks("G", 2) },
            ReferencingF("H", 12),
            ReferencingF("I", 2),
            Table("dbo", "J", Id()));
        var ui = await UiDriver.OpenAsync(TestServices.Create(schema));
        await ui.Wizard.ConnectSourceAsync();
        await ui.NextAsync();
        await ui.Wizard.ConnectTargetAsync();
        await ui.NextAsync();
        SortEveryColumn(ui, "TablesGrid", new()
        {
            ["Keys"] = r => (((TableSelectionRowViewModel)r).Definition.PrimaryKey is not null, ((TableSelectionRowViewModel)r).Definition.ForeignKeys.Count),
        });

        await ui.NextAsync();
        var review = ui.Shell.Review;
        review.SelectedTab = ReviewStepViewModel.TablesTab;
        SortEveryColumn(ui, "PlanTablesGrid", new()
        {
            ["Issues"] = r => (((PlanTableRowViewModel)r).Errors, ((PlanTableRowViewModel)r).Warnings, ((PlanTableRowViewModel)r).Notes),
        });
        Assert.True(review.PlanTables.Select(p => (p.Errors, p.Warnings, p.Notes)).Distinct().Count() >= 4);

        review.SelectedPlanTable = review.PlanTables.Single(p => p.Source.Name == "G");
        var issues = ui.Find<ItemsControl>("TableIssues");
        Assert.Equal(2, issues.ItemCount);
        Assert.Equal(2, issues.GetVisualDescendants().OfType<TextBlock>().Count(t => t.IsEffectivelyVisible && t.Text?.Contains("CK_G_", StringComparison.Ordinal) == true));

        review.SelectedTab = ReviewStepViewModel.TypesTab;
        SortEveryColumn(ui, "TypeMappingsGrid", new() { ["Worst"] = r => ((TypeMappingRowViewModel)r).WorstSeverity is { } s ? (int)s : -1 });
        Assert.Equal(3, review.TypeMappings.Select(t => t.WorstSeverity).Distinct().Count());

        Assert.True(fixture.Bindings.Errors.Count == 0, string.Join(Environment.NewLine, fixture.Bindings.Errors.Distinct()));
        ui.Window.Close();
    });

    private static TableDefinition Keyed(string name, params Core.Schema.ColumnDefinition[] more) =>
        Table("dbo", name, [Id(), .. more]).WithPrimaryKey($"PK_{name}", "Id");

    private static CheckConstraintDefinition[] Checks(string table, int count) =>
        Enumerable.Range(1, count).Select(i => new CheckConstraintDefinition($"CK_{table}_{i}", $"([Id] > {i})")).ToArray();

    private static TableDefinition ReferencingF(string name, int count) =>
        Enumerable.Range(1, count).Aggregate(
            Keyed(name, Enumerable.Range(1, count).Select(i => Column($"F{i}", DataTypeKind.Int32)).ToArray()),
            (table, i) => table.WithForeignKey($"FK_{name}_{i}", $"F{i}", new QualifiedName("dbo", "F"), "Id"));

    /// <summary>
    /// Clicks each sortable header twice (ascending, then descending). Each column with a sort key must
    /// then show its rows in the order of that key, and those in <paramref name="ordered"/> in the order
    /// of the value given, worked out from the row itself.
    /// </summary>
    private static void SortEveryColumn(UiDriver ui, string gridName, Dictionary<string, Func<object, IComparable>> ordered)
    {
        var grid = ui.Find<DataGrid>(gridName);

        // A text column the grid cannot sort binds a value that is not comparable, such as a QualifiedName.
        foreach (var text in grid.Columns.OfType<DataGridTextColumn>())
        {
            Assert.True(text.CanUserSort, $"{gridName} \"{text.Header}\" cannot be sorted.");
        }

        foreach (var (header, key) in SortKeys.GetValueOrDefault(gridName) ?? [])
        {
            Assert.Equal(key, grid.Columns.Single(c => Equals(c.Header, header)).SortMemberPath);
        }

        var sortable = grid.Columns.Where(c => c.CanUserSort).ToList();
        foreach (var check in ordered.Keys)
        {
            Assert.Contains(sortable, c => Equals(c.Header, check));
        }

        foreach (var column in sortable)
        {
            // A header scrolled out of sight would take no click, and the order would only look checked.
            grid.ScrollIntoView(Shown(grid).FirstOrDefault(), column);
            UiDriver.Settle();
            var header = grid.GetVisualDescendants().OfType<DataGridColumnHeader>().First(h => Equals(h.Content, column.Header));
            ui.Click(header);
            Assert.True(header.Classes.Contains(":sortascending"), $"{gridName} \"{column.Header}\" did not sort ascending.");
            var ascending = Shown(grid);
            ui.Click(header);
            Assert.True(header.Classes.Contains(":sortdescending"), $"{gridName} \"{column.Header}\" did not sort descending.");
            var descending = Shown(grid);

            var name = column.Header?.ToString() ?? "";
            if (ordered.TryGetValue(name, out var value))
            {
                Assert.True(ascending.Count > 1, $"{gridName} shows {ascending.Count} rows.");
                AssertOrder(gridName, name, ascending, descending, value);
            }
            else if (column.SortMemberPath is { Length: > 0 } path)
            {
                AssertOrder(gridName, name, ascending, descending, r => (IComparable)r.GetType().GetProperty(path)!.GetValue(r)!);
            }
        }
    }

    private static void AssertOrder(string grid, string column, List<object> ascending, List<object> descending, Func<object, IComparable> value)
    {
        var up = ascending.Select(value).ToList();
        var down = descending.Select(value).ToList();
        Assert.True(up.SequenceEqual(up.Order(Comparer<IComparable>.Default)), $"{grid} \"{column}\" ascending: {string.Join(", ", up)}");
        Assert.True(down.SequenceEqual(down.OrderDescending(Comparer<IComparable>.Default)), $"{grid} \"{column}\" descending: {string.Join(", ", down)}");
    }

    private static List<object> Shown(DataGrid grid)
    {
        UiDriver.Settle();
        return grid.GetVisualDescendants().OfType<DataGridRow>()
            .Where(r => r.IsVisible && r.DataContext is not null)
            .OrderBy(r => r.Bounds.Y)
            .Select(r => r.DataContext!)
            .ToList();
    }
}
