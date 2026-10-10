using System.Diagnostics;
using DbMigrator.Core.Diagnostics;
using DbMigrator.Core.Planning;
using DbMigrator.Core.Providers;
using DbMigrator.Core.Schema;

namespace DbMigrator.Core.Execution;

/// <summary>A plan ready to run, with everything that went into it so it can be re-planned with other options.</summary>
public sealed record PreparedMigration(
    SourceSchema Source,
    TargetInfo Target,
    MigrationOptions Options,
    MigrationPlan Plan,
    MigrationScript Script);

public static class MigrationEngine
{
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(1);

    /// <summary>Reads both databases and plans the migration. Changes nothing.</summary>
    public static async Task<PreparedMigration> PrepareAsync(
        ISourceDatabase source,
        ITargetDatabase target,
        MigrationOptions options,
        CancellationToken cancellationToken)
    {
        var schema = await source.ReadSchemaAsync(cancellationToken);
        var info = await target.InspectAsync(cancellationToken);
        return Prepare(schema, info, options);
    }

    public static PreparedMigration Prepare(SourceSchema source, TargetInfo target, MigrationOptions options)
    {
        var plan = MigrationPlanner.CreatePlan(source, target.Dialect, target.State, options);
        return new PreparedMigration(source, target, options, plan, ScriptBuilder.Build(plan, target.Dialect));
    }

    /// <summary>
    /// Runs a prepared migration: creates tables in one transaction, bulk-copies each table,
    /// then builds keys and indexes and checks row counts. A table that fails to copy or a
    /// constraint that fails to build is recorded and the run carries on with the rest.
    /// </summary>
    public static async Task<MigrationReport> ExecuteAsync(
        PreparedMigration migration,
        ISourceDatabase source,
        ITargetDatabase target,
        IProgress<MigrationEvent>? progress,
        CancellationToken cancellationToken)
    {
        var plan = migration.Plan;
        if (plan.HasErrors)
        {
            throw new InvalidOperationException("The plan has errors; resolve them before running the migration.");
        }

        var clock = Stopwatch.StartNew();
        var issues = new List<MigrationIssue>(plan.Issues);
        var tables = new List<TableResult>();
        var steps = new List<StepResult>();
        MigrationReport Report(string? fatalError = null) => new()
        {
            Plan = plan,
            Tables = tables,
            Steps = steps,
            Issues = issues,
            Elapsed = clock.Elapsed,
            FatalError = fatalError,
        };

        progress?.Report(new PhaseStarted(MigrationPhase.CreatingTables));
        try
        {
            await target.ExecuteAsync(migration.Script.BeforeData.Select(s => s.Sql).ToList(), inTransaction: true, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Report($"Preparing the target tables (drop, truncate, create) failed: {ex.Message}");
        }

        progress?.Report(new PhaseStarted(MigrationPhase.CopyingData));
        foreach (var table in plan.Tables)
        {
            TableResult result;
            if (!table.ReceivesData || !migration.Options.CopyData)
            {
                result = new TableResult { Table = table, Status = TableStatus.NotCopied };
            }
            else
            {
                progress?.Report(new TableCopyStarted(table));
                result = await CopyTableAsync(table, source, target, issues, progress, cancellationToken);
            }

            tables.Add(result);
            progress?.Report(new TableCopyFinished(result));
        }

        progress?.Report(new PhaseStarted(MigrationPhase.CreatingConstraints));
        foreach (var step in migration.Script.AfterData)
        {
            var stepClock = Stopwatch.StartNew();
            string? error = null;
            try
            {
                await target.ExecuteAsync([step.Sql], inTransaction: false, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                error = ex.Message;
            }

            var stepResult = new StepResult(step, stepClock.Elapsed, error);
            steps.Add(stepResult);
            progress?.Report(new StepFinished(stepResult));
        }

        if (migration.Options.VerifyRowCounts && migration.Options.CopyData)
        {
            progress?.Report(new PhaseStarted(MigrationPhase.VerifyingRowCounts));
            for (var i = 0; i < tables.Count; i++)
            {
                tables[i] = await VerifyAsync(tables[i], source, target, cancellationToken);
            }
        }

        return Report();
    }

    private static async Task<TableResult> CopyTableAsync(
        TablePlan table,
        ISourceDatabase source,
        ITargetDatabase target,
        List<MigrationIssue> issues,
        IProgress<MigrationEvent>? progress,
        CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        long rows = 0;
        try
        {
            await using var reader = await source.OpenTableReaderAsync(
                table.Source, table.Columns.Select(c => c.Source).ToList(), cancellationToken);
            await using var writer = await target.OpenBulkWriterAsync(table, cancellationToken);
            var values = new object?[table.Columns.Count];
            var lastReport = TimeSpan.Zero;
            while (await reader.ReadAsync(cancellationToken))
            {
                reader.GetValues(values);
                await writer.WriteRowAsync(values, cancellationToken);
                rows++;
                if ((rows & 0xFFF) == 0 && clock.Elapsed - lastReport >= ProgressInterval)
                {
                    lastReport = clock.Elapsed;
                    progress?.Report(new TableCopyProgress(table, rows, lastReport));
                }
            }

            var written = await writer.CompleteAsync(cancellationToken);
            issues.AddRange(written.Issues);
            return new TableResult { Table = table, Status = TableStatus.Copied, RowsCopied = written.Rows, Elapsed = clock.Elapsed };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new TableResult
            {
                Table = table,
                Status = TableStatus.Failed,
                Elapsed = clock.Elapsed,
                Error = $"Failed after reading {rows:N0} row(s); none were kept. {ex.Message}",
            };
        }
    }

    private static async Task<TableResult> VerifyAsync(TableResult result, ISourceDatabase source, ITargetDatabase target, CancellationToken cancellationToken)
    {
        if (result.Status != TableStatus.Copied)
        {
            return result;
        }

        long sourceCount, targetCount;
        try
        {
            sourceCount = await source.CountRowsAsync(result.Table.Source.Name, cancellationToken);
            targetCount = await target.CountRowsAsync(result.Table.Target, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return result with { Error = $"Row counts could not be checked: {ex.Message}" };
        }

        // Appending keeps the rows already there, so only the copied count can be checked.
        var matches = sourceCount == result.RowsCopied
            && (result.Table.Action == TableAction.Append || targetCount == sourceCount);
        return result with
        {
            SourceRowCount = sourceCount,
            TargetRowCount = targetCount,
            Status = matches ? TableStatus.Copied : TableStatus.CountMismatch,
            Error = matches ? null : $"The source has {sourceCount:N0} row(s), {result.RowsCopied:N0} were copied, and the target now has {targetCount:N0}.",
        };
    }
}
