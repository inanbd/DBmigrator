using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbMigrator.Core.Diagnostics;
using DbMigrator.Core.Execution;
using DbMigrator.Core.Planning;

namespace DbMigrator.Desktop.ViewModels;

/// <summary>
/// The plan before anything runs: decisions for tables that already exist, every table's
/// mapping, type changes, issues and the SQL. Changing a decision re-plans from the cached
/// schema; neither database is read again.
/// </summary>
public sealed partial class ReviewStepViewModel : WizardStepViewModel
{
    public const int DecisionsTab = 0;
    public const int TablesTab = 1;
    public const int TypesTab = 2;
    public const int IssuesTab = 3;
    public const int ScriptTab = 4;

    private readonly ShellServices _services;
    private readonly MigrationSession _session;
    private readonly Debouncer _replan;
    private (int Source, int Target, int Selection, int Options) _plannedFrom = (-1, -1, -1, -1);
    private bool _applyingChoices;
    private int _replansPending;

    public ReviewStepViewModel(ShellServices services, MigrationSession session)
    {
        _services = services;
        _session = session;
        _replan = new Debouncer(services.Time, TimeSpan.FromMilliseconds(200));
        Issues = new IssueListViewModel(services);
        Script = new ScriptViewModel(services);
    }

    public override WizardStepKind Kind => WizardStepKind.Review;

    public override string Title => "Review";

    public override string Subtitle => "Check the plan. Nothing has been changed yet.";

    public override string NextLabel => _session.ScriptOnly ? "Save script..." : "Continue to run";

