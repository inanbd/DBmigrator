using System.Globalization;
using DbMigrator.Core.Diagnostics;
using DbMigrator.Core.Planning;

namespace DbMigrator.Core.Execution;

/// <summary>Plain-text plans and reports, shared by the CLI and the desktop app's "copy summary".</summary>
public static class ReportWriter
{
    private const int EmptiedTablesListed = 10;

    public static void WritePlan(TextWriter output, PreparedMigration migration)
    {
        var plan = migration.Plan;
        output.WriteLine($"Source: {migration.Source.ServerVersion}");
        output.WriteLine($"Target: {migration.Target.ServerVersion}");
        output.WriteLine();
        output.WriteLine($"{plan.Tables.Count} table(s), in load order:");
        var width = plan.Tables.Count == 0 ? 0 : plan.Tables.Max(t => t.Source.Name.ToString().Length);
        foreach (var table in plan.Tables)
        {
            var rows = table.Source.EstimatedRowCount is { } estimate ? $"~{estimate:N0} rows" : "";
            var action = table.IsUnresolvedConflict ? "EXISTS" : DescribeAction(table.Action);
            output.WriteLine($"  {table.Source.Name.ToString().PadRight(width)}  ->  {table.Target}  [{action}]  {rows}".TrimEnd());
        }

        if (plan.SchemasToCreate.Count > 0)
        {
            output.WriteLine($"Schemas to create: {string.Join(", ", plan.SchemasToCreate)}");
        }
    }

    public static void WriteIssues(TextWriter output, IReadOnlyList<MigrationIssue> issues, int issuesShownPerCode = 5)
    {
        if (issues.Count == 0)
        {
            return;
        }

        foreach (var severity in new[] { IssueSeverity.Error, IssueSeverity.Warning, IssueSeverity.Info })
        {
            var group = issues.Where(i => i.Severity == severity).ToList();
            if (group.Count == 0)
            {
                continue;
            }

            output.WriteLine();
            output.WriteLine($"{severity switch { IssueSeverity.Error => "Errors", IssueSeverity.Warning => "Warnings", _ => "Notes" }} ({group.Count}):");
            foreach (var byCode in group.GroupBy(i => i.Code))
            {
                foreach (var issue in byCode.Take(issuesShownPerCode))
                {
                    output.WriteLine($"  [{issue.Code}] {issue.Subject}: {issue.Message}");
                }

                var hidden = byCode.Count() - issuesShownPerCode;
                if (hidden > 0)
                {
                    output.WriteLine($"  [{byCode.Key}] ... and {hidden} more like this.");
                }
            }
        }
    }

    public static void WriteReport(TextWriter output, MigrationReport report)
    {
        foreach (var mismatch in report.Tables.Where(t => t.Status == TableStatus.CountMismatch))
        {
            output.WriteLine($"  MISMATCH  {mismatch.Table.Source.Name}: {mismatch.Error}");
        }

        WriteIssues(output, report.Issues);
        output.WriteLine();

        if (report.FatalError is not null)
        {
            output.WriteLine($"Migration stopped: {report.FatalError}");
            return;
        }

        if (report.CancelledDuring is { } phase)
        {
            WriteCancellation(output, report, phase);
            return;
        }

        var copied = report.Tables.Count(t => t.Status == TableStatus.Copied);
        var failedTables = report.Tables.Count(t => t.Status is TableStatus.Failed or TableStatus.CountMismatch);
        var failedSteps = report.Steps.Count(s => !s.Succeeded);
        output.WriteLine($"Copied {report.TotalRowsCopied:N0} rows into {copied} table(s) in {FormatElapsed(report.Elapsed)}.");
        output.WriteLine($"Built {report.Steps.Count - failedSteps} of {report.Steps.Count} key, index and identity step(s).");
        output.WriteLine(report.Succeeded
            ? "Migration succeeded."
            : $"Migration finished with problems: {failedTables} table(s) and {failedSteps} step(s) failed.");
    }

    /// <summary>One line per planned table: its outcome, rows and any message.</summary>
    public static void WriteTables(TextWriter output, MigrationReport report)
    {
        foreach (var result in report.Tables)
        {
            var counts = result.SourceRowCount is { } source && result.TargetRowCount is { } target
                ? $" (source {source:N0}, target {target:N0})"
                : "";
            var message = result.Error is null ? "" : $" {result.Error}";
            output.WriteLine($"  {DescribeStatus(result).PadRight(12)} {result.Table.Source.Name} -> {result.Table.Target}: {result.RowsCopied:N0} rows{counts}.{message}");
        }
    }

