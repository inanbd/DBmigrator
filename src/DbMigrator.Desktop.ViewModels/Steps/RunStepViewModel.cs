using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbMigrator.Core.Execution;
using DbMigrator.Core.Planning;

namespace DbMigrator.Desktop.ViewModels;

public enum RunState
{
    /// <summary>Showing what will change; Start is offered.</summary>
    Ready,

    /// <summary>Checking the target has not changed since the review.</summary>
    Preflight,

    Running,

    /// <summary>Stop was confirmed; waiting for the engine to roll back the table in flight.</summary>
    Stopping,

    Finished,
}

public enum RunTableFilter
{
    All,
    Problems,
    Remaining,
}

/// <summary>
/// Runs the reviewed plan. Engine events are collected by <see cref="RunProgressHub"/> on the
/// engine's thread and pulled onto the UI four times a second, so a fast engine never floods the UI.
/// </summary>
public sealed partial class RunStepViewModel : WizardStepViewModel
{
    /// <summary>The most discrete events applied per tick; the rest wait for the next one.</summary>
    internal const int MaxEventsPerTick = 1000;

    internal static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>How long closing the window waits for a stopped run to roll back.</summary>
    internal static readonly TimeSpan CloseWait = TimeSpan.FromSeconds(30);

    private readonly ShellServices _services;
    private readonly MigrationSession _session;
    private PreparedMigration? _prepared;
    private RunTracker? _tracker;
    private RunProgressHub? _hub;
    private ThroughputEstimator? _estimator;
    private int _shownStatusVersion = -1;
    private TaskCompletionSource? _runFinished;
    private bool _stopBeforeRun;

    public RunStepViewModel(ShellServices services, MigrationSession session)
    {
        _services = services;
        _session = session;
    }

    public override WizardStepKind Kind => WizardStepKind.Run;

    public override string Title => "Run";

    public override string Subtitle => Stage switch
    {
        RunState.Running or RunState.Stopping => "The migration is running.",
        RunState.Finished => "The run has finished.",
        _ => "Start the migration when you are ready.",
    };

    public override string NextLabel => "See the report";

    public override bool IsApplicable => !_session.ScriptOnly;

    /// <summary>The footer is hidden while running; Stop is the only way out.</summary>
    public override bool ShowsFooter => Stage is RunState.Ready or RunState.Finished;

    public override bool CanAdvance => Stage == RunState.Finished && _session.Report is not null;

