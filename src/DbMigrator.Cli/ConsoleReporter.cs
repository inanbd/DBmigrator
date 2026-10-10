using DbMigrator.Core.Diagnostics;
using DbMigrator.Core.Execution;
using DbMigrator.Core.Planning;

namespace DbMigrator.Cli;

/// <summary>Writes plans, progress and reports as plain text. Reports synchronously, so lines keep their order.</summary>
internal sealed class ConsoleReporter(TextWriter output) : IProgress<MigrationEvent>
{
    private const int IssuesShownPerCode = 5;

    public void WritePlan(PreparedMigration migration)
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
            var action = table.IsUnresolvedConflict ? "EXISTS" : ActionText(table.Action);
            output.WriteLine($"  {table.Source.Name.ToString().PadRight(width)}  ->  {table.Target}  [{action}]  {rows}".TrimEnd());
        }

        if (plan.SchemasToCreate.Count > 0)
        {
            output.WriteLine($"Schemas to create: {string.Join(", ", plan.SchemasToCreate)}");
        }
    }

    public void WriteIssues(IReadOnlyList<MigrationIssue> issues)
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
                foreach (var issue in byCode.Take(IssuesShownPerCode))
                {
                    output.WriteLine($"  [{issue.Code}] {issue.Subject}: {issue.Message}");
                }

                var hidden = byCode.Count() - IssuesShownPerCode;
                if (hidden > 0)
                {
                    output.WriteLine($"  [{byCode.Key}] ... and {hidden} more like this.");
                }
            }
        }
    }

    public void Report(MigrationEvent value)
    {
        switch (value)
        {
            case PhaseStarted { Phase: var phase }:
                output.WriteLine();
                output.WriteLine(phase switch
                {
                    MigrationPhase.CreatingTables => "Creating tables...",
                    MigrationPhase.CopyingData => "Copying data...",
                    MigrationPhase.CreatingConstraints => "Creating keys, indexes and foreign keys...",
                    _ => "Checking row counts...",
                });
                break;
            case TableCopyProgress progress:
                var total = progress.Table.Source.EstimatedRowCount is { } estimate and > 0 ? $" of ~{estimate:N0}" : "";
                var rate = progress.Elapsed.TotalSeconds > 0 ? progress.RowsCopied / progress.Elapsed.TotalSeconds : 0;
                output.WriteLine($"    {progress.Table.Source.Name}: {progress.RowsCopied:N0}{total} rows ({rate:N0} rows/s)");
                break;
            case TableCopyFinished { Result: var result } when result.Status != TableStatus.NotCopied:
                output.WriteLine(result.Status == TableStatus.Failed
                    ? $"  FAILED  {result.Table.Source.Name}: {result.Error}"
                    : $"  ok      {result.Table.Source.Name} -> {result.Table.Target}: {result.RowsCopied:N0} rows in {Format(result.Elapsed)}");
                break;
            case StepFinished { Result: { Succeeded: false } step }:
                output.WriteLine($"  FAILED  {step.Step.Description}: {step.Error}");
                break;
        }
    }

    public void WriteReport(MigrationReport report)
    {
        foreach (var mismatch in report.Tables.Where(t => t.Status == TableStatus.CountMismatch))
        {
            output.WriteLine($"  MISMATCH  {mismatch.Table.Source.Name}: {mismatch.Error}");
        }

        WriteIssues(report.Issues);
        output.WriteLine();

        if (report.FatalError is not null)
        {
            output.WriteLine($"Migration stopped: {report.FatalError}");
            return;
        }

        var copied = report.Tables.Count(t => t.Status == TableStatus.Copied);
        var failedTables = report.Tables.Count(t => t.Status is TableStatus.Failed or TableStatus.CountMismatch);
        var failedSteps = report.Steps.Count(s => !s.Succeeded);
        output.WriteLine($"Copied {report.TotalRowsCopied:N0} rows into {copied} table(s) in {Format(report.Elapsed)}.");
        output.WriteLine($"Built {report.Steps.Count - failedSteps} of {report.Steps.Count} key, index and identity step(s).");
        output.WriteLine(report.Succeeded
            ? "Migration succeeded."
            : $"Migration finished with problems: {failedTables} table(s) and {failedSteps} step(s) failed.");
    }

    private static string ActionText(TableAction action) => action switch
    {
        TableAction.Create => "create",
        TableAction.DropAndCreate => "drop and recreate",
        TableAction.Truncate => "truncate and load",
        TableAction.Append => "append",
        _ => "skip",
    };

    private static string Format(TimeSpan elapsed) =>
        elapsed.TotalSeconds < 60 ? $"{elapsed.TotalSeconds:0.0}s" : elapsed.ToString(@"h\:mm\:ss", System.Globalization.CultureInfo.InvariantCulture);
}
