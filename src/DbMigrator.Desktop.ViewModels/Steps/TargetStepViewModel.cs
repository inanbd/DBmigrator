using CommunityToolkit.Mvvm.ComponentModel;
using DbMigrator.Core.Planning;
using DbMigrator.Core.Providers;

namespace DbMigrator.Desktop.ViewModels;

/// <summary>What was found in the target, shown once connected.</summary>
public sealed record TargetSummaryViewModel(
    string ServerVersion,
    string Database,
    string User,
    int ExistingTableCount,
    IReadOnlyList<string> ExistingSchemas,
    IReadOnlyList<IssueRowViewModel> Warnings)
{
    public string ExistingText => ExistingTableCount == 0
        ? "The database has no tables yet."
        : $"{Humanize.Plural(ExistingTableCount, "existing table")} in schemas {string.Join(", ", ExistingSchemas)}";

    public bool HasWarnings => Warnings.Count > 0;
}

public sealed partial class TargetStepViewModel(ShellServices services, MigrationSession session)
    : ConnectionStepViewModel(services, session, ConnectionRole.Target)
{
    public override WizardStepKind Kind => WizardStepKind.Target;

    public override string Title => "Target";

    public override string Subtitle => "The database to copy into, or no database at all to get a SQL script.";

    protected override string IdleText => "Only looks at what exists. Nothing is changed until you start the run.";

    protected override string ConnectingText => "Connecting and looking at the target...";

    /// <summary>No target connection: plan against an empty database and save the script.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MigrateIntoDatabase))]
    public partial bool ScriptOnly { get; set; }

    public bool MigrateIntoDatabase
    {
        get => !ScriptOnly;
        set => ScriptOnly = !value;
    }

    public string ScriptOnlyText => $"Assumes an empty {SelectedProvider?.DisplayName ?? "target"} database. You can save the script on the Review step; nothing will be run.";

    [ObservableProperty]
    public partial TargetSummaryViewModel? Summary { get; private set; }

    /// <summary>How many source tables already exist in the target under the current naming; null until known.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MatchingText))]
    public partial int? MatchingTableCount { get; private set; }

    public string? MatchingText => MatchingTableCount is > 0 and var count
        ? $"{Humanize.Plural(count, "of them has", "of them have")} the same name as a source table; you will decide what to do with each on the Review step."
        : null;

    public override bool CanAdvance => ScriptOnly ? !IsBusy && SelectedProvider is not null : base.CanAdvance;

    public override string? BlockingReason => CanAdvance ? null : ScriptOnly ? null : "Connect to the target to continue.";

    public override async Task<bool> OnLeavingAsync(CancellationToken cancellationToken)
    {
        if (ScriptOnly && SelectedProvider is { } provider)
        {
            await Session.UseOfflineTargetAsync(provider.Provider);
        }

        return true;
    }

    partial void OnScriptOnlyChanged(bool value) => RaiseCanAdvanceChanged();

    protected override Task ConnectCoreAsync(ResolvedConnection connection, CancellationToken cancellationToken) =>
        Session.ConnectTargetAsync(connection, cancellationToken);

    protected override void OnConnected()
    {
        var info = Session.TargetInfo!;
        var check = Session.TargetCheck!;
        Summary = new TargetSummaryViewModel(
            check.ServerVersion,
            check.Database,
            check.User,
            info.State.ExistingTables.Count,
            info.State.ExistingTables.Select(t => t.Schema).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
            check.Findings.Select(i => new IssueRowViewModel(i)).ToList());
        _ = CountMatchingTablesAsync();
    }

    private async Task CountMatchingTablesAsync()
    {
        MatchingTableCount = null;
        if (Session.Schema is null || Session.TargetInfo is null || Session.TargetInfo.State.ExistingTables.Count == 0)
        {
            MatchingTableCount = 0;
            return;
        }

        try
        {
            var options = new MigrationOptions { Naming = Session.Naming };
            var plan = await Session.PlanAsync(options, CancellationToken.None);
            MatchingTableCount = plan.Plan.Tables.Count(t => t.TargetExists);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            Services.Log.Error("Counting matching tables failed.", ex);
        }
    }
}
