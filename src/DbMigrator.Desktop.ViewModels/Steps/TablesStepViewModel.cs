using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbMigrator.Core.Planning;
using DbMigrator.Core.Providers;
using DbMigrator.Core.Schema;

namespace DbMigrator.Desktop.ViewModels;

public sealed record ShowOption(string Label, Func<TableSelectionRowViewModel, bool> Includes);

/// <summary>Which tables to copy, how to name them, and what to create.</summary>
public sealed partial class TablesStepViewModel : WizardStepViewModel
{
    public const string AllSchemas = "All schemas";

    private readonly ShellServices _services;
    private readonly MigrationSession _session;
    private readonly Debouncer _preview;
    private readonly Debouncer _references;
    private Dictionary<QualifiedName, TableSelectionRowViewModel> _byName = [];
    private int _builtSourceVersion = -1;
    private int _builtTargetVersion = -1;
    private bool _settingsLoaded;
    private bool _bulkChange;

    public TablesStepViewModel(ShellServices services, MigrationSession session)
    {
        _services = services;
        _session = session;
        _preview = new Debouncer(services.Time, TimeSpan.FromMilliseconds(300));
        _references = new Debouncer(services.Time, TimeSpan.FromMilliseconds(100));
        ShowOptions =
        [
            new("All", _ => true),
            new("Selected", r => r.IsSelected),
            new("Not selected", r => !r.IsSelected),
            new("Already in target", r => r.ExistsInTarget),
        ];
        ShowFilter = ShowOptions[0];
        NamingChoices = BuildNamingChoices([]);
    }

    public override WizardStepKind Kind => WizardStepKind.Tables;

    public override string Title => "Tables & options";

    public override string Subtitle => "Choose what to copy and how it is named in the target.";

