using CommunityToolkit.Mvvm.ComponentModel;
using DbMigrator.Core.Planning;
using DbMigrator.Core.Schema;

namespace DbMigrator.Desktop.ViewModels;

/// <summary>A source table in the selection grid.</summary>
public sealed partial class TableSelectionRowViewModel : ObservableObject
{
    public TableSelectionRowViewModel(TableDefinition table)
    {
        Definition = table;
        var notes = new List<string>();
        var computed = table.Columns.Count(c => c.ComputedExpression is not null);
        if (computed > 0)
        {
            notes.Add($"computed columns: {computed}");
        }

        if (table.CheckConstraints.Count > 0)
        {
            notes.Add($"check constraints: {table.CheckConstraints.Count}");
        }

        if (table.Indexes.Any(i => i.Filter is not null))
        {
            notes.Add("filtered indexes");
        }

        Notes = string.Join("; ", notes);
        var keys = new List<string>();
        if (table.PrimaryKey is not null)
        {
            keys.Add("PK");
        }

        if (table.ForeignKeys.Count > 0)
        {
            keys.Add($"{table.ForeignKeys.Count} FK");
        }

        KeysText = string.Join(", ", keys);
    }

    public TableDefinition Definition { get; }

    public QualifiedName Name => Definition.Name;

    public string Schema => Definition.Name.Schema;

    public string Table => Definition.Name.Name;

    public string FullName => Definition.Name.ToString();

    public long? EstimatedRows => Definition.EstimatedRowCount;

    /// <summary>Sorts as a number; shown as "~1.2 M".</summary>
    public long SortRows => Definition.EstimatedRowCount ?? -1;

    public string RowsText => Definition.EstimatedRowCount is { } rows ? "~" + Humanize.Count(rows) : "";

    public int ColumnCount => Definition.Columns.Count;

    public string KeysText { get; }

    public string Notes { get; }

    public string SelectionLabel => $"Include {Definition.Name}";

    [ObservableProperty]
    public partial bool IsSelected { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InTargetText))]
    public partial bool ExistsInTarget { get; set; }

    public string InTargetText => ExistsInTarget ? "exists" : "";

    public string InTargetHint => $"{TargetName} exists; you will choose what to do with it on the Review step.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InTargetHint))]
    public partial string TargetName { get; set; } = "";
}

/// <summary>One naming option with examples from the user's own names.</summary>
public sealed partial class NamingChoiceViewModel(IdentifierCase identifierCase, string label, string examples, Action<IdentifierCase> select) : ObservableObject
{
    public IdentifierCase Case => identifierCase;

    public string Label => label;

    public string Examples => examples;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    partial void OnIsSelectedChanged(bool value)
    {
        if (value)
        {
            select(identifierCase);
        }
    }
}

/// <summary>Where one source schema's tables go.</summary>
public sealed partial class SchemaMappingRowViewModel(string sourceSchema, int tableCount, string defaultTarget) : ObservableObject
{
    public string SourceSchema => sourceSchema;

    public int TableCount => tableCount;

    /// <summary>Where the planner would put the tables by default with the current naming.</summary>
    public string DefaultTarget { get; set; } = defaultTarget;

    [ObservableProperty]
    public partial string TargetSchema { get; set; } = defaultTarget;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; set; }

    public bool HasError => Error is not null;

    public bool IsCustom => !string.Equals(TargetSchema.Trim(), DefaultTarget, StringComparison.Ordinal);
}
