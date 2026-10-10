using DbMigrator.Core.Diagnostics;
using DbMigrator.Core.Planning;

namespace DbMigrator.Core.Execution;

public enum MigrationPhase
{
    CreatingTables,
    CopyingData,
    CreatingConstraints,
    VerifyingRowCounts,
}

/// <summary>Progress notifications, raised on the migrating task in order.</summary>
public abstract record MigrationEvent;

public sealed record PhaseStarted(MigrationPhase Phase) : MigrationEvent;

public sealed record TableCopyStarted(TablePlan Table) : MigrationEvent;

public sealed record TableCopyProgress(TablePlan Table, long RowsCopied, TimeSpan Elapsed) : MigrationEvent;

public sealed record TableCopyFinished(TableResult Result) : MigrationEvent;

public sealed record StepFinished(StepResult Result) : MigrationEvent;

/// <summary>Raised before each after-data step, which can run for minutes on a large table.</summary>
public sealed record StepStarted(ScriptStep Step, int Index, int Count) : MigrationEvent;

/// <summary>Raised before a table's rows are counted on both sides.</summary>
public sealed record TableVerifyStarted(TablePlan Table, int Index, int Count) : MigrationEvent;

/// <summary>Raised after a table's counts have been compared.</summary>
public sealed record TableVerified(TableResult Result) : MigrationEvent;

public enum TableStatus
{
    Copied,
    Failed,
    /// <summary>Copied, but the row counts afterwards disagreed.</summary>
    CountMismatch,
    /// <summary>The plan copies no data into it (skipped, or schema only).</summary>
    NotCopied,
    /// <summary>The run was cancelled before this table finished copying; nothing this run wrote to it was kept.</summary>
    Cancelled,
}

public sealed record TableResult
{
    public required TablePlan Table { get; init; }

    public required TableStatus Status { get; init; }

    public long RowsCopied { get; init; }

    public TimeSpan Elapsed { get; init; }

    public string? Error { get; init; }

    public long? SourceRowCount { get; init; }

    public long? TargetRowCount { get; init; }
}

public sealed record StepResult(ScriptStep Step, TimeSpan Elapsed, string? Error = null)
{
    public bool Succeeded => Error is null;
}

public sealed record MigrationReport
{
    public required MigrationPlan Plan { get; init; }

    public required IReadOnlyList<TableResult> Tables { get; init; }

    /// <summary>
    /// The after-data steps that ran, in script order: always a prefix of <see cref="MigrationScript.AfterData"/>.
    /// They run independently, so one failure does not stop the rest.
    /// </summary>
    public required IReadOnlyList<StepResult> Steps { get; init; }

    /// <summary>After-data steps that never ran because the run stopped or was cancelled first. These are not failures.</summary>
    public IReadOnlyList<ScriptStep> StepsNotRun { get; init; } = [];

    /// <summary>The phase the run was in when it was cancelled, or null if it was not.</summary>
    public MigrationPhase? CancelledDuring { get; init; }

    public bool Cancelled => CancelledDuring is not null;

    /// <summary>The table being copied when the run was cancelled; its rows were rolled back.</summary>
    public TablePlan? InterruptedTable { get; init; }

    /// <summary>The plan's issues plus anything the copy itself reported.</summary>
    public required IReadOnlyList<MigrationIssue> Issues { get; init; }

    public required TimeSpan Elapsed { get; init; }

    /// <summary>Set when the migration stopped before finishing, e.g. because creating the tables failed.</summary>
    public string? FatalError { get; init; }

    public bool Succeeded =>
        FatalError is null
        && !Cancelled
        && Tables.All(t => t.Status is TableStatus.Copied or TableStatus.NotCopied)
        && Steps.All(s => s.Succeeded);

    public long TotalRowsCopied => Tables.Sum(t => t.RowsCopied);
}