    public override bool NextActsInPlace => _session.ScriptOnly;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlan), nameof(HasErrors), nameof(HeaderText), nameof(SchemasText), nameof(ErrorBannerText))]
    public partial PreparedMigration? Prepared { get; private set; }

    public bool HasPlan => Prepared is not null;

    public bool HasErrors => Prepared?.Plan.HasErrors == true;

    [ObservableProperty]
    public partial bool IsPlanning { get; private set; }

    public ObservableCollection<ConflictRowViewModel> Conflicts { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DecisionsHeader))]
    public partial int UndecidedCount { get; private set; }

    public bool HasConflicts => Conflicts.Count > 0;

    public string DecisionsHeader => Conflicts.Count == 0 ? "Decisions (none needed)" : UndecidedCount > 0 ? $"Decisions needed ({UndecidedCount})" : $"Decisions ({Conflicts.Count})";

    public IReadOnlyList<ConflictChoice> BulkChoices { get; } = ConflictChoice.All.Skip(1).ToList();

    [ObservableProperty]
    public partial ConflictChoice BulkChoice { get; set; } = ConflictChoice.All[1];

    [ObservableProperty]
    public partial IReadOnlyList<PlanTableRowViewModel> PlanTables { get; private set; } = [];

    [ObservableProperty]
    public partial PlanTableRowViewModel? SelectedPlanTable { get; set; }

    [ObservableProperty]
    public partial TableDetailViewModel? Detail { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<TypeMappingRowViewModel> TypeMappings { get; private set; } = [];

    [ObservableProperty]
    public partial TypeMappingRowViewModel? SelectedTypeMapping { get; set; }

    public IssueListViewModel Issues { get; }

    public ScriptViewModel Script { get; }

    [ObservableProperty]
    public partial int SelectedTab { get; set; }

    /// <summary>Set when the target changed between review and start; the user sees why they are back here.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreflightDifferences))]
    public partial IReadOnlyList<string> PreflightDifferences { get; private set; } = [];

    public bool HasPreflightDifferences => PreflightDifferences.Count > 0;

    public string HeaderText
    {
        get
        {
            if (Prepared is not { } prepared)
            {
                return "";
            }

            var tables = prepared.Plan.Tables;
            var rows = tables.Sum(t => t.Source.EstimatedRowCount ?? 0);
            var target = _session.ScriptOnly ? "a SQL script (no target connection)" : $"{_session.Target?.Description} ({prepared.Target.ServerVersion})";
            return $"{Humanize.Plural(tables.Count, "table")} · ~{Humanize.Count(rows)} rows → {target}";
        }
    }

    public string SchemasText => Prepared?.Plan.SchemasToCreate is { Count: > 0 } schemas ? $"Schemas to create: {string.Join(", ", schemas)}" : "";

    public string? ErrorBannerText => Prepared?.Plan is { HasErrors: true } plan
        ? $"Resolve {Humanize.Plural(plan.Issues.Count(i => i.Severity == IssueSeverity.Error), "problem")} before running: "
          + string.Join(" ", plan.Issues.Where(i => i.Severity == IssueSeverity.Error).Take(3).Select(i => i.Message))
        : null;

    public override bool CanAdvance => Prepared is { Plan.HasErrors: false } && !IsPlanning && !IsBusy;

    public override string? BlockingReason =>
        IsPlanning ? "Updating the plan..."
        : Prepared is null ? "The plan is not ready."
        : UndecidedCount > 0 ? $"{Humanize.Plural(UndecidedCount, "table already exists", "tables already exist")} in the target: choose what to do with {(UndecidedCount == 1 ? "it" : "them")}."
        : HasErrors ? Prepared.Plan.Issues.First(i => i.Severity == IssueSeverity.Error).Message
        : null;

    public override async Task OnEnteringAsync(CancellationToken cancellationToken)
    {
        var versions = (_session.SourceVersion, _session.TargetVersion, _session.SelectionVersion, _session.OptionsVersion);
        OnPropertyChanged(nameof(NextLabel));
        if (Prepared is null || versions != _plannedFrom || _session.Prepared is null)
        {
            _plannedFrom = versions;
            await PlanNowAsync(cancellationToken);
        }
    }

    public override async Task<bool> OnLeavingAsync(CancellationToken cancellationToken)
    {
        if (Prepared is null)
        {
            return false;
        }

        _session.Prepared = Prepared;
        if (_session.ScriptOnly)
        {
            try
            {
                await Script.SaveCommand.ExecuteAsync(null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                ShowError(ex);
            }

            return false;
        }

        PreflightDifferences = [];
        return true;
    }

    /// <summary>Shown when the pre-flight check found the target changed: adopt the fresh plan and explain.</summary>
    public void ShowPreflightResult(PreflightResult result)
    {
        PreflightDifferences = result.Differences;

        // The pre-flight plan is made from the target as it is now: no need to plan again on entering.
        _plannedFrom = (_session.SourceVersion, _session.TargetVersion, _session.SelectionVersion, _session.OptionsVersion);
        Apply(result.Current);
        SelectedTab = UndecidedCount > 0 ? DecisionsTab : TablesTab;
    }

    /// <summary>Forgets decisions so the next visit plans afresh and asks again, e.g. for "Run again".</summary>
    public void ResetDecisions()
    {
        _session.ClearConflictChoices();
        foreach (var row in Conflicts)
        {
            row.PropertyChanged -= OnConflictRowChanged;
        }

        Conflicts.Clear();
        Prepared = null;
        _plannedFrom = (-1, -1, -1, -1);
        PreflightDifferences = [];
    }

    [RelayCommand]
    private void ApplyToUndecided()
    {
        _applyingChoices = true;
        try
        {
            foreach (var row in Conflicts.Where(r => r.IsUndecided))
            {
                row.Choice = BulkChoice;
                _session.SetConflictChoice(row.Source, BulkChoice.Action);
            }
        }
        finally
        {
            _applyingChoices = false;
        }

        _ = ReplanAsync();
    }

    /// <summary>Jumps to the issues tab with a severity selected, e.g. from a header chip.</summary>
    [RelayCommand]
    private void ShowIssues(IssueSeverity severity)
    {
        Issues.Focus(severity);
        SelectedTab = IssuesTab;
    }

    partial void OnSelectedPlanTableChanged(PlanTableRowViewModel? value) =>
        Detail = value is null || Prepared is null ? null : new TableDetailViewModel(value.Plan, Prepared.Target.Dialect, Prepared.Plan.Issues);

    private async Task PlanNowAsync(CancellationToken cancellationToken)
    {
        _replan.Cancel();
        _replansPending++;
        IsPlanning = true;
        RaiseCanAdvanceChanged();
        try
        {
            await GuardAsync(async ct => Apply(await _session.PlanAsync(_session.BuildOptions(), ct)), "Planning...", cancellationToken);
        }
        finally
        {
            IsPlanning = --_replansPending > 0;
            RaiseCanAdvanceChanged();
        }
    }

    private void OnConflictChanged(ConflictRowViewModel row)
    {
        if (_applyingChoices)
        {
            return;
        }

        _session.SetConflictChoice(row.Source, row.Choice.Action);
        UndecidedCount = Conflicts.Count(r => r.IsUndecided);
        _ = ReplanAsync();
    }

    private async Task ReplanAsync()
    {
        UndecidedCount = Conflicts.Count(r => r.IsUndecided);

        // A superseded re-plan ends early; planning goes on until the newest one is applied.
        _replansPending++;
        IsPlanning = true;
        RaiseCanAdvanceChanged();
        try
        {
            var options = _session.BuildOptions();
            await _replan.RunAsync(ct => _session.PlanAsync(options, ct), Apply);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            ShowError(ex);
        }
        finally
        {
            IsPlanning = --_replansPending > 0;
            RaiseCanAdvanceChanged();
        }
    }

    private void Apply(PreparedMigration prepared)
    {
        Prepared = prepared;
        _session.Prepared = prepared;
        var plan = prepared.Plan;

        // Decided conflicts stay listed so a choice can still be changed.
        var existing = plan.Tables.Where(t => t.TargetExists).ToList();
        var rows = Conflicts.ToDictionary(r => r.Source);
        if (existing.Count != Conflicts.Count || existing.Any(t => !rows.ContainsKey(t.Source.Name)))
        {
            foreach (var row in Conflicts)
            {
                row.PropertyChanged -= OnConflictRowChanged;
            }

            Conflicts.Clear();
            foreach (var table in existing)
            {
                var row = new ConflictRowViewModel(table, OnConflictChanged);
                _applyingChoices = true;
                row.Choice = ConflictChoice.For(_session.ConflictChoices.TryGetValue(table.Source.Name, out var chosen) ? chosen : null);
                _applyingChoices = false;
                row.PropertyChanged += OnConflictRowChanged;
                Conflicts.Add(row);
            }
        }
        else
        {
            foreach (var table in existing)
            {
                var row = rows[table.Source.Name];
                row.Update(table);

                // Choices live in the session, which forgets them when the source or target changes.
                _applyingChoices = true;
                row.Choice = ConflictChoice.For(_session.ConflictChoices.TryGetValue(table.Source.Name, out var chosen) ? chosen : null);
                _applyingChoices = false;
            }
        }

        UndecidedCount = Conflicts.Count(r => r.IsUndecided);
        OnPropertyChanged(nameof(HasConflicts));
        OnPropertyChanged(nameof(DecisionsHeader));

        var tableNames = plan.Tables.Select(t => t.Source.Name.ToString()).ToHashSet(StringComparer.Ordinal);
        var issuesByTable = plan.Issues.ToLookup(i => TableOf(i.Subject, tableNames));
        var keepTable = SelectedPlanTable?.Source;
        PlanTables = plan.Tables.Select((t, i) =>
        {
            var issues = issuesByTable[t.Source.Name.ToString()].ToList();
            return new PlanTableRowViewModel(i + 1, t,
                issues.Count(x => x.Severity == IssueSeverity.Error),
                issues.Count(x => x.Severity == IssueSeverity.Warning),
                issues.Count(x => x.Severity == IssueSeverity.Info));
        }).ToList();
        SelectedPlanTable = PlanTables.FirstOrDefault(r => r.Source == keepTable) ?? (PlanTables.Count > 0 ? PlanTables[0] : null);
        TypeMappings = TypeMappingRowViewModel.From(plan);
        Issues.SetIssues(plan.Issues);
        Script.SetScript(prepared.Script, plan, ct => MigrationSession.BuildScriptTextAsync(prepared, ct), "migration.sql");
        if (UndecidedCount > 0)
        {
            SelectedTab = DecisionsTab;
        }

        OnPropertyChanged(nameof(HeaderText));
        OnPropertyChanged(nameof(ErrorBannerText));
        RaiseCanAdvanceChanged();
    }

    private void OnConflictRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConflictRowViewModel.IsUndecided))
        {
            OnPropertyChanged(nameof(DecisionsHeader));
        }
    }

    /// <summary>The table an issue is about ("dbo.Orders.Total" → "dbo.Orders"), found by trimming segments.</summary>
    private static string TableOf(string subject, HashSet<string> tables)
    {
        for (var candidate = subject; ;)
        {
            if (tables.Contains(candidate))
            {
                return candidate;
            }

            var dot = candidate.LastIndexOf('.');
            if (dot <= 0)
            {
                return subject;
            }

            candidate = candidate[..dot];
        }
    }
}
