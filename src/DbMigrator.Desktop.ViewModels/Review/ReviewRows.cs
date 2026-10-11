using System.Text;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using DbMigrator.Core.Diagnostics;
using DbMigrator.Core.Planning;
using DbMigrator.Core.Providers;
using DbMigrator.Core.Schema;

namespace DbMigrator.Desktop.ViewModels;

/// <summary>A choice for a table that exists in the target. A null action means not decided yet.</summary>
public sealed record ConflictChoice(TableExistsAction? Action, string Label)
{
    public static IReadOnlyList<ConflictChoice> All { get; } =
    [
        new(null, "Choose..."),
        new(TableExistsAction.DropAndRecreate, "Drop and recreate"),
        new(TableExistsAction.Truncate, "Truncate and reload"),
        new(TableExistsAction.Append, "Append"),
        new(TableExistsAction.Skip, "Skip"),
    ];

    public static ConflictChoice For(TableExistsAction? action) => All.First(c => c.Action == action);
}

/// <summary>A table that already exists in the target, and what the user chose for it.</summary>
public sealed partial class ConflictRowViewModel(TablePlan table, Action<ConflictRowViewModel> changed) : ObservableObject
{
    public TablePlan Table { get; private set; } = table;

    public QualifiedName Source => Table.Source.Name;

    public QualifiedName Target => Table.Target;

    public string RowsText => Table.Source.EstimatedRowCount is { } rows ? "~" + Humanize.Count(rows) : "";

    public IReadOnlyList<ConflictChoice> Options { get; } = ConflictChoice.All;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Explanation), nameof(IsUndecided))]
    public partial ConflictChoice Choice { get; set; } = ConflictChoice.All[0];

    public bool IsUndecided => Choice.Action is null;

    public string Explanation => Choice.Action switch
    {
        TableExistsAction.DropAndRecreate =>
            $"Drops {Target} at the start of the run, with any views and foreign keys in the target that depend on it, and creates it afresh.",
        TableExistsAction.Truncate =>
            $"Empties {Target} at the start of the run (before any table is copied), then loads it. Assumes it has the shape this tool would create, e.g. from an earlier run.",
        TableExistsAction.Append => "Keeps its rows and adds the source rows; assumes the same shape."
            + (Table.Source.PrimaryKey is not null ? " If any key already exists there, the whole table fails and is rolled back." : ""),
        TableExistsAction.Skip => "Leaves it alone; copies nothing into it.",
        _ => "Undecided: nothing runs until you choose.",
    };

    /// <summary>Refreshes the plan behind the row after a re-plan, keeping the choice.</summary>
    public void Update(TablePlan table)
    {
        Table = table;
        OnPropertyChanged(nameof(Target));
        OnPropertyChanged(nameof(Explanation));
    }

    partial void OnChoiceChanged(ConflictChoice value) => changed(this);
}

/// <summary>A table in the plan, in load order.</summary>
public sealed record PlanTableRowViewModel(int Index, TablePlan Plan, int Errors, int Warnings, int Notes)
{
    public QualifiedName Source => Plan.Source.Name;

    public QualifiedName Target => Plan.Target;

    public string ActionText => Plan.IsUnresolvedConflict ? "exists: choose" : Core.Execution.ReportWriter.DescribeAction(Plan.Action);

    public string RowsText => Plan.Source.EstimatedRowCount is { } rows ? "~" + Humanize.Count(rows) : "";

    public int ColumnCount => Plan.Columns.Count;

    public string IssuesText => Errors + Warnings + Notes == 0 ? "" : $"{Errors} E · {Warnings} W · {Notes} N";
}

/// <summary>One column's journey from source to target.</summary>
public sealed record ColumnMappingRowViewModel(
    string SourceColumn,
    string SourceType,
    string TargetColumn,
    string TargetType,
    bool IsNullable,
    string Identity,
    string Default,
    string IssueText)
{
    public string NullText => IsNullable ? "null" : "not null";

    public bool HasIssue => IssueText.Length > 0;
}

/// <summary>Everything about one planned table: columns, keys, indexes, foreign keys and its DDL.</summary>
public sealed class TableDetailViewModel
{
    public TableDetailViewModel(TablePlan table, ISqlDialect dialect, IReadOnlyList<MigrationIssue> issues)
    {
        Table = table;
        var byColumn = issues.Where(i => i.Subject.StartsWith(table.Source.Name + ".", StringComparison.Ordinal))
            .GroupBy(i => i.Subject[(table.Source.Name.ToString().Length + 1)..], StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => string.Join(" ", g.Select(i => i.Message)), StringComparer.Ordinal);
        Columns = table.Columns.Select(c => new ColumnMappingRowViewModel(
            c.Source.Name,
            c.Source.NativeType,
            c.Name,
            c.SqlType,
            c.IsNullable,
            c.Identity is { } id ? $"start {id.Seed}, step {id.Increment}" : "",
            DescribeDefault(c),
            byColumn.GetValueOrDefault(c.Source.Name) ?? "")).ToList();
        PrimaryKeyText = table.PrimaryKey is { } pk ? $"{pk.Name} ({string.Join(", ", pk.Columns)})" : "None created by this run";
        Indexes = table.Indexes.Select(i => $"{i.Name}{(i.IsUnique ? " (unique)" : "")}: {string.Join(", ", i.Columns.Select(c => c.Name + (c.IsDescending ? " desc" : "")))}"
            + (i.IncludedColumns.Count > 0 ? $" include {string.Join(", ", i.IncludedColumns)}" : "")).ToList();
        ForeignKeys = table.ForeignKeys.Select(f => $"{f.Name}: ({string.Join(", ", f.Columns)}) → {f.ReferencedTable} ({string.Join(", ", f.ReferencedColumns)})"
            + (f.OnDelete != ReferentialAction.NoAction ? $", on delete {f.OnDelete}" : "")
            + (f.IsTrusted ? "" : ", not validated")).ToList();
        TableIssues = issues.Where(i => i.Subject == table.Source.Name.ToString() || (i.Subject.StartsWith(table.Source.Name + ".", StringComparison.Ordinal) && !byColumn.ContainsKey(i.Subject[(table.Source.Name.ToString().Length + 1)..])))
            .Select(i => new IssueRowViewModel(i)).ToList();
        ExistingNote = table.TargetExists && !table.CreatesTable
            ? "Existing table: its columns are not changed; rows load assuming this shape."
            : null;
        Ddl = BuildDdl(table, dialect);
    }

