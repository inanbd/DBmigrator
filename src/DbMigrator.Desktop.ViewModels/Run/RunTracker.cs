using CommunityToolkit.Mvvm.ComponentModel;
using DbMigrator.Core.Execution;
using DbMigrator.Core.Planning;
using DbMigrator.Core.Schema;

namespace DbMigrator.Desktop.ViewModels;

public enum TableRunStatus
{
    Waiting,
    Copying,
    Copied,
    Verified,
    Failed,
    RolledBack,
    Mismatch,
    Skipped,
    NotStarted,
}

/// <summary>One table's progress during a run.</summary>
public sealed partial class TableProgressRowViewModel : ObservableObject
{
    private readonly TimeProvider _time;

    public TableProgressRowViewModel(int index, TablePlan table, bool receivesData, TimeProvider time)
    {
        _time = time;
        Index = index;
        Table = table;
        Status = receivesData ? TableRunStatus.Waiting : TableRunStatus.Skipped;
    }

    public int Index { get; }

    public TablePlan Table { get; }

    public QualifiedName Source => Table.Source.Name;

    public string Name => $"{Table.Source.Name} → {Table.Target}";

    public long? EstimatedRows => Table.Source.EstimatedRowCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(IsProblem), nameof(IsDone), nameof(IsActive), nameof(Percent), nameof(IsIndeterminate))]
    public partial TableRunStatus Status { get; set; }

    public string StatusText => TableRunStatuses.Describe(Status);

    public bool IsProblem => TableRunStatuses.IsProblem(Status);

    public bool IsDone => Status is TableRunStatus.Copied or TableRunStatus.Verified;

    public bool IsActive => Status == TableRunStatus.Copying;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RowsText), nameof(Percent), nameof(IsIndeterminate), nameof(RateText))]
    public partial long RowsCopied { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ElapsedText), nameof(RateText))]
    public partial TimeSpan Elapsed { get; set; }

    [ObservableProperty]
    public partial string? Message { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public string RowsText => EstimatedRows is { } estimate ? $"{Humanize.Count(RowsCopied)} / ~{Humanize.Count(estimate)}" : Humanize.Count(RowsCopied);

    /// <summary>0–100 against the catalog estimate.</summary>
    public double Percent => IsDone ? 100 : EstimatedRows is > 0 and var estimate ? Math.Min(100, 100.0 * RowsCopied / estimate) : 0;

    /// <summary>No usable estimate, or the copy has gone past it.</summary>
    public bool IsIndeterminate => Status == TableRunStatus.Copying && (EstimatedRows is not > 0 || RowsCopied > EstimatedRows);

    public string RateText => Elapsed.TotalSeconds >= 1 ? $"{Humanize.Rate(RowsCopied / Elapsed.TotalSeconds)}/s" : "";

    /// <summary>The rate as a number, to sort by.</summary>
    public double SortRate => Elapsed.TotalSeconds >= 1 ? RowsCopied / Elapsed.TotalSeconds : -1;

    public string ElapsedText => Elapsed > TimeSpan.Zero ? Humanize.Duration(Elapsed) : "";

    /// <summary>Advances the elapsed clock of a table being copied between engine events.</summary>
    public void Tick()
    {
        if (Status == TableRunStatus.Copying && StartedAt is { } started)
        {
            Elapsed = _time.GetUtcNow() - started;
        }
    }
}

/// <summary>The words and meaning of each table status, shared by the Run and Report steps.</summary>
public static class TableRunStatuses
{
    public static string Describe(TableRunStatus status) => status switch
    {
        TableRunStatus.Waiting => "Waiting",
        TableRunStatus.Copying => "Copying",
        TableRunStatus.Copied => "Copied",
        TableRunStatus.Verified => "Copied, counts match",
        TableRunStatus.Failed => "Failed",
        TableRunStatus.RolledBack => "Rolled back",
        TableRunStatus.Mismatch => "Count mismatch",
        TableRunStatus.Skipped => "Skipped",
        _ => "Not started",
    };

