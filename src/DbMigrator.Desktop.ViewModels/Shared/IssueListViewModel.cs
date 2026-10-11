using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbMigrator.Core.Diagnostics;

namespace DbMigrator.Desktop.ViewModels;

/// <summary>All issues with one code, with what they mean and what to do.</summary>
public sealed record IssueGroupViewModel(string Code, IssueSeverity Severity, IReadOnlyList<IssueRowViewModel> Rows)
{
    public IssueHelp Help { get; } = IssueCatalog.For(Code);

    public string Title => Help.Title;

    public int Count => Rows.Count;

    public string SeverityText => Severity switch
    {
        IssueSeverity.Error => "Error",
        IssueSeverity.Warning => "Warning",
        _ => "Note",
    };

    public bool IsError => Severity == IssueSeverity.Error;

    public bool IsWarning => Severity == IssueSeverity.Warning;
}

/// <summary>Issues grouped by code and filterable by severity and text. Shared by Review and Report.</summary>
public sealed partial class IssueListViewModel : ObservableObject
{
    private readonly ShellServices _services;
    private IReadOnlyList<MigrationIssue> _issues = [];

    public IssueListViewModel(ShellServices services)
    {
        _services = services;
    }

    [ObservableProperty]
    public partial IReadOnlyList<IssueGroupViewModel> Groups { get; private set; } = [];

    [ObservableProperty]
    public partial IssueGroupViewModel? SelectedGroup { get; set; }

    [ObservableProperty]
    public partial string FilterText { get; set; } = "";

    [ObservableProperty]
    public partial bool ShowErrors { get; set; } = true;

    [ObservableProperty]
    public partial bool ShowWarnings { get; set; } = true;

    [ObservableProperty]
    public partial bool ShowNotes { get; set; } = true;

    [ObservableProperty]
    public partial int ErrorCount { get; private set; }

    [ObservableProperty]
    public partial int WarningCount { get; private set; }

    [ObservableProperty]
    public partial int NoteCount { get; private set; }

    public string ErrorsLabel => $"Errors ({ErrorCount:N0})";

    public string WarningsLabel => $"Warnings ({WarningCount:N0})";

    public string NotesLabel => $"Notes ({NoteCount:N0})";

    public bool IsEmpty => _issues.Count == 0;

    public void SetIssues(IReadOnlyList<MigrationIssue> issues)
    {
        _issues = issues;
        ErrorCount = issues.Count(i => i.Severity == IssueSeverity.Error);
        WarningCount = issues.Count(i => i.Severity == IssueSeverity.Warning);
        NoteCount = issues.Count(i => i.Severity == IssueSeverity.Info);
        OnPropertyChanged(nameof(ErrorsLabel));
        OnPropertyChanged(nameof(WarningsLabel));
        OnPropertyChanged(nameof(NotesLabel));
        OnPropertyChanged(nameof(IsEmpty));
        Regroup();
    }

    /// <summary>Selects the first group of a severity, e.g. when a header chip is clicked.</summary>
    public void Focus(IssueSeverity severity) => SelectedGroup = Groups.FirstOrDefault(g => g.Severity == severity) ?? SelectedGroup;

    partial void OnFilterTextChanged(string value) => Regroup();

    partial void OnShowErrorsChanged(bool value) => Regroup();

    partial void OnShowWarningsChanged(bool value) => Regroup();

    partial void OnShowNotesChanged(bool value) => Regroup();

    [RelayCommand]
    private async Task CopyAsync()
    {
        var text = new StringBuilder();
        foreach (var group in Groups)
        {
            foreach (var row in group.Rows)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"{row.Severity}\t{row.Code}\t{row.Subject}\t{row.Message}");
            }
        }

        await _services.Clipboard.SetTextAsync(text.ToString());
    }

    [RelayCommand]
    private async Task ExportCsvAsync()
    {
        var issues = _issues;
        await _services.Files.SaveAsync(
            new SaveFileRequest("Export issues", "migration-issues.csv", [new FileTypeChoice("CSV", ".csv")]),
            async (_, stream, ct) =>
            {
                await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                await writer.WriteLineAsync("Severity,Code,Subject,Message".AsMemory(), ct);
                foreach (var issue in issues)
                {
                    await writer.WriteLineAsync($"{issue.Severity},{Csv(issue.Code)},{Csv(issue.Subject)},{Csv(issue.Message)}".AsMemory(), ct);
                }
            },
            CancellationToken.None);
    }

    private static string Csv(string value) =>
        value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;

    private void Regroup()
    {
        var text = FilterText.Trim();
        var keep = SelectedGroup?.Code;
        Groups = _issues
            .Where(i => i.Severity switch
            {
                IssueSeverity.Error => ShowErrors,
                IssueSeverity.Warning => ShowWarnings,
                _ => ShowNotes,
            })
            .Where(i => text.Length == 0
                || i.Subject.Contains(text, StringComparison.OrdinalIgnoreCase)
                || i.Message.Contains(text, StringComparison.OrdinalIgnoreCase)
                || i.Code.Contains(text, StringComparison.OrdinalIgnoreCase))
            .GroupBy(i => (i.Severity, i.Code))
            .OrderByDescending(g => g.Key.Severity)
            .ThenBy(g => g.Key.Code, StringComparer.Ordinal)
            .Select(g => new IssueGroupViewModel(g.Key.Code, g.Key.Severity, g.Select(i => new IssueRowViewModel(i)).ToList()))
            .ToList();
        SelectedGroup = Groups.FirstOrDefault(g => g.Code == keep) ?? (Groups.Count > 0 ? Groups[0] : null);
    }
}
