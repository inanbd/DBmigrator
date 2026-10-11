using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DbMigrator.Core.Execution;

namespace DbMigrator.Desktop.ViewModels;

public sealed record ReportTable(
    string Source,
    string Target,
    string Action,
    string Status,
    long RowsCopied,
    long? SourceRowCount,
    long? TargetRowCount,
    double ElapsedSeconds,
    string? Message);

/// <param name="Status">"succeeded", "failed" or "not run".</param>
public sealed record ReportStep(string Description, string Status, double? ElapsedSeconds, string? Error, string Sql);

public sealed record ReportIssue(string Severity, string Code, string Subject, string Message);

public sealed record ReportAttentionGroup(string Title, int Count, IReadOnlyList<string> Items);

public sealed record ReportOptions(bool CopyData, bool CreateIndexes, bool CreateForeignKeys, bool VerifyRowCounts, string Naming);

/// <summary>
/// A finished run as it is saved and exported. Connections appear only as their description
/// ("shop on pg01:5433 as migrator"); no connection string or password is ever part of it.
/// </summary>
public sealed record ReportDocument
{
    public string Tool { get; init; } = "DbMigrator";

    public required string Version { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public required double ElapsedSeconds { get; init; }

    public required ReportOutcome Outcome { get; init; }

    public required string Headline { get; init; }

    public required string Summary { get; init; }

    public required string Source { get; init; }

    public required string SourceEngine { get; init; }

    public required string Target { get; init; }

    public required string TargetEngine { get; init; }

    public string? TargetServerVersion { get; init; }

    public string? CancelledDuring { get; init; }

    public string? FatalError { get; init; }

    public required long TotalRowsCopied { get; init; }

    public required ReportOptions Options { get; init; }

    public required IReadOnlyList<ReportAttentionGroup> Attention { get; init; }

    public required IReadOnlyList<ReportTable> Tables { get; init; }

    public required IReadOnlyList<ReportStep> Steps { get; init; }

    public required IReadOnlyList<ReportIssue> Issues { get; init; }

    /// <summary>Builds the document for the session's last run.</summary>
    public static ReportDocument From(MigrationSession session, string version)
    {
        var report = session.Report ?? throw new InvalidOperationException("There is no report yet.");
        var options = session.Prepared?.Options ?? session.BuildOptions();
        var outcome = ReportSummary.Outcome(report, session.RunFailure);
        var script = session.Prepared?.Script;
        var steps = report.Steps
            .Select(s => new ReportStep(s.Step.Description, s.Succeeded ? "succeeded" : "failed", s.Elapsed.TotalSeconds, s.Error, s.Step.Sql))
            .Concat(report.StepsNotRun.Select(s => new ReportStep(s.Description, "not run", null, null, s.Sql)))
            .ToList();
        return new ReportDocument
        {
            Version = version,
            StartedAt = session.RunStartedAt ?? DateTimeOffset.UnixEpoch,
            ElapsedSeconds = Math.Round(report.Elapsed.TotalSeconds, 3),
            Outcome = outcome,
            Headline = ReportSummary.Headline(report, outcome),
            Summary = ReportSummary.SubHeadline(report, outcome, options),
            Source = session.Source?.Description ?? "",
            SourceEngine = session.Source?.Provider.DisplayName ?? "",
            Target = session.Target?.Description ?? "",
            TargetEngine = session.TargetProvider?.DisplayName ?? "",
            TargetServerVersion = session.Prepared?.Target.ServerVersion,
            CancelledDuring = report.CancelledDuring is { } phase ? ReportWriter.DescribePhase(phase) : null,
            FatalError = report.FatalError,
            TotalRowsCopied = report.TotalRowsCopied,
            Options = new ReportOptions(options.CopyData, options.CreateIndexes, options.CreateForeignKeys, options.VerifyRowCounts, options.Naming.Case.ToString()),
            Attention = ReportAttention.Build(report, options)
                .Select(g => new ReportAttentionGroup(g.Title, g.Count, g.Items.Select(i => i.Detail is null ? i.Title : $"{i.Title}: {i.Detail}").ToList()))
                .ToList(),
            Tables = report.Tables.Select(t => new ReportTable(
                t.Table.Source.Name.ToString(),
                t.Table.Target.ToString(),
                ReportWriter.DescribeAction(t.Table.Action),
                TableRunStatuses.Describe(TableRunStatuses.From(t, report.InterruptedTable)),
                t.RowsCopied,
                t.SourceRowCount,
                t.TargetRowCount,
                Math.Round(t.Elapsed.TotalSeconds, 3),
                t.Error)).ToList(),
            Steps = steps,
            Issues = report.Issues.Select(i => new ReportIssue(i.Severity.ToString(), i.Code, i.Subject, i.Message)).ToList(),
            RemainingSql = script is null ? [] : ReportAttention.RemainingSql(report, script)
                .Where(s => s.Steps.Count > 0)
                .Select(s => new ReportSqlSection(s.Heading, s.Steps.Select(st => new ReportSqlStep(st.Description, st.Sql)).ToList()))
                .ToList(),
        };
    }

    /// <summary>SQL the run did not complete, in the order to run it.</summary>
    public IReadOnlyList<ReportSqlSection> RemainingSql { get; init; } = [];
}

public sealed record ReportSqlStep(string Description, string Sql);

public sealed record ReportSqlSection(string Heading, IReadOnlyList<ReportSqlStep> Steps);

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ReportDocument))]
internal sealed partial class ReportJsonContext : JsonSerializerContext;