    public override string? BlockingReason => Stage switch
    {
        RunState.Ready or RunState.Preflight => "Start the migration to continue.",
        RunState.Running or RunState.Stopping => "The migration is running.",
        _ => null,
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning), nameof(IsReady), nameof(IsFinished), nameof(ShowsFooter), nameof(Subtitle), nameof(ShowsProgress))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand))]
    public partial RunState Stage { get; private set; }

    /// <summary>True from Start until the report is ready; closing the window must ask first.</summary>
    public bool IsRunning => Stage is RunState.Preflight or RunState.Running or RunState.Stopping;

    public bool IsReady => Stage == RunState.Ready;

    public bool IsFinished => Stage == RunState.Finished;

    public bool ShowsProgress => Stage is RunState.Running or RunState.Stopping or RunState.Finished;

    /// <summary>"What will change in shop on pg01".</summary>
    [ObservableProperty]
    public partial string ChangeTitle { get; private set; } = "";

    [ObservableProperty]
    public partial IReadOnlyList<string> ChangeSummary { get; private set; } = [];

    /// <summary>Tables will be dropped or emptied, so Start needs an explicit tick first.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    public partial bool RequiresAcknowledgement { get; private set; }

    [ObservableProperty]
    public partial string AcknowledgementText { get; private set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    public partial bool Acknowledged { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<PhaseViewModel> Phases { get; private set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<TableProgressRowViewModel> Tables { get; private set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<TableProgressRowViewModel> VisibleTables { get; private set; } = [];

    public IReadOnlyList<RunTableFilter> ShowFilters { get; } = Enum.GetValues<RunTableFilter>();

    [ObservableProperty]
    public partial RunTableFilter ShowFilter { get; set; }

    /// <summary>Keeps the table being worked on scrolled into view.</summary>
    [ObservableProperty]
    public partial bool FollowCurrent { get; set; } = true;

    /// <summary>The table being copied or counted; the view scrolls to it when following.</summary>
    [ObservableProperty]
    public partial TableProgressRowViewModel? CurrentRow { get; private set; }

    [ObservableProperty]
    public partial double OverallPercent { get; private set; }

    [ObservableProperty]
    public partial string OverallText { get; private set; } = "";

    [ObservableProperty]
    public partial long RowsCopied { get; private set; }

    [ObservableProperty]
    public partial double RowsPerSecond { get; private set; }

    [ObservableProperty]
    public partial TimeSpan Elapsed { get; private set; }

    [ObservableProperty]
    public partial string? EtaText { get; private set; }

    [ObservableProperty]
    public partial string CurrentActivity { get; private set; } = "";

    [ObservableProperty]
    public partial string? CurrentActivityDetail { get; private set; }

    [ObservableProperty]
    public partial string CountersText { get; private set; } = "";

    [ObservableProperty]
    public partial int FailedCount { get; private set; }

    [ObservableProperty]
    public partial int StepsDone { get; private set; }

    [ObservableProperty]
    public partial int StepsTotal { get; private set; }

    internal RunTracker? Tracker => _tracker;

    public override Task OnEnteringAsync(CancellationToken cancellationToken)
    {
        if (IsRunning)
        {
            return Task.CompletedTask;
        }

        // A plan reviewed since the last visit (or since the last run) starts over at Ready.
        if (_session.Prepared is { } prepared && !ReferenceEquals(prepared, _prepared))
        {
            ShowReady(prepared);
        }

        return Task.CompletedTask;
    }

    /// <summary>Back to Ready for a fresh plan, e.g. after "Run again" or "New migration".</summary>
    public void Reset()
    {
        if (IsRunning)
        {
            throw new InvalidOperationException("A run is in progress.");
        }

        _prepared = null;
        _tracker = null;
        _hub = null;
        _estimator = null;
        Tables = [];
        VisibleTables = [];
        Phases = [];
        CurrentRow = null;
        Acknowledged = false;
        Stage = RunState.Ready;
        RaiseCanAdvanceChanged();
    }

    /// <summary>
    /// The window is closing during a run: asks first, then stops the run and waits a while for the
    /// rollback. Returns false when the user chose to keep running.
    /// </summary>
    public async Task<bool> StopForCloseAsync()
    {
        if (!IsRunning)
        {
            return true;
        }

        var bullets = _tracker is { } tracker ? StopConsequences.From(tracker) : [];
        var confirmed = await _services.Dialogs.ConfirmAsync(new ConfirmRequest(
            "Stop the migration and close?",
            "The migration is still running. Closing stops it first.",
            bullets,
            "Stop and close",
            "Keep running",
            IsDestructive: true));
        if (!confirmed && IsRunning)
        {
            return false;
        }

        RequestStop();
        if (_runFinished is { } finished)
        {
            await Task.WhenAny(finished.Task, Task.Delay(CloseWait, _services.Time));
        }

        return true;
    }

    private bool CanStart() =>
        Stage == RunState.Ready && _session.Prepared is not null && (!RequiresAcknowledgement || Acknowledged);

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        var reviewed = _session.Prepared ?? throw new InvalidOperationException("Review the plan first.");
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _runFinished = finished;
        _stopBeforeRun = false;
        try
        {
            if (await PreflightAsync(reviewed) is not { } current)
            {
                return;
            }

            // The same plan as reviewed, made from the target as it is now.
            _session.Prepared = current;
            _prepared = current;
            await RunCommand.ExecuteAsync(null);
        }
        finally
        {
            finished.TrySetResult();
        }
    }

    /// <returns>The plan to run, or null when the target changed, the check failed or the user closed the window meanwhile.</returns>
    private async Task<PreparedMigration?> PreflightAsync(PreparedMigration reviewed)
    {
        ClearError();
        Stage = RunState.Preflight;
        IsBusy = true;
        BusyText = "Checking the target has not changed since you reviewed it...";
        RaiseCanAdvanceChanged();
        PreflightResult result;
        try
        {
            result = await _session.PreflightAsync(reviewed, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ShowError(ex);
            return null;
        }
        finally
        {
            IsBusy = false;
            BusyText = null;
            if (_stopBeforeRun || Error is not null)
            {
                Stage = RunState.Ready;
            }

            RaiseCanAdvanceChanged();
        }

        if (_stopBeforeRun)
        {
            return null;
        }

        if (result.CanRun)
        {
            return result.Current;
        }

        Stage = RunState.Ready;
        RaiseCanAdvanceChanged();
        var bullets = result.Differences.Take(20).ToList();
        if (result.Differences.Count > bullets.Count)
        {
            bullets.Add($"... and {result.Differences.Count - bullets.Count} more.");
        }

        if (result.Current.Plan.HasErrors)
        {
            bullets.AddRange(result.Current.Plan.Issues.Where(i => i.Severity == Core.Diagnostics.IssueSeverity.Error).Take(5).Select(i => $"{i.Subject}: {i.Message}"));
        }

        var back = await _services.Dialogs.ConfirmAsync(new ConfirmRequest(
            "The target changed since you reviewed the plan",
            "Nothing has been changed. Review the plan again before running.",
            bullets,
            "Back to Review",
            "Stay here"));
        if (back)
        {
            await Navigator.ReturnToReviewAsync(result);
        }

        return null;
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var migration = _prepared ?? throw new InvalidOperationException("Start the migration first.");
        var time = _services.Time;
        var tracker = new RunTracker(migration, time);
        var hub = new RunProgressHub(time);
        _tracker = tracker;
        _hub = hub;
        _estimator = new ThroughputEstimator(time);
        _shownStatusVersion = -1;
        Tables = tracker.Rows;
        Phases = tracker.Phases;
        StepsTotal = tracker.StepsTotal;
        _session.ClearRun();
        _session.RunStartedAt = tracker.StartedAt;
        Stage = RunState.Running;
        Navigator.SetLocked(true);
        RaiseCanAdvanceChanged();
        _services.Log.Info($"Run started: {migration.Plan.Tables.Count} tables into {_session.Target?.Description}.");

        MigrationReport report;
        try
        {
            using (_services.Scheduler.StartTimer(TickInterval, Tick))
            {
                try
                {
                    report = await _session.ExecuteAsync(migration, hub, cancellationToken);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _services.Log.Error("The migration stopped unexpectedly.", ex);
                    DrainAll();
                    _session.RunFailure = ex;
                    report = tracker.ToFailureReport(ex);
                }
            }

            // The timer is gone: take whatever is left so no event is lost.
            DrainAll();
            tracker.ApplyReport(report);
            _session.Report = report;
            Refresh();
            _services.Log.Info(report.Succeeded ? "Run succeeded." : report.Cancelled ? "Run cancelled." : "Run finished with problems.");
            _session.Autosave = await AutosaveAsync();
            Stage = RunState.Finished;
        }
        finally
        {
            if (Stage != RunState.Finished)
            {
                Stage = RunState.Finished;
            }

            Navigator.SetLocked(false);
            RaiseCanAdvanceChanged();
        }

        if (_session.Report is not null)
        {
            await Navigator.GoToAsync(WizardStepKind.Report);
        }
    }

    private bool CanStop() => Stage == RunState.Running;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync()
    {
        if (_tracker is not { } tracker)
        {
            return;
        }

        var confirmed = await _services.Dialogs.ConfirmAsync(new ConfirmRequest(
            "Stop the migration?",
            "Stopping cancels the statement in progress. Here is what stays:",
            StopConsequences.From(tracker),
            "Stop migration",
            "Keep running",
            IsDestructive: true));

        // The run may have finished while the question was open.
        if (confirmed && Stage == RunState.Running)
        {
            RequestStop();
        }
    }

    partial void OnShowFilterChanged(RunTableFilter value)
    {
        _shownStatusVersion = -1;
        UpdateVisibleTables();
    }

    private void RequestStop()
    {
        if (Stage == RunState.Preflight)
        {
            _stopBeforeRun = true;
            return;
        }

        if (Stage != RunState.Running)
        {
            return;
        }

        Stage = RunState.Stopping;
        CurrentActivity = _tracker?.Copying is { } copying
            ? $"Stopping: rolling back {copying.Source}"
            : "Stopping: waiting for the current statement to be cancelled";
        CurrentActivityDetail = null;
        RunCancelCommand.Execute(null);
    }

    private void Tick()
    {
        if (_hub is not { } hub || _tracker is not { } tracker)
        {
            return;
        }

        var batch = hub.Drain(MaxEventsPerTick);
        Apply(tracker, batch);
        Refresh();
    }

    private void DrainAll()
    {
        if (_hub is not { } hub || _tracker is not { } tracker)
        {
            return;
        }

        ProgressBatch batch;
        do
        {
            batch = hub.Drain(MaxEventsPerTick);
            Apply(tracker, batch);
        }
        while (batch.More);
    }

    private static void Apply(RunTracker tracker, ProgressBatch batch)
    {
        foreach (var timed in batch.Discrete)
        {
            tracker.Apply(timed);
        }

        foreach (var progress in batch.Latest)
        {
            tracker.ApplyProgress(progress);
        }
    }

    /// <summary>Copies the tracker's state into the bound properties.</summary>
    private void Refresh()
    {
        if (_tracker is not { } tracker || _estimator is not { } estimator)
        {
            return;
        }

        tracker.Tick();
        estimator.Update(tracker);
        var now = _services.Time.GetUtcNow();
        Elapsed = now - tracker.StartedAt;
        RowsCopied = tracker.RowsCopied;
        RowsPerSecond = estimator.RowsPerSecond;
        OverallPercent = Math.Round(estimator.Fraction * 100, 1);
        EtaText = estimator.Remaining is { } remaining ? $"{Humanize.Remaining(remaining)} left in the copy (estimate)" : null;

        var estimate = tracker.Rows.Where(r => r.Status != TableRunStatus.Skipped).Sum(r => r.EstimatedRows ?? 0);
        var parts = new List<string>
        {
            $"{Humanize.Count(RowsCopied)} of ~{Humanize.Count(estimate)} rows ({OverallPercent:0}%)",
        };
        if (RowsPerSecond > 0 && tracker.Phase == MigrationPhase.CopyingData)
        {
            parts.Add($"{Humanize.Rate(RowsPerSecond)} rows/s");
        }

        parts.Add($"elapsed {Humanize.Duration(Elapsed)}");
        if (EtaText is not null)
        {
            parts.Add(EtaText);
        }

        OverallText = string.Join(" · ", parts);

        FailedCount = tracker.TablesFailed + tracker.StepsFailed;
        StepsDone = tracker.StepsDone;
        var finished = tracker.Rows.Count(r => r.Status is not (TableRunStatus.Waiting or TableRunStatus.Copying or TableRunStatus.Skipped));
        CountersText = $"Tables {finished:N0} of {tracker.DataTableCount:N0} · Failed {FailedCount:N0} · Steps {StepsDone:N0} of {StepsTotal:N0}";

        if (Stage != RunState.Stopping)
        {
            (CurrentActivity, CurrentActivityDetail) = Activity(tracker, now);
        }

        CurrentRow = tracker.Copying ?? tracker.Verifying;
        UpdateVisibleTables();
    }

    private static (string Activity, string? Detail) Activity(RunTracker tracker, DateTimeOffset now)
    {
        if (tracker.Copying is { } row)
        {
            var detail = row.EstimatedRows is not > 0
                ? $"{Humanize.Count(row.RowsCopied)} rows; no estimate"
                : row.RowsCopied > row.EstimatedRows
                    ? $"{Humanize.Count(row.RowsCopied)} rows; estimate was {Humanize.Count(row.EstimatedRows.Value)}"
                    : $"{row.RowsText} rows";
            return ($"Copying {row.Name}", detail);
        }

        if (tracker.CurrentStep is { } step && tracker.Phase == MigrationPhase.CreatingConstraints)
        {
            var elapsed = now - (tracker.CurrentStepStartedAt ?? now);
            return ($"Building: {step.Description}", $"{Humanize.Duration(elapsed)} · step {tracker.CurrentStepIndex:N0} of {tracker.StepsTotal:N0}");
        }

        if (tracker.Verifying is { } verifying && tracker.Phase == MigrationPhase.VerifyingRowCounts)
        {
            var elapsed = now - (tracker.VerifyStartedAt ?? now);
            return ($"Counting rows in {verifying.Source}", $"{Humanize.Duration(elapsed)} · table {tracker.VerifyIndex:N0} of {tracker.VerifyCount:N0}");
        }

        return tracker.Phase switch
        {
            null => ("Starting...", null),
            MigrationPhase.CreatingTables => ("Creating tables (one transaction)", null),
            MigrationPhase.CopyingData => ("Copying data", null),
            MigrationPhase.CreatingConstraints => ("Building keys and indexes", null),
            _ => ("Checking row counts", null),
        };
    }

    private void UpdateVisibleTables()
    {
        if (_tracker is not { } tracker || tracker.StatusVersion == _shownStatusVersion)
        {
            return;
        }

        _shownStatusVersion = tracker.StatusVersion;
        VisibleTables = ShowFilter switch
        {
            RunTableFilter.Problems => tracker.Rows.Where(r => r.IsProblem).ToList(),
            RunTableFilter.Remaining => tracker.Rows.Where(r => r.Status is TableRunStatus.Waiting or TableRunStatus.Copying).ToList(),
            _ => tracker.Rows,
        };
    }

    private void ShowReady(PreparedMigration prepared)
    {
        _prepared = prepared;
        _tracker = null;
        Tables = [];
        VisibleTables = [];
        Phases = [];
        CurrentRow = null;
        Stage = RunState.Ready;

        var plan = prepared.Plan;
        var options = prepared.Options;
        ChangeTitle = $"What will change in {_session.Target?.Description ?? "the target"}";
        var lines = new List<string>();
        var created = plan.Tables.Where(t => t.Action == TableAction.Create).ToList();
        if (created.Count > 0)
        {
            var schemas = plan.SchemasToCreate.Count > 0 ? $" (and {Humanize.Plural(plan.SchemasToCreate.Count, "schema")}: {string.Join(", ", plan.SchemasToCreate)})" : "";
            lines.Add($"Create {Humanize.Plural(created.Count, "table")}{schemas}.");
        }

        AddAction(lines, plan, TableAction.DropAndCreate, "Drop and recreate");
        AddAction(lines, plan, TableAction.Truncate, "Empty and reload");
        AddAction(lines, plan, TableAction.Append, "Append to");
        var skipped = plan.Tables.Count(t => t.Action == TableAction.Skip);
        if (skipped > 0)
        {
            lines.Add($"Skip {Humanize.Plural(skipped, "table")}.");
        }

        lines.Add("Then: " + string.Join(" · ",
            options.CopyData ? "copy data" : "no data (tables only)",
            options.CreateIndexes ? "indexes on" : "indexes off",
            options.CreateForeignKeys ? "foreign keys on" : "foreign keys off",
            options.VerifyRowCounts && options.CopyData ? "row counts checked" : "row counts not checked") + ".");
        ChangeSummary = lines;

        var dropped = plan.Tables.Count(t => t.Action == TableAction.DropAndCreate);
        var emptied = plan.Tables.Count(t => t.Action == TableAction.Truncate);
        RequiresAcknowledgement = dropped + emptied > 0;
        Acknowledged = false;
        var what = new List<string>();
        if (dropped > 0)
        {
            what.Add($"{Humanize.Plural(dropped, "table")} will be dropped");
        }

        if (emptied > 0)
        {
            what.Add($"{Humanize.Plural(emptied, "table")} emptied");
        }

        AcknowledgementText = RequiresAcknowledgement
            ? $"I understand that {string.Join(" and ", what)} in {_session.Target?.Description ?? "the target"}."
            : "";
        RaiseCanAdvanceChanged();
    }

    private static void AddAction(List<string> lines, MigrationPlan plan, TableAction action, string verb)
    {
        var tables = plan.Tables.Where(t => t.Action == action).Select(t => t.Target.ToString()).ToList();
        if (tables.Count == 0)
        {
            return;
        }

        var names = string.Join(", ", tables.Take(5)) + (tables.Count > 5 ? $", and {tables.Count - 5} more" : "");
        lines.Add($"{verb} {tables.Count:N0}: {names}.");
    }

    private async Task<ReportAutosave> AutosaveAsync()
    {
        try
        {
            var document = ReportDocument.From(_session, _services.Version);
            var baseName = ReportExporter.BaseName(document, _services.Time);
            var reports = _services.Reports;
            var path = await BackgroundWork.RunAsync(() => reports.Save(baseName, ReportExporter.ToMarkdown(document), ReportExporter.ToJson(document)), CancellationToken.None);
            return new ReportAutosave(path, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _services.Log.Error("Saving the report failed.", ex);
            return new ReportAutosave(null, ErrorText.From(ex).Message);
        }
    }
}
