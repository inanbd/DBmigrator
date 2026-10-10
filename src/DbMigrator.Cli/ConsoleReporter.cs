using DbMigrator.Core.Diagnostics;
using DbMigrator.Core.Execution;

namespace DbMigrator.Cli;

/// <summary>Writes plans, progress and reports as plain text. Reports synchronously, so lines keep their order.</summary>
internal sealed class ConsoleReporter(TextWriter output) : IProgress<MigrationEvent>
{
    public void WritePlan(PreparedMigration migration) => ReportWriter.WritePlan(output, migration);

    public void WriteIssues(IReadOnlyList<MigrationIssue> issues) => ReportWriter.WriteIssues(output, issues);

    public void WriteReport(MigrationReport report) => ReportWriter.WriteReport(output, report);

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
            case TableCopyFinished { Result: { Status: TableStatus.Cancelled } cancelled }:
                output.WriteLine($"  CANCELLED {cancelled.Table.Source.Name}: {cancelled.Error}");
                break;
            case TableCopyFinished { Result: var result } when result.Status != TableStatus.NotCopied:
                output.WriteLine(result.Status == TableStatus.Failed
                    ? $"  FAILED  {result.Table.Source.Name}: {result.Error}"
                    : $"  ok      {result.Table.Source.Name} -> {result.Table.Target}: {result.RowsCopied:N0} rows in {ReportWriter.FormatElapsed(result.Elapsed)}");
                break;
            case StepFinished { Result: { Succeeded: false } step }:
                output.WriteLine($"  FAILED  {step.Step.Description}: {step.Error}");
                break;
        }
    }
}