/// <summary>Writes a <see cref="ReportDocument"/> as Markdown for people and JSON for tools.</summary>
public static class ReportExporter
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    public static string ToJson(ReportDocument document) => JsonSerializer.Serialize(document, ReportJsonContext.Default.ReportDocument);

    public static ReportDocument? FromJson(string json) => JsonSerializer.Deserialize(json, ReportJsonContext.Default.ReportDocument);

    /// <summary>"dbmigrator-20261010-120405-succeeded", in the user's time zone.</summary>
    public static string BaseName(ReportDocument document, TimeProvider time)
    {
        var local = TimeZoneInfo.ConvertTime(document.StartedAt, time.LocalTimeZone);
        var outcome = document.Outcome switch
        {
            ReportOutcome.Succeeded => "succeeded",
            ReportOutcome.CompletedWithProblems => "problems",
            ReportOutcome.Cancelled => "cancelled",
            _ => "stopped",
        };
        return $"dbmigrator-{local.ToString("yyyyMMdd-HHmmss", Culture)}-{outcome}";
    }

    public static string ToMarkdown(ReportDocument document)
    {
        var md = new StringBuilder();
        md.Append("# ").AppendLine(Escape(document.Headline)).AppendLine();
        md.AppendLine(Escape(document.Summary)).AppendLine();
        md.AppendLine("| | |").AppendLine("|---|---|");
        Row(md, "Source", $"{document.Source} ({document.SourceEngine})");
        Row(md, "Target", $"{document.Target} ({document.TargetEngine}{(document.TargetServerVersion is { } v ? $", {v}" : "")})");
        Row(md, "Started", document.StartedAt.ToString("yyyy-MM-dd HH:mm:ss zzz", Culture));
        Row(md, "Duration", ReportWriter.FormatElapsed(TimeSpan.FromSeconds(document.ElapsedSeconds)));
        Row(md, "Rows copied", document.TotalRowsCopied.ToString("N0", Culture));
        var o = document.Options;
        Row(md, "Options", string.Join(" · ",
            o.CopyData ? "data copied" : "no data",
            o.CreateIndexes ? "indexes on" : "indexes off",
            o.CreateForeignKeys ? "foreign keys on" : "foreign keys off",
            o.VerifyRowCounts ? "row counts checked" : "row counts not checked",
            $"names {o.Naming.ToLowerInvariant()}"));
        Row(md, "DbMigrator", document.Version);
        md.AppendLine();

        if (document.Attention.Count > 0)
        {
            md.AppendLine("## Needs attention").AppendLine();
            foreach (var group in document.Attention)
            {
                md.Append("### ").Append(Escape(group.Title)).Append(" (").Append(group.Count.ToString("N0", Culture)).AppendLine(")").AppendLine();
                foreach (var item in group.Items)
                {
                    md.Append("- ").AppendLine(Escape(item));
                }

                md.AppendLine();
            }
        }

        md.AppendLine("## Tables").AppendLine();
        md.AppendLine("| Status | Source | Target | Action | Rows copied | Source count | Target count | Elapsed | Message |");
        md.AppendLine("|---|---|---|---|---:|---:|---:|---:|---|");
        foreach (var t in document.Tables)
        {
            md.Append("| ").Append(Cell(t.Status))
                .Append(" | ").Append(Cell(t.Source))
                .Append(" | ").Append(Cell(t.Target))
                .Append(" | ").Append(Cell(t.Action))
                .Append(" | ").Append(t.RowsCopied.ToString("N0", Culture))
                .Append(" | ").Append(t.SourceRowCount?.ToString("N0", Culture) ?? "")
                .Append(" | ").Append(t.TargetRowCount?.ToString("N0", Culture) ?? "")
                .Append(" | ").Append(t.ElapsedSeconds > 0 ? ReportWriter.FormatElapsed(TimeSpan.FromSeconds(t.ElapsedSeconds)) : "")
                .Append(" | ").Append(Cell(t.Message ?? ""))
                .AppendLine(" |");
        }

        md.AppendLine();
        if (document.RemainingSql.Count > 0)
        {
            md.AppendLine("## SQL still to run").AppendLine();
            foreach (var section in document.RemainingSql)
            {
                md.Append("### ").AppendLine(Escape(section.Heading)).AppendLine();
                md.AppendLine(Fence(section.Steps.Select(s => $"-- {s.Description}\n{s.Sql};")));
                md.AppendLine();
            }
        }

        if (document.Issues.Count > 0)
        {
            md.AppendLine("## Issues").AppendLine();
            md.AppendLine("| Severity | Code | Subject | Message |").AppendLine("|---|---|---|---|");
            foreach (var issue in document.Issues)
            {
                md.Append("| ").Append(Cell(issue.Severity))
                    .Append(" | ").Append(Cell(issue.Code))
                    .Append(" | ").Append(Cell(issue.Subject))
                    .Append(" | ").Append(Cell(issue.Message))
                    .AppendLine(" |");
            }

            md.AppendLine();
        }

        return md.ToString();
    }

    private static void Row(StringBuilder md, string label, string value) =>
        md.Append("| ").Append(label).Append(" | ").Append(Cell(value)).AppendLine(" |");

    /// <summary>Text in a table cell: pipes escaped and line breaks flattened, so the table holds together.</summary>
    private static string Cell(string text) => Escape(text).Replace("|", "\\|", StringComparison.Ordinal).Replace("\r", "", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);

    /// <summary>Stops text from the database (names, messages) from being read as HTML.</summary>
    private static string Escape(string text) => text.Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);

    /// <summary>A sql code fence longer than any run of backticks inside it.</summary>
    private static string Fence(IEnumerable<string> blocks)
    {
        var body = string.Join("\n\n", blocks);
        var longest = 0;
        var run = 0;
        foreach (var c in body)
        {
            run = c == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }

        var fence = new string('`', Math.Max(3, longest + 1));
        return $"{fence}sql\n{body}\n{fence}";
    }
}
