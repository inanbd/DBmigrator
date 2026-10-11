using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DbMigrator.Core.Providers;

namespace DbMigrator.Desktop.ViewModels;

/// <summary>What was found in the source, shown once connected.</summary>
public sealed record SourceSummaryViewModel(
    string ServerVersion,
    string Database,
    string User,
    int TableCount,
    int SchemaCount,
    long EstimatedRows,
    IReadOnlyList<string> LargestTables,
    DateTimeOffset ReadAt,
    IReadOnlyList<IssueRowViewModel> Warnings)
{
    public string TablesText => $"{Humanize.Plural(TableCount, "table")} in {Humanize.Plural(SchemaCount, "schema")} visible to this login";

    public string RowsText => $"~{Humanize.Count(EstimatedRows)} rows (catalog estimates)";

    public string ReadAtText => $"Catalog read {ReadAt.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)}";

    public bool HasWarnings => Warnings.Count > 0;
}

public sealed partial class SourceStepViewModel(ShellServices services, MigrationSession session)
    : ConnectionStepViewModel(services, session, ConnectionRole.Source)
{
    public override WizardStepKind Kind => WizardStepKind.Source;

    public override string Title => "Source";

    public override string Subtitle => "The database to copy from. Nothing in it is changed.";

    public string Advice { get; } = "Copy from a database nobody is writing to, such as a restored backup or a snapshot. Rows written during the copy can make the final row counts differ.";

    public override string ConnectLabel => "Connect and read catalog";

    protected override string IdleText => "Reads table definitions only. Nothing is changed.";

    protected override string ConnectingText => "Connecting and reading the catalog...";

    [ObservableProperty]
    public partial SourceSummaryViewModel? Summary { get; private set; }

    protected override Task ConnectCoreAsync(ResolvedConnection connection, CancellationToken cancellationToken) =>
        Session.ConnectSourceAsync(connection, cancellationToken);

    protected override void OnConnected()
    {
        var schema = Session.Schema!;
        var check = Session.SourceCheck!;
        var largest = schema.Tables
            .Where(t => t.EstimatedRowCount is > 0)
            .OrderByDescending(t => t.EstimatedRowCount)
            .Take(5)
            .Select(t => $"{t.Name}  ~{Humanize.Count(t.EstimatedRowCount!.Value)} rows")
            .ToList();
        Summary = new SourceSummaryViewModel(
            check.ServerVersion,
            check.Database,
            check.User,
            schema.Tables.Count,
            schema.Tables.Select(t => t.Name.Schema).Distinct(StringComparer.Ordinal).Count(),
            schema.Tables.Sum(t => t.EstimatedRowCount ?? 0),
            largest,
            Session.SchemaReadAt ?? DateTimeOffset.UtcNow,
            check.Findings.Concat(schema.Issues).Select(i => new IssueRowViewModel(i)).ToList());
    }
}
