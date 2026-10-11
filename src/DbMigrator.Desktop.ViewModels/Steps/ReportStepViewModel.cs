using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbMigrator.Core.Execution;

namespace DbMigrator.Desktop.ViewModels;

/// <summary>A headline number on the report, e.g. "Rows copied" and "176,204,331".</summary>
public sealed record KpiViewModel(string Label, string Value);

/// <summary>One table's outcome in the report.</summary>
public sealed record ReportTableRowViewModel(int Index, TableResult Result, TableRunStatus Status)
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    public string StatusText => TableRunStatuses.Describe(Status);

    public bool IsProblem => TableRunStatuses.IsProblem(Status);

    public string Source => Result.Table.Source.Name.ToString();

    public string Target => Result.Table.Target.ToString();

    public string Action => ReportWriter.DescribeAction(Result.Table.Action);

    public string RowsCopied => Result.RowsCopied.ToString("N0", Culture);

    public string SourceCount => Result.SourceRowCount?.ToString("N0", Culture) ?? "";

    public string TargetCount => Result.TargetRowCount?.ToString("N0", Culture) ?? "";

    public string Elapsed => Result.Elapsed > TimeSpan.Zero ? Humanize.Duration(Result.Elapsed) : "";

    public string Rate => Result.Elapsed.TotalSeconds >= 1 ? $"{Humanize.Rate(Result.RowsCopied / Result.Elapsed.TotalSeconds)}/s" : "";

    public string Message => Result.Error ?? "";
}

/// <summary>What happened, what needs attention, and the SQL still to run.</summary>
public sealed partial class ReportStepViewModel : WizardStepViewModel
{
    public const int TablesTab = 0;
    public const int IssuesTab = 1;
    public const int SqlTab = 2;

    private readonly ShellServices _services;
    private readonly MigrationSession _session;
    private MigrationReport? _shown;

