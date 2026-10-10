using System.Globalization;
using DbMigrator.Core.Execution;

namespace DbMigrator.Tests.Execution;

/// <summary>
/// The CLI's plan and report text, captured before it moved from the CLI into
/// <see cref="ReportWriter"/>. The move must not change a byte.
/// </summary>
public class ReportWriterTests
{
    private static readonly GoldenData.Set Data = GoldenData.Create();

    private static string Golden(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Execution", "Golden", name + ".txt")).ReplaceLineEndings();

    private static string Write(Action<TextWriter> write)
    {
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            using var output = new StringWriter();
            write(output);
            return output.ToString();
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    [Fact]
    public void Plan_text_is_unchanged() =>
        Assert.Equal(Golden("plan"), Write(o =>
        {
            ReportWriter.WritePlan(o, Data.Migration);
            ReportWriter.WriteIssues(o, Data.Migration.Plan.Issues);
        }));

    [Fact]
    public void Successful_report_text_is_unchanged() =>
        Assert.Equal(Golden("success"), Write(o => ReportWriter.WriteReport(o, Data.Success)));

    [Fact]
    public void Report_with_problems_text_is_unchanged() =>
        Assert.Equal(Golden("problems"), Write(o => ReportWriter.WriteReport(o, Data.Problems)));

    [Fact]
    public void Stopped_report_text_is_unchanged() =>
        Assert.Equal(Golden("fatal"), Write(o => ReportWriter.WriteReport(o, Data.Fatal)));

    [Fact]
    public void Table_lines_show_each_outcome()
    {
        var text = Write(o => ReportWriter.WriteTables(o, Data.Problems));

        Assert.Contains("FAILED       dbo.Customers -> public.customers: 0 rows. Failed after reading 17 row(s)", text);
        Assert.Contains("MISMATCH     dbo.Wide -> public.wide: 3,500,000 rows (source 3,500,001, target 3,500,000).", text);
    }
}