    public static bool IsProblem(TableRunStatus status) =>
        status is TableRunStatus.Failed or TableRunStatus.RolledBack or TableRunStatus.Mismatch or TableRunStatus.NotStarted;

    /// <summary>A table's status from its result in the report.</summary>
    /// <param name="interrupted">The table that was being copied when the run was cancelled, if any.</param>
    public static TableRunStatus From(TableResult result, TablePlan? interrupted) => result.Status switch
    {
        TableStatus.Copied when result.SourceRowCount is not null => TableRunStatus.Verified,
        TableStatus.Copied => TableRunStatus.Copied,
        TableStatus.Failed => TableRunStatus.Failed,
        TableStatus.CountMismatch => TableRunStatus.Mismatch,
        TableStatus.Cancelled when ReferenceEquals(result.Table, interrupted) => TableRunStatus.RolledBack,
        TableStatus.Cancelled => TableRunStatus.NotStarted,
        _ => TableRunStatus.Skipped,
    };
}

public enum PhaseState
{
    Waiting,
    Running,
    Done,
    Stopped,
}

/// <summary>One phase of the run in the phase strip.</summary>
public sealed partial class PhaseViewModel(MigrationPhase phase, string label) : ObservableObject
{
    public MigrationPhase Phase => phase;

    public string Label => label;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText), nameof(IsRunning), nameof(IsDone), nameof(StatusLine))]
    public partial PhaseState State { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLine))]
    public partial string ElapsedText { get; set; } = "";

    /// <summary>"Running · 2:41", or just the state before it has a time.</summary>
    public string StatusLine => ElapsedText.Length == 0 ? StateText : $"{StateText} · {ElapsedText}";

    public DateTimeOffset? StartedAt { get; set; }

    public string StateText => State switch
    {
        PhaseState.Running => "Running",
        PhaseState.Done => "Done",
        PhaseState.Stopped => "Stopped",
        _ => "Waiting",
    };

    public bool IsRunning => State == PhaseState.Running;

    public bool IsDone => State == PhaseState.Done;
}

/// <summary>
/// Applies engine events to the run's model: per-table rows, phases, steps and verification.
/// Holds no UI types, so it is tested directly.
/// </summary>
public sealed class RunTracker
{
    private readonly TimeProvider _time;
    private readonly Dictionary<QualifiedName, TableProgressRowViewModel> _byName;
    private readonly List<StepResult> _steps = [];

    public RunTracker(PreparedMigration migration, TimeProvider time)
    {
        _time = time;
        Migration = migration;
        var copyData = migration.Options.CopyData;
        Rows = migration.Plan.Tables.Select((t, i) => new TableProgressRowViewModel(i + 1, t, t.ReceivesData && copyData, time)).ToList();
        _byName = Rows.ToDictionary(r => r.Source);
        Phases =
        [
            new(MigrationPhase.CreatingTables, "Create tables"),
            new(MigrationPhase.CopyingData, "Copy data"),
            new(MigrationPhase.CreatingConstraints, "Build keys and indexes"),
            new(MigrationPhase.VerifyingRowCounts, "Check row counts"),
        ];
        StepsTotal = migration.Script.AfterData.Count;
        StartedAt = time.GetUtcNow();
    }

    public PreparedMigration Migration { get; }

    public IReadOnlyList<TableProgressRowViewModel> Rows { get; }

    public IReadOnlyList<PhaseViewModel> Phases { get; }

    public DateTimeOffset StartedAt { get; }

    public MigrationPhase? Phase { get; private set; }

    /// <summary>The table being copied, if any.</summary>
    public TableProgressRowViewModel? Copying { get; private set; }

    public ScriptStep? CurrentStep { get; private set; }

    public int CurrentStepIndex { get; private set; }