    public ReportStepViewModel(ShellServices services, MigrationSession session)
    {
        _services = services;
        _session = session;
        Issues = new IssueListViewModel(services);
        RemainingSql = new ScriptViewModel(services);
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IsBusy))
            {
                RunAgainCommand.NotifyCanExecuteChanged();
                NewMigrationCommand.NotifyCanExecuteChanged();
            }
        };
    }

    public override WizardStepKind Kind => WizardStepKind.Report;

    public override string Title => "Report";

    public override string Subtitle => "What happened, and what is left to do.";

    public override bool IsApplicable => !_session.ScriptOnly;

    /// <summary>The report has its own buttons: run again or start a new migration.</summary>
    public override bool ShowsFooter => false;

    public override bool CanAdvance => false;

    public override string? BlockingReason => null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSuccess), nameof(IsCancelled), nameof(IsFailure))]
    public partial ReportOutcome Outcome { get; private set; }

    public bool IsSuccess => Outcome == ReportOutcome.Succeeded;

    public bool IsCancelled => Outcome == ReportOutcome.Cancelled;

    public bool IsFailure => Outcome is ReportOutcome.CompletedWithProblems or ReportOutcome.Stopped or ReportOutcome.Crashed;

    [ObservableProperty]
    public partial string Headline { get; private set; } = "";

    [ObservableProperty]
    public partial string SubHeadline { get; private set; } = "";

    [ObservableProperty]
    public partial IReadOnlyList<KpiViewModel> Kpis { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAttention))]
    public partial IReadOnlyList<AttentionGroupViewModel> Attention { get; private set; } = [];

    public bool HasAttention => Attention.Count > 0;

    [ObservableProperty]
    public partial IReadOnlyList<ReportTableRowViewModel> Tables { get; private set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<ReportTableRowViewModel> VisibleTables { get; private set; } = [];

    [ObservableProperty]
    public partial bool ProblemsOnly { get; set; }

    public IssueListViewModel Issues { get; }

    public ScriptViewModel RemainingSql { get; }

    [ObservableProperty]
    public partial bool HasRemainingSql { get; private set; }

    [ObservableProperty]
    public partial int SelectedTab { get; set; }

    /// <summary>"Saved automatically to ...", or why that failed.</summary>
    [ObservableProperty]
    public partial string AutosaveText { get; private set; } = "";

    [ObservableProperty]
    public partial bool AutosaveFailed { get; private set; }

    /// <summary>A note after saving or copying.</summary>
    [ObservableProperty]
    public partial string? Notice { get; private set; }

    public override Task OnEnteringAsync(CancellationToken cancellationToken)
    {
        if (_session.Report is { } report && !ReferenceEquals(report, _shown))
        {
            Show(report);
        }

        return Task.CompletedTask;
    }

    /// <summary>Forgets the shown report, for "Run again" and "New migration".</summary>
    public void Reset()
    {
        _shown = null;
        Headline = "";
        SubHeadline = "";
        Kpis = [];
        Attention = [];
        Tables = [];
        VisibleTables = [];
        Issues.SetIssues([]);
        RemainingSql.Clear();
        HasRemainingSql = false;
        AutosaveText = "";
        AutosaveFailed = false;
        Notice = null;
        SelectedTab = TablesTab;
    }

    partial void OnProblemsOnlyChanged(bool value) => VisibleTables = value ? Tables.Where(t => t.IsProblem).ToList() : Tables;

    [RelayCommand]
    private async Task SaveReportAsync()
    {
        if (_shown is null)
        {
            return;
        }

        try
        {
            var document = ReportDocument.From(_session, _services.Version);
            var saved = await _services.Files.SaveAsync(
                new SaveFileRequest("Save report", ReportExporter.BaseName(document, _services.Time) + ".md",
                    [new FileTypeChoice("Markdown", ".md"), new FileTypeChoice("JSON", ".json")]),
                async (type, stream, ct) =>
                {
                    var text = await BackgroundWork.RunAsync(
                        () => type.Extension == ".json" ? ReportExporter.ToJson(document) : ReportExporter.ToMarkdown(document), ct);
                    await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                    await writer.WriteAsync(text.AsMemory(), ct);
                },
                CancellationToken.None);
            if (saved is not null)
            {
                Notice = $"Saved to {saved.Path ?? saved.Name}.";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError(ex);
        }
    }

    [RelayCommand]
    private async Task CopySummaryAsync()
    {
        if (_shown is not { } report)
        {
            return;
        }

        var header = $"{Headline}{Environment.NewLine}{SubHeadline}{Environment.NewLine}"
            + $"Source: {_session.Source?.Description}{Environment.NewLine}Target: {_session.Target?.Description}{Environment.NewLine}";
        var text = await BackgroundWork.RunAsync(() =>
        {
            using var writer = new StringWriter(CultureInfo.InvariantCulture);
            writer.WriteLine(header);
            ReportWriter.WriteReport(writer, report);
            return writer.ToString();
        }, CancellationToken.None);
        await _services.Clipboard.SetTextAsync(text);
        Notice = "Copied the summary to the clipboard.";
    }

    [RelayCommand]
    private async Task SaveRemainingSqlAsync()
    {
        try
        {
            await RemainingSql.SaveCommand.ExecuteAsync(null);
            Notice = RemainingSql.Notice;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError(ex);
        }
    }

    /// <summary>Shows a failed or not-run step's SQL in the "SQL still to run" tab.</summary>
    [RelayCommand]
    private void ShowSql(AttentionItemViewModel? item)
    {
        if (item?.Step is not { } step)
        {
            return;
        }

        SelectedTab = SqlTab;
        RemainingSql.SelectedRow = RemainingSql.Rows.FirstOrDefault(r => r.IsStep && r.Sql == step.Sql) ?? RemainingSql.SelectedRow;
    }

    private bool CanRestart() => !IsBusy;

    /// <summary>The same tables and options again: the target is inspected anew and existing tables are asked about again.</summary>
    [RelayCommand(CanExecute = nameof(CanRestart))]
    private async Task RunAgainAsync()
    {
        if (await GuardAsync(ct => _session.ReinspectTargetAsync(ct), "Inspecting the target again...", CancellationToken.None))
        {
            await Navigator.RestartAsync(RestartMode.RunAgain);
        }
    }

    /// <summary>Keeps both connections and starts again from choosing tables.</summary>
    [RelayCommand(CanExecute = nameof(CanRestart))]
    private async Task NewMigrationAsync()
    {
        if (await GuardAsync(ct => _session.ReinspectTargetAsync(ct), "Inspecting the target again...", CancellationToken.None))
        {
            await Navigator.RestartAsync(RestartMode.NewMigration);
        }
    }

    private void Show(MigrationReport report)
    {
        _shown = report;
        var options = _session.Prepared?.Options ?? _session.BuildOptions();
        Outcome = ReportSummary.Outcome(report, _session.RunFailure);
        Headline = ReportSummary.Headline(report, Outcome);
        SubHeadline = Outcome == ReportOutcome.Crashed
            ? $"{_session.RunFailure?.Message} Details are in the log: {_services.Log.LogFolder}"
            : ReportSummary.SubHeadline(report, Outcome, options);

        var dataTables = report.Plan.Tables.Count(t => t.ReceivesData && options.CopyData);
        var copied = report.Tables.Count(t => t.Status is TableStatus.Copied or TableStatus.CountMismatch);
        var stepsTotal = report.Steps.Count + report.StepsNotRun.Count;
        var seconds = report.Elapsed.TotalSeconds;
        Kpis =
        [
            new("Tables copied", $"{copied:N0} of {dataTables:N0}"),
            new("Rows copied", report.TotalRowsCopied.ToString("N0", CultureInfo.InvariantCulture)),
            new("Duration", ReportWriter.FormatElapsed(report.Elapsed)),
            new("Average rate", seconds >= 1 ? $"{Humanize.Rate(report.TotalRowsCopied / seconds)} rows/s" : "-"),
            new("Keys and indexes built", $"{report.Steps.Count(s => s.Succeeded):N0} of {stepsTotal:N0}"),
        ];

        Attention = ReportAttention.Build(report, options);
        Tables = report.Tables.Count == 0 && report.FatalError is not null
            ? report.Plan.Tables.Select((t, i) => new ReportTableRowViewModel(
                i + 1,
                new TableResult { Table = t, Status = TableStatus.NotCopied, Error = "Not started: the run stopped first." },
                t.ReceivesData && options.CopyData ? TableRunStatus.NotStarted : TableRunStatus.Skipped)).ToList()
            : report.Tables.Select((t, i) => new ReportTableRowViewModel(i + 1, t, TableRunStatuses.From(t, report.InterruptedTable))).ToList();
        VisibleTables = ProblemsOnly ? Tables.Where(t => t.IsProblem).ToList() : Tables;
        Issues.SetIssues(report.Issues);

        if (_session.Prepared?.Script is { } script)
        {
            var sections = ReportAttention.RemainingSql(report, script);
            RemainingSql.SetSections(sections, "still-to-run.sql");
            HasRemainingSql = sections.Any(s => s.Steps.Count > 0);
        }
        else
        {
            RemainingSql.Clear();
            HasRemainingSql = false;
        }

        (AutosaveText, AutosaveFailed) = _session.Autosave switch
        {
            { Path: { } path } => ($"Saved automatically to {path}", false),
            { Error: { } error } => ($"The report could not be saved automatically: {error} Use Save report.", true),
            _ => ("", false),
        };
        Notice = null;
        SelectedTab = TablesTab;
    }
}
