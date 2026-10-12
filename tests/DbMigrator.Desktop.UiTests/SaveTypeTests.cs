using DbMigrator.Desktop.Services;
using DbMigrator.Desktop.ViewModels;

namespace DbMigrator.Desktop.UiTests;

public sealed class SaveTypeTests
{
    private static readonly FileTypeChoice[] Types = [new("Markdown", ".md"), new("JSON", ".json")];

    [Theory]
    [InlineData("report.md", "JSON", ".md")]
    [InlineData("report.json", "Markdown", ".json")]
    [InlineData("REPORT.JSON", null, ".json")]
    [InlineData("report", "JSON", ".json")]
    [InlineData("report.txt", null, ".md")]
    public void The_name_decides_the_format_and_the_filter_only_breaks_a_tie(string name, string? selected, string expected) =>
        Assert.Equal(expected, SaveTypes.Choose(Types, name, selected).Extension);
}
