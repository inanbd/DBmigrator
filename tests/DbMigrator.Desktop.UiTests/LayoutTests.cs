using Avalonia;
using Avalonia.Controls;
using DbMigrator.Desktop.Testing;
using DbMigrator.Desktop.ViewModels;

namespace DbMigrator.Desktop.UiTests;

/// <summary>What must stay usable at the window's default size (1200x800) and its minimum (1000x680).</summary>
public sealed class LayoutTests(AvaloniaFixture fixture)
{
    private static readonly string[] Toolbar = ["SchemaFilter", "ShowFilter", "SelectShown", "ClearShown"];
    private static readonly string[] Summary = ["SelectionSummary", "Reread", "TableFilter"];

    public static TheoryData<int, int> Sizes() => new() { { 1200, 800 }, { 1000, 680 } };

    [Theory]
    [MemberData(nameof(Sizes))]
    public Task The_table_filter_keeps_a_usable_width(int width, int height) => fixture.RunAsync(async () =>
    {
        var ui = await UiDriver.OpenAsync();
        ui.Window.Width = width;
        ui.Window.Height = height;
        await ui.Wizard.ConnectSourceAsync();
        await ui.NextAsync();
        await ui.Wizard.ConnectTargetAsync();
        await ui.NextAsync();

        // Nothing on the left runs into the options column.
        var options = ui.Find<ItemsControl>("NamingChoices").TranslatePoint(default, ui.Window)!.Value.X;
        foreach (var name in Summary.Concat(Toolbar))
        {
            var control = ui.Find<Control>(name);
            var right = control.TranslatePoint(new Point(control.Bounds.Width, 0), ui.Window)!.Value.X;
            Assert.True(right <= options, $"{name} ends at {right}, past the options column at {options}, at {width}x{height}.");
        }

        var filter = ui.Find<TextBox>("TableFilter");
        Assert.True(filter.Bounds.Width >= 300, $"The filter is {filter.Bounds.Width} px wide at {width}x{height}.");
        foreach (var name in Toolbar)
        {
            Assert.True(InWindow(ui, ui.Find<Control>(name)), $"{name} is cut off at {width}x{height}.");
        }

        ui.Screenshot($"30-tables-{width}x{height}");
        ui.Window.Close();
    });

    [Theory]
    [MemberData(nameof(Sizes))]
    public Task A_long_question_scrolls_and_keeps_its_buttons_in_view(int width, int height) => fixture.RunAsync(async () =>
    {
        var ui = await UiDriver.OpenAsync();
        ui.Window.Width = width;
        ui.Window.Height = height;
        var bullets = Enumerable.Range(1, 40)
            .Select(i => $"New problem with dbo.Table{i}: public.table{i} already exists in the target. Choose to drop and recreate it, empty it, append to it or skip it.")
            .ToList();
        var answer = ui.Shell.Dialogs.ConfirmAsync(new ConfirmRequest("The target has changed since you reviewed the plan", "Review it again before running.", bullets, "Back to Review", "Stay here"));
        UiDriver.Settle();

        Assert.True(InWindow(ui, ui.Find<TextBlock>("DialogTitle")), "The title is cut off.");
        Assert.True(InWindow(ui, ui.Find<Button>("DialogConfirm")), "Back to Review is cut off.");
        Assert.True(InWindow(ui, ui.Find<Button>("DialogCancel")), "Stay here is cut off.");
        var body = ui.Find<ScrollViewer>("DialogBody");
        Assert.True(body.Extent.Height > body.Viewport.Height, "The bullets do not scroll.");
        ui.Screenshot($"31-long-question-{width}x{height}");

        ui.Click(ui.Find<Button>("DialogConfirm"));
        Assert.True(await answer);
        ui.Window.Close();
    });

    private static bool InWindow(UiDriver ui, Control control)
    {
        var topLeft = control.TranslatePoint(default, ui.Window);
        var bottomRight = control.TranslatePoint(new Point(control.Bounds.Width, control.Bounds.Height), ui.Window);
        return topLeft is { } a && bottomRight is { } b && control.Bounds.Width > 0
            && a.X >= 0 && a.Y >= 0 && b.X <= ui.Window.Bounds.Width && b.Y <= ui.Window.Bounds.Height;
    }
}