    public DateTimeOffset? CurrentStepStartedAt { get; private set; }

    public TableProgressRowViewModel? Verifying { get; private set; }

    public int VerifyIndex { get; private set; }

    public int VerifyCount { get; private set; }

    public DateTimeOffset? VerifyStartedAt { get; private set; }

    public int StepsTotal { get; }

    public int StepsDone { get; private set; }

    public int StepsFailed { get; private set; }

    public int TablesFinished { get; private set; }

    public int TablesFailed { get; private set; }

    /// <summary>Goes up whenever a table's status changes, so filtered views know to refresh.</summary>
    public int StatusVersion { get; private set; }

    /// <summary>The after-data steps finished so far, in order.</summary>
    public IReadOnlyList<StepResult> StepResults => _steps;

    /// <summary>Rows in finished tables plus the table being copied.</summary>
    public long RowsCopied => Rows.Sum(r => r.Status is TableRunStatus.Copied or TableRunStatus.Verified or TableRunStatus.Mismatch or TableRunStatus.Copying ? r.RowsCopied : 0);

    public int DataTableCount => Rows.Count(r => r.Status != TableRunStatus.Skipped);

    public TableProgressRowViewModel Row(QualifiedName source) => _byName[source];

    public void Apply(TimedEvent timed)
    {
        if (timed.Event is TableCopyStarted or TableCopyFinished or TableVerified)
        {
            StatusVersion++;
        }

        switch (timed.Event)
        {
            case PhaseStarted { Phase: var phase }:
                foreach (var p in Phases.Where(p => p.State == PhaseState.Running))
                {
                    p.State = PhaseState.Done;
                    p.ElapsedText = Humanize.Duration(timed.At - (p.StartedAt ?? timed.At));
                }

                var started = Phases.First(p => p.Phase == phase);
                started.State = PhaseState.Running;
                started.StartedAt = timed.At;
                Phase = phase;
                CurrentStep = null;
                Verifying = null;
                break;
            case TableCopyStarted { Table: var table }:
                var row = _byName[table.Source.Name];
                row.Status = TableRunStatus.Copying;
                row.StartedAt = timed.At;
                Copying = row;
                break;
            case TableCopyFinished { Result: var result }:
                Finish(result);
                break;
            case StepStarted step:
                CurrentStep = step.Step;
                CurrentStepIndex = step.Index + 1; // the engine counts from 0
                CurrentStepStartedAt = timed.At;
                break;
            case StepFinished { Result: var stepResult }:
                _steps.Add(stepResult);
                StepsDone++;
                if (!stepResult.Succeeded)
                {
                    StepsFailed++;
                }

                break;
            case TableVerifyStarted verify:
                Verifying = _byName[verify.Table.Source.Name];
                VerifyIndex = verify.Index + 1; // the engine counts from 0
                VerifyCount = verify.Count;
                VerifyStartedAt = timed.At;
                break;
            case TableVerified { Result: var verified }:
                var verifiedRow = _byName[verified.Table.Source.Name];
                // A table whose counts could not be read stays Copied, with the reason as its message.
                verifiedRow.Status = TableRunStatuses.From(verified, interrupted: null);
                verifiedRow.Message = verified.Error;
                if (verified.Status == TableStatus.CountMismatch)
                {
                    TablesFailed++;
                }

                break;
        }
    }

    public void ApplyProgress(TableCopyProgress progress)
    {
        if (_byName.TryGetValue(progress.Table.Source.Name, out var row) && row.Status == TableRunStatus.Copying)
        {
            row.RowsCopied = progress.RowsCopied;
            row.Elapsed = progress.Elapsed;
        }
    }