    public static string DescribeAction(TableAction action) => action switch
    {
        TableAction.Create => "create",
        TableAction.DropAndCreate => "drop and recreate",
        TableAction.Truncate => "truncate and load",
        TableAction.Append => "append",
        _ => "skip",
    };

    public static string DescribePhase(MigrationPhase phase) => phase switch
    {
        MigrationPhase.CreatingTables => "creating tables",
        MigrationPhase.CopyingData => "copying data",
        MigrationPhase.CreatingConstraints => "building keys and indexes",
        _ => "checking row counts",
    };

    public static string FormatElapsed(TimeSpan elapsed) =>
        elapsed.TotalSeconds < 60 ? $"{elapsed.TotalSeconds:0.0}s" : elapsed.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture);

    /// <summary>
    /// Tables that a cancelled run left empty: emptied or dropped and recreated in the opening
    /// transaction, then never reloaded. None when the cancel came during that transaction,
    /// because it was rolled back.
    /// </summary>
    public static IReadOnlyList<TableResult> LeftEmpty(MigrationReport report) =>
        report.CancelledDuring is null or MigrationPhase.CreatingTables
            ? []
            : report.Tables
                .Where(t => t.Status == TableStatus.Cancelled && t.Table.Action is TableAction.Truncate or TableAction.DropAndCreate)
                .ToList();

    private static string DescribeStatus(TableResult result) => result.Status switch
    {
        TableStatus.Copied => "copied",
        TableStatus.Failed => "FAILED",
        TableStatus.CountMismatch => "MISMATCH",
        TableStatus.Cancelled => "CANCELLED",
        _ => "not copied",
    };

    private static void WriteCancellation(TextWriter output, MigrationReport report, MigrationPhase phase)
    {
        var where = phase == MigrationPhase.CreatingTables
            ? "creating tables (that step runs in one transaction, which was rolled back)"
            : DescribePhase(phase);
        output.WriteLine($"Cancelled while {where} after {FormatElapsed(report.Elapsed)}.");

        if (phase == MigrationPhase.CreatingTables)
        {
            output.WriteLine("Nothing was copied.");
        }
        else
        {
            var copied = report.Tables.Count(t => t.Status is TableStatus.Copied or TableStatus.CountMismatch);
            var notStarted = report.Tables.Count(t => t.Status == TableStatus.Cancelled && !ReferenceEquals(t.Table, report.InterruptedTable));
            var line = $"Copied {report.TotalRowsCopied:N0} rows into {copied} table(s)";
            if (report.InterruptedTable is { } interrupted)
            {
                line += $"; rolled back {interrupted.Source.Name}";
            }

            if (notStarted > 0)
            {
                line += $"; {notStarted} table(s) not started";
            }

            output.WriteLine(line + ".");
        }

        var failed = report.Tables.Count(t => t.Status == TableStatus.Failed);
        if (failed > 0)
        {
            output.WriteLine($"{failed} table(s) failed before the cancel.");
        }

        var empty = LeftEmpty(report);
        if (empty.Count > 0)
        {
            var names = string.Join(", ", empty.Take(EmptiedTablesListed).Select(t => t.Table.Target));
            var more = empty.Count > EmptiedTablesListed ? $", and {empty.Count - EmptiedTablesListed} more" : "";
            output.WriteLine($"{empty.Count} table(s) emptied or recreated at the start are now empty: {names}{more}.");
        }

        var notRun = report.StepsNotRun.Count;
        if (report.Steps.Count > 0)
        {
            var failedSteps = report.Steps.Count(s => !s.Succeeded);
            var total = report.Steps.Count + notRun;
            output.WriteLine($"Built {report.Steps.Count - failedSteps} of {total} key, index and identity step(s); {notRun} did not run.");
        }
        else if (notRun > 0)
        {
            output.WriteLine($"{notRun:N0} key, index and identity step(s) did not run.");
        }

        if (phase == MigrationPhase.VerifyingRowCounts)
        {
            var notChecked = report.Tables.Count(t => t.Status == TableStatus.Copied && t.SourceRowCount is null);
            if (notChecked > 0)
            {
                output.WriteLine($"Row counts were not checked for {notChecked} table(s).");
            }
        }
    }
}