    public IReadOnlyList<TableSelectionRowViewModel> Rows { get; private set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<TableSelectionRowViewModel> VisibleRows { get; private set; } = [];

    [ObservableProperty]
    public partial string FilterText { get; set; } = "";

    [ObservableProperty]
    public partial IReadOnlyList<string> Schemas { get; private set; } = [AllSchemas];

    [ObservableProperty]
    public partial string SchemaFilter { get; set; } = AllSchemas;

    public IReadOnlyList<ShowOption> ShowOptions { get; }

    [ObservableProperty]
    public partial ShowOption ShowFilter { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SummaryText))]
    public partial int SelectedCount { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SummaryText))]
    public partial long SelectedEstimatedRows { get; private set; }

    public string SummaryText => Rows.Count == 0
        ? "Connect to the source first."
        : $"Selected {SelectedCount:N0} of {Rows.Count:N0} tables · ~{Humanize.Count(SelectedEstimatedRows)} rows (catalog estimates){CatalogReadText}";

    private string CatalogReadText => _session.SchemaReadAt is { } read ? $" · Catalog read {read.ToLocalTime():HH:mm}" : "";

    /// <summary>Tables left out that selected tables' foreign keys point to.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMissingReferences), nameof(MissingReferencesText))]
    public partial IReadOnlyList<QualifiedName> MissingReferences { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MissingReferencesText))]
    public partial int MissingForeignKeyCount { get; private set; }

    public bool HasMissingReferences => MissingReferences.Count > 0;

    public string MissingReferencesText =>
        $"{Humanize.Plural(MissingForeignKeyCount, "foreign key")} point to {Humanize.Plural(MissingReferences.Count, "table")} you left out "
        + $"({string.Join(", ", MissingReferences.Take(3))}{(MissingReferences.Count > 3 ? ", ..." : "")}). Those foreign keys will not be created.";

    [ObservableProperty]
    public partial IReadOnlyList<NamingChoiceViewModel> NamingChoices { get; private set; }

    [ObservableProperty]
    public partial IdentifierCase Case { get; set; } = IdentifierCase.Lower;

    public ObservableCollection<SchemaMappingRowViewModel> SchemaMappings { get; } = [];

    [ObservableProperty]
    public partial bool CopyData { get; set; } = true;

    [ObservableProperty]
    public partial bool CreateIndexes { get; set; } = true;

    [ObservableProperty]
    public partial bool CreateForeignKeys { get; set; } = true;

    [ObservableProperty]
    public partial bool VerifyRowCounts { get; set; } = true;

    [ObservableProperty]
    public partial bool IsPreviewing { get; private set; }

    /// <summary>A note after re-reading the catalog, e.g. "3 new tables (not selected)".</summary>
    [ObservableProperty]
    public partial string? RereadNotice { get; private set; }

    public override bool CanAdvance => !IsBusy && SelectedCount > 0 && SchemaMappings.All(m => !m.HasError);

    public override string? BlockingReason =>
        IsBusy ? null
        : SelectedCount == 0 ? "Select at least one table."
        : SchemaMappings.FirstOrDefault(m => m.HasError) is { } bad ? $"Fix the target schema for {bad.SourceSchema}."
        : null;

    public override async Task OnEnteringAsync(CancellationToken cancellationToken)
    {
        if (!_settingsLoaded)
        {
            _settingsLoaded = true;
            var store = _services.Settings;
            try
            {
                var settings = await BackgroundWork.RunAsync(store.Load, cancellationToken);
                Case = settings.Naming;
                CopyData = settings.CopyData;
                CreateIndexes = settings.CreateIndexes;
                CreateForeignKeys = settings.CreateForeignKeys;
                VerifyRowCounts = settings.VerifyRowCounts;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                _services.Log.Error("Loading settings failed.", ex);
            }
        }

        var preview = false;
        if (_builtSourceVersion != _session.SourceVersion)
        {
            // New rows know nothing of the target yet.
            Rebuild(keepSelection: _builtSourceVersion >= 0);
            _builtSourceVersion = _session.SourceVersion;
            preview = true;
        }

        if (_builtTargetVersion != _session.TargetVersion)
        {
            _builtTargetVersion = _session.TargetVersion;
            RefreshDefaults();
            preview = true;
        }

        if (preview)
        {
            _ = PreviewAsync();
        }
    }

    public override async Task<bool> OnLeavingAsync(CancellationToken cancellationToken)
    {
        _session.SetSelection(Rows.Where(r => r.IsSelected).Select(r => r.Name));
        _session.SetOptions(BuildNaming(), CopyData, CreateIndexes, CreateForeignKeys, VerifyRowCounts);
        var settings = new AppSettings
        {
            Naming = Case,
            CopyData = CopyData,
            CreateIndexes = CreateIndexes,
            CreateForeignKeys = CreateForeignKeys,
            VerifyRowCounts = VerifyRowCounts,
        };
        var store = _services.Settings;
        try
        {
            await BackgroundWork.RunAsync(() =>
            {
                var current = store.Load();
                store.Save(settings with { LastSourceProfileId = current.LastSourceProfileId, LastTargetProfileId = current.LastTargetProfileId });
            }, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _services.Log.Error("Saving settings failed.", ex);
        }

        return true;
    }

    /// <summary>The naming options as the planner takes them: only schemas the user changed are mapped.</summary>
    public NamingOptions BuildNaming() => new()
    {
        Case = Case,
        SchemaMap = SchemaMappings.Where(m => m.IsCustom).ToDictionary(m => m.SourceSchema, m => m.TargetSchema.Trim(), StringComparer.OrdinalIgnoreCase),
    };

    [RelayCommand]
    private void SelectShown() => SetSelected(VisibleRows, true);

    [RelayCommand]
    private void ClearShown() => SetSelected(VisibleRows, false);

    /// <summary>Selects every table that selected tables reference, and the ones those reference, and so on.</summary>
    [RelayCommand]
    private void SelectReferenced()
    {
        var selected = Rows.Where(r => r.IsSelected).Select(r => r.Name).ToHashSet();
        var queue = new Queue<QualifiedName>(selected);
        var add = new List<TableSelectionRowViewModel>();
        while (queue.Count > 0)
        {
            var table = _byName[queue.Dequeue()].Definition;
            foreach (var referenced in table.ForeignKeys.Select(fk => fk.ReferencedTable))
            {
                if (_byName.TryGetValue(referenced, out var row) && selected.Add(referenced))
                {
                    add.Add(row);
                    queue.Enqueue(referenced);
                }
            }
        }

        SetSelected(add, true);
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task RereadCatalogAsync(CancellationToken cancellationToken)
    {
        var before = Rows.Select(r => r.Name).ToHashSet();
        if (!await GuardAsync(ct => _session.RereadSourceAsync(ct), "Reading the catalog again...", cancellationToken))
        {
            return;
        }

        Rebuild(keepSelection: true, newTablesSelected: false);
        _builtSourceVersion = _session.SourceVersion;
        var after = Rows.Select(r => r.Name).ToHashSet();
        var added = after.Count(t => !before.Contains(t));
        var removed = before.Count(t => !after.Contains(t));
        RereadNotice = added == 0 && removed == 0
            ? "No tables were added or removed."
            : $"{Humanize.Plural(added, "new table")} (not selected), {Humanize.Plural(removed, "table")} no longer there.";
        _ = PreviewAsync();
    }

    /// <summary>Selects every table again, as on first arrival, for a new migration.</summary>
    public void ResetSelection()
    {
        Rebuild(keepSelection: false);
        _builtSourceVersion = _session.SourceVersion;
        RereadNotice = null;
    }

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    partial void OnSchemaFilterChanged(string value) => ApplyFilter();

    partial void OnShowFilterChanged(ShowOption value) => ApplyFilter();

    partial void OnCaseChanged(IdentifierCase value)
    {
        foreach (var choice in NamingChoices)
        {
            choice.IsSelected = choice.Case == value;
        }

        RefreshDefaults();
        _ = PreviewAsync();
    }

    /// <summary>Rebuilds rows from the session's schema, keeping the selection by name.</summary>
    private void Rebuild(bool keepSelection, bool newTablesSelected = true)
    {
        var previous = _byName;
        var schema = _session.Schema;
        var rows = (schema?.Tables ?? []).Select(t => new TableSelectionRowViewModel(t)).ToList();
        foreach (var row in rows)
        {
            row.IsSelected = keepSelection && previous.TryGetValue(row.Name, out var old) ? old.IsSelected : !keepSelection || newTablesSelected;
            row.PropertyChanged += OnRowChanged;
        }

        foreach (var old in previous.Values)
        {
            old.PropertyChanged -= OnRowChanged;
        }

        Rows = rows;
        _byName = rows.ToDictionary(r => r.Name);
        OnPropertyChanged(nameof(Rows));
        Schemas = [AllSchemas, .. rows.Select(r => r.Schema).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        if (!Schemas.Contains(SchemaFilter))
        {
            SchemaFilter = AllSchemas;
        }

        var mappings = SchemaMappings.ToDictionary(m => m.SourceSchema, StringComparer.Ordinal);
        SchemaMappings.Clear();
        foreach (var group in rows.GroupBy(r => r.Schema).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var row = new SchemaMappingRowViewModel(group.Key, group.Count(), DefaultTargetFor(group.Key));
            if (mappings.TryGetValue(group.Key, out var kept) && kept.IsCustom)
            {
                row.TargetSchema = kept.TargetSchema;
            }

            row.PropertyChanged += OnMappingChanged;
            SchemaMappings.Add(row);
        }

        NamingChoices = BuildNamingChoices(rows);
        RecountSelection();

        // The table count and catalog time in the summary change even when the selection does not.
        OnPropertyChanged(nameof(SummaryText));
        ApplyFilter();
        ValidateMappings();
        _ = RecomputeReferencesAsync();
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TableSelectionRowViewModel.IsSelected) && !_bulkChange)
        {
            RecountSelection();
            _ = RecomputeReferencesAsync();
        }
    }

    private void OnMappingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SchemaMappingRowViewModel.TargetSchema))
        {
            ValidateMappings();
            _ = PreviewAsync();
        }
    }

    private void SetSelected(IEnumerable<TableSelectionRowViewModel> rows, bool selected)
    {
        _bulkChange = true;
        try
        {
            foreach (var row in rows.ToList())
            {
                row.IsSelected = selected;
            }
        }
        finally
        {
            _bulkChange = false;
        }

        RecountSelection();
        _ = RecomputeReferencesAsync();
        if (ShowFilter != ShowOptions[0])
        {
            ApplyFilter();
        }
    }

    private void RecountSelection()
    {
        var selected = Rows.Where(r => r.IsSelected).ToList();
        SelectedCount = selected.Count;
        SelectedEstimatedRows = selected.Sum(r => r.EstimatedRows ?? 0);
        RaiseCanAdvanceChanged();
    }

    private async Task RecomputeReferencesAsync()
    {
        var rows = Rows;
        await _references.RunAsync(
            _ => Task.FromResult(MissingReferencesOf(rows)),
            result =>
            {
                MissingReferences = result.Tables;
                MissingForeignKeyCount = result.ForeignKeys;
            });
    }

    private static (IReadOnlyList<QualifiedName> Tables, int ForeignKeys) MissingReferencesOf(IReadOnlyList<TableSelectionRowViewModel> rows)
    {
        var byName = rows.ToDictionary(r => r.Name);
        var missing = new SortedSet<QualifiedName>(QualifiedNameComparer.Ordinal);
        var count = 0;
        foreach (var row in rows.Where(r => r.IsSelected))
        {
            foreach (var fk in row.Definition.ForeignKeys)
            {
                if (byName.TryGetValue(fk.ReferencedTable, out var referenced) && !referenced.IsSelected)
                {
                    missing.Add(fk.ReferencedTable);
                    count++;
                }
            }
        }

        return (missing.ToList(), count);
    }

    private void ApplyFilter()
    {
        var text = FilterText.Trim();
        var pattern = text.Contains('*', StringComparison.Ordinal) || text.Contains('?', StringComparison.Ordinal)
            ? new TableFilter([text], [])
            : null;
        VisibleRows = Rows.Where(r =>
                (SchemaFilter == AllSchemas || r.Schema == SchemaFilter)
                && ShowFilter.Includes(r)
                && (text.Length == 0 || (pattern?.Matches(r.Name) ?? r.FullName.Contains(text, StringComparison.OrdinalIgnoreCase))))
            .ToList();
    }

    /// <summary>Fills the target name and "exists" columns from a plan of every table with the current naming.</summary>
    private async Task PreviewAsync()
    {
        if (_session.Schema is null || _session.TargetInfo is null || Rows.Count == 0 || SchemaMappings.Any(m => m.HasError))
        {
            return;
        }

        var options = new MigrationOptions { Naming = BuildNaming(), IfTableExists = TableExistsAction.Skip };
        IsPreviewing = true;
        try
        {
            await _preview.RunAsync(
                ct => _session.PlanAsync(options, ct),
                prepared =>
                {
                    foreach (var table in prepared.Plan.Tables)
                    {
                        if (_byName.TryGetValue(table.Source.Name, out var row))
                        {
                            row.TargetName = table.Target.ToString();
                            row.ExistsInTarget = table.TargetExists;
                        }
                    }
                });
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            _services.Log.Error("Previewing target names failed.", ex);
        }
        finally
        {
            IsPreviewing = false;
        }
    }

    private void RefreshDefaults()
    {
        foreach (var mapping in SchemaMappings)
        {
            var wasDefault = !mapping.IsCustom;
            mapping.DefaultTarget = DefaultTargetFor(mapping.SourceSchema);
            if (wasDefault)
            {
                mapping.TargetSchema = mapping.DefaultTarget;
            }
        }

        ValidateMappings();
    }

    /// <summary>Where the planner puts a source schema when the user maps nothing.</summary>
    private string DefaultTargetFor(string sourceSchema)
    {
        var dialect = _session.TargetInfo?.Dialect;
        if (_session.Schema is { } schema && dialect is not null && string.Equals(sourceSchema, schema.DefaultSchema, StringComparison.OrdinalIgnoreCase))
        {
            return dialect.DefaultSchema;
        }

        var cased = IdentifierNaming.ApplyCase(sourceSchema, Case);
        return dialect is null ? cased : IdentifierNaming.Shorten(cased, dialect.IdentifierLimit);
    }

    private void ValidateMappings()
    {
        var limit = _session.TargetInfo?.Dialect.IdentifierLimit;
        foreach (var mapping in SchemaMappings)
        {
            var value = mapping.TargetSchema.Trim();
            mapping.Error = value.Length == 0 ? "Enter a schema name."
                : limit is not null && !limit.Fits(value) ? $"Longer than the target's limit of {limit.MaxLength} {(limit.CountsUtf8Bytes ? "bytes" : "characters")}."
                : null;
        }

        RaiseCanAdvanceChanged();
    }

    private NamingChoiceViewModel[] BuildNamingChoices(IReadOnlyList<TableSelectionRowViewModel> rows)
    {
        // Prefer the user's own names whose spelling differs between the options.
        var names = rows.Where(r => r.IsSelected).Take(200)
            .SelectMany(r => r.Definition.Columns.Select(c => c.Name).Prepend(r.Table))
            .Distinct(StringComparer.Ordinal)
            .Where(n => n.Any(char.IsUpper) && !string.Equals(IdentifierNaming.ToSnakeCase(n), n, StringComparison.OrdinalIgnoreCase))
            .Take(3)
            .ToList();
        if (names.Count == 0)
        {
            names = ["OrderDetails", "CustomerID"];
        }

        string Examples(IdentifierCase c) => string.Join(", ", names.Select(n => $"{n} → {IdentifierNaming.ApplyCase(n, c)}"));
        var choices = new[]
        {
            new NamingChoiceViewModel(IdentifierCase.Lower, "lower case (recommended)", Examples(IdentifierCase.Lower), c => Case = c),
            new NamingChoiceViewModel(IdentifierCase.SnakeCase, "snake_case", Examples(IdentifierCase.SnakeCase), c => Case = c),
            new NamingChoiceViewModel(IdentifierCase.Preserve, "Keep as is (must be quoted in every query)", Examples(IdentifierCase.Preserve), c => Case = c),
        };
        foreach (var choice in choices)
        {
            choice.IsSelected = choice.Case == Case;
        }

        return choices;
    }
}
