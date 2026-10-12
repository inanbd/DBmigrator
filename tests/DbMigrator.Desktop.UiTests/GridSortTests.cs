using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using DbMigrator.Core.Planning;
using DbMigrator.Core.Schema;
using DbMigrator.Desktop.Testing;
using DbMigrator.Desktop.ViewModels;
using DbMigrator.TestSupport;

namespace DbMigrator.Desktop.UiTests;

/// <summary>Every column in every grid sorts by clicking its header, and numbers sort as numbers.</summary>
public sealed class GridSortTests(AvaloniaFixture fixture)
{
    [Fact]
    public Task Every_column_sorts_and_numbers_sort_by_value() => fixture.RunAsync(async () =>
    {
        // Counts whose text order is not their numeric order: "1,500" < "20" < "3" < "999".
        var estimates = new Dictionary<string, long?> { ["A"] = 2_000_000, ["B"] = 999, ["C"] = 50_000, ["D"] = null };
        var schema = TestServices.DefaultSchema();
        schema = schema with { Tables = schema.Tables.Select(t => t with { EstimatedRowCount = estimates[t.Name.Name] }).ToList() };
        var test = TestServices.Create(schema);
        test.SourceDb.RowCounts[TestServices.A] = 1_500;
        test.SourceDb.RowCounts[TestServices.B] = 999;
        test.SourceDb.RowCounts[TestServices.C] = 20;
        test.SourceDb.RowCounts[TestServices.D] = 3;
        test.TargetDb.ExistingTables.Add(new QualifiedName("public", "c"));
        test.TargetDb.ExistingTables.Add(new QualifiedName("public", "d"));
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

    /// <summary>
    /// Clicks each sortable header twice (ascending, then descending). The columns in
    /// <paramref name="ordered"/> must then show their rows in the order of that value.
    /// </summary>
    private static void SortEveryColumn(UiDriver ui, string gridName, Dictionary<string, Func<object, IComparable>> ordered)
    {
        var grid = ui.Find<DataGrid>(gridName);

        // A text column the grid cannot sort binds a value that is not comparable, such as a QualifiedName.
        foreach (var text in grid.Columns.OfType<DataGridTextColumn>())
        {
            Assert.True(text.CanUserSort, $"{gridName} \"{text.Header}\" cannot be sorted.");
        }

        var sortable = grid.Columns.Where(c => c.CanUserSort).ToList();
        foreach (var check in ordered.Keys)
        {
            Assert.Contains(sortable, c => Equals(c.Header, check));
        }

        foreach (var column in sortable)
        {
            var header = grid.GetVisualDescendants().OfType<DataGridColumnHeader>().First(h => Equals(h.Content, column.Header));
            ui.Click(header);
            var ascending = Shown(grid);
            ui.Click(header);
            var descending = Shown(grid);
            if (ordered.TryGetValue(column.Header?.ToString() ?? "", out var value))
            {
                Assert.True(ascending.Count > 1, $"{gridName} shows {ascending.Count} rows.");
                var up = ascending.Select(value).ToList();
                var down = descending.Select(value).ToList();
                Assert.True(up.SequenceEqual(up.Order(Comparer<IComparable>.Default)), $"{gridName} \"{column.Header}\" ascending: {string.Join(", ", up)}");
                Assert.True(down.SequenceEqual(down.OrderDescending(Comparer<IComparable>.Default)), $"{gridName} \"{column.Header}\" descending: {string.Join(", ", down)}");
            }
        }
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