    /// <summary>Brings every row in line with the final report, which is the source of truth.</summary>
    public void ApplyReport(MigrationReport report)
    {
        foreach (var result in report.Tables)
        {
            if (_byName.TryGetValue(result.Table.Source.Name, out var row))
            {
                SetFromResult(row, result, report.InterruptedTable);
            }
        }

        // A run that stopped early never reached these tables.
        foreach (var row in Rows.Where(r => r.Status is TableRunStatus.Waiting or TableRunStatus.Copying))
        {
            row.Status = TableRunStatus.NotStarted;
        }

        StatusVersion++;
        foreach (var phase in Phases.Where(p => p.State == PhaseState.Running))
        {
            phase.State = report.Cancelled || report.FatalError is not null ? PhaseState.Stopped : PhaseState.Done;
            phase.ElapsedText = Humanize.Duration(_time.GetUtcNow() - (phase.StartedAt ?? _time.GetUtcNow()));
        }

        Copying = null;
        CurrentStep = null;
        Verifying = null;
    }

    /// <summary>
    /// A report made from what was seen before the run failed unexpectedly, so the user still learns
    /// which tables were copied. The table being copied is reported as failed: nothing says it was kept.
    /// </summary>
    public MigrationReport ToFailureReport(Exception failure)
    {
        var tables = Rows.Select(r => r.Status switch
        {
            TableRunStatus.Copied or TableRunStatus.Verified => new TableResult { Table = r.Table, Status = TableStatus.Copied, RowsCopied = r.RowsCopied, Elapsed = r.Elapsed },
            TableRunStatus.Mismatch => new TableResult { Table = r.Table, Status = TableStatus.CountMismatch, RowsCopied = r.RowsCopied, Elapsed = r.Elapsed, Error = r.Message },
            TableRunStatus.Failed => new TableResult { Table = r.Table, Status = TableStatus.Failed, RowsCopied = r.RowsCopied, Elapsed = r.Elapsed, Error = r.Message },
            TableRunStatus.Copying => new TableResult { Table = r.Table, Status = TableStatus.Failed, Elapsed = r.Elapsed, Error = "The run stopped while this table was being copied; check it in the target." },
            TableRunStatus.Waiting => new TableResult { Table = r.Table, Status = TableStatus.NotCopied, Error = "Not started: the run stopped first." },
            _ => new TableResult { Table = r.Table, Status = TableStatus.NotCopied },
        }).ToList();
        return new MigrationReport
        {
            Plan = Migration.Plan,
            Tables = tables,
            Steps = _steps.ToList(),
            StepsNotRun = Migration.Script.AfterData.Skip(_steps.Count).ToList(),
            Issues = Migration.Plan.Issues,
            Elapsed = _time.GetUtcNow() - StartedAt,
            FatalError = $"The migration stopped unexpectedly: {failure.Message}",
        };
    }

    /// <summary>Advances elapsed clocks between engine events.</summary>
    public void Tick()
    {
        var now = _time.GetUtcNow();
        Copying?.Tick();
        foreach (var phase in Phases.Where(p => p.State == PhaseState.Running))
        {
            phase.ElapsedText = Humanize.Duration(now - (phase.StartedAt ?? now));
        }
    }

    private void Finish(TableResult result)
    {
        var row = _byName[result.Table.Source.Name];
        SetFromResult(row, result, interrupted: result.Status == TableStatus.Cancelled ? result.Table : null);
        if (result.Status != TableStatus.NotCopied)
        {
            TablesFinished++;
        }

        if (result.Status == TableStatus.Failed)
        {
            TablesFailed++;
        }

        if (Copying == row)
        {
            Copying = null;
        }
    }

    private static void SetFromResult(TableProgressRowViewModel row, TableResult result, TablePlan? interrupted)
    {
        row.Status = TableRunStatuses.From(result, interrupted);
        if (result.Status != TableStatus.Cancelled || ReferenceEquals(result.Table, interrupted))
        {
            row.RowsCopied = result.RowsCopied;
        }

        if (result.Elapsed > TimeSpan.Zero)
        {
            row.Elapsed = result.Elapsed;
        }

        row.Message = result.Error;
    }
}