    public TablePlan Table { get; }

    public string Title => $"{Table.Source.Name} → {Table.Target}";

    public IReadOnlyList<ColumnMappingRowViewModel> Columns { get; }

    public string PrimaryKeyText { get; }

    public IReadOnlyList<string> Indexes { get; }

    public IReadOnlyList<string> ForeignKeys { get; }

    public IReadOnlyList<IssueRowViewModel> TableIssues { get; }

    public string? ExistingNote { get; }

    public bool HasExistingNote => ExistingNote is not null;

    public string Ddl { get; }

    private static string DescribeDefault(ColumnPlan column) => (column.Source.Default, column.DefaultSql) switch
    {
        (null, _) => "",
        (_, null) => "(not migrated)",
        (_, { } sql) => sql,
    };

    private static string BuildDdl(TablePlan table, ISqlDialect dialect)
    {
        var ddl = new StringBuilder();
        if (table.CreatesTable)
        {
            ddl.Append(dialect.CreateTable(table)).AppendLine(";");
        }

        if (table.PrimaryKey is not null)
        {
            ddl.AppendLine().Append(dialect.AddPrimaryKey(table)).AppendLine(";");
        }

        foreach (var index in table.Indexes)
        {
            ddl.AppendLine().Append(dialect.CreateIndex(table, index)).AppendLine(";");
        }

        foreach (var fk in table.ForeignKeys)
        {
            ddl.AppendLine().Append(dialect.AddForeignKey(table, fk)).AppendLine(";");
        }

        var text = ddl.Length == 0 ? "-- Nothing is created for this table; its existing definition is kept." : ddl.ToString();
        return text.Length > ScriptViewModel.MaxPreviewChars ? text[..ScriptViewModel.MaxPreviewChars] + "\n-- (truncated)" : text;
    }
}

/// <summary>A distinct source → target type pair, e.g. nvarchar(n) → varchar(n), and the columns that have it.</summary>
public sealed partial record TypeMappingRowViewModel(string SourcePattern, string TargetPattern, IReadOnlyList<string> Columns, IssueSeverity? WorstSeverity, string Note)
{
    public int ColumnCount => Columns.Count;

    public string SeverityText => WorstSeverity switch
    {
        IssueSeverity.Error => "Error",
        IssueSeverity.Warning => "Warning",
        IssueSeverity.Info => "Note",
        _ => "",
    };

    public bool IsWarning => WorstSeverity == IssueSeverity.Warning;

    /// <summary>Groups every planned column by type pattern, with the worst issue on any of them.</summary>
    public static IReadOnlyList<TypeMappingRowViewModel> From(MigrationPlan plan)
    {
        var issuesBySubject = plan.Issues.GroupBy(i => i.Subject, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        return plan.Tables
            .SelectMany(t => t.Columns.Select(c => (Table: t, Column: c)))
            .GroupBy(x => (Pattern(x.Column.Source.NativeType), Pattern(x.Column.SqlType)))
            .Select(g =>
            {
                var issues = g.SelectMany(x => issuesBySubject.GetValueOrDefault($"{x.Table.Source.Name}.{x.Column.Source.Name}") ?? []).ToList();
                var worst = issues.Count == 0 ? (IssueSeverity?)null : issues.Max(i => i.Severity);
                var note = issues.OrderByDescending(i => i.Severity).Select(i => i.Message).FirstOrDefault() ?? "";
                return new TypeMappingRowViewModel(g.Key.Item1, g.Key.Item2, g.Select(x => $"{x.Table.Source.Name}.{x.Column.Source.Name}").ToList(), worst, note);
            })
            .OrderByDescending(r => r.WorstSeverity ?? (IssueSeverity)(-1))
            .ThenBy(r => r.SourcePattern, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>nvarchar(50) → nvarchar(n); decimal(18,2) → decimal(p,s); max stays max.</summary>
    public static string Pattern(string type)
    {
        var withPrecision = PrecisionScale().Replace(type, "(p,s)");
        return Length().Replace(withPrecision, "(n)");
    }

    [GeneratedRegex(@"\(\d+,\s*\d+\)")]
    private static partial Regex PrecisionScale();

    [GeneratedRegex(@"\(\d+\)")]
    private static partial Regex Length();
}
