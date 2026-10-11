using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbMigrator.Core.Planning;

namespace DbMigrator.Desktop.ViewModels;

/// <summary>A line in the script list: a phase header or one statement.</summary>
public sealed record ScriptStepRowViewModel(bool IsHeader, string Text, string Sql)
{
    public bool IsStep => !IsHeader;
}

/// <summary>
/// A script shown as a virtualized list of statements with a capped preview of the selected one.
/// The full text is only built on the background, for Copy and Save, so the UI never holds megabytes.
/// </summary>
public sealed partial class ScriptViewModel : ObservableObject
{
    /// <summary>The most text a preview box holds; larger text makes the UI stall.</summary>
    public const int MaxPreviewChars = 64 * 1024;

    private const int MaxLineChars = 200;

    private readonly ShellServices _services;
    private Func<CancellationToken, Task<string>>? _fullText;
    private string _suggestedName = "migration.sql";

    public ScriptViewModel(ShellServices services)
    {
        _services = services;
    }

    [ObservableProperty]
    public partial IReadOnlyList<ScriptStepRowViewModel> Rows { get; private set; } = [];

    [ObservableProperty]
    public partial ScriptStepRowViewModel? SelectedRow { get; set; }

    [ObservableProperty]
    public partial string PreviewText { get; private set; } = "";

    [ObservableProperty]
    public partial bool IsPreviewTruncated { get; private set; }

    /// <summary>A note after saving or copying, such as where the file went.</summary>
    [ObservableProperty]
    public partial string? Notice { get; private set; }

    public bool IsEmpty => Rows.Count == 0;

    /// <summary>Shows a migration script: phase headers, then each statement.</summary>
    public void SetScript(MigrationScript script, MigrationPlan plan, Func<CancellationToken, Task<string>> fullText, string suggestedName)
    {
        var rows = new List<ScriptStepRowViewModel> { Header("Before copying data (one transaction)") };
        rows.AddRange(script.BeforeData.Select(Step));
        rows.Add(Header($"Data copy: {Humanize.Plural(plan.Tables.Count(t => t.ReceivesData), "table")}"));
        rows.Add(Header("After copying data (each statement on its own)"));
        rows.AddRange(script.AfterData.Select(Step));
        Show(rows, fullText, suggestedName);
    }

    /// <summary>Shows sections of statements, e.g. "Failed" and "Not run" in a report.</summary>
    public void SetSections(IReadOnlyList<(string Heading, IReadOnlyList<ScriptStep> Steps)> sections, string suggestedName)
    {
        var rows = new List<ScriptStepRowViewModel>();
        foreach (var (heading, steps) in sections.Where(s => s.Steps.Count > 0))
        {
            rows.Add(Header(heading));
            rows.AddRange(steps.Select(Step));
        }

        var text = string.Join(Environment.NewLine, sections.Where(s => s.Steps.Count > 0).Select(s =>
            $"-- {s.Heading}{Environment.NewLine}" + string.Join(Environment.NewLine, s.Steps.Select(st => $"-- {st.Description}{Environment.NewLine}{st.Sql};{Environment.NewLine}"))));
        Show(rows, _ => Task.FromResult(text), suggestedName);
    }

    public void Clear() => Show([], null, _suggestedName);

    partial void OnSelectedRowChanged(ScriptStepRowViewModel? value)
    {
        var sql = value?.Sql ?? "";
        IsPreviewTruncated = sql.Length > MaxPreviewChars;
        if (IsPreviewTruncated)
        {
            var cut = sql.LastIndexOf('\n', MaxPreviewChars);
            sql = sql[..(cut > 0 ? cut : MaxPreviewChars)];
        }

        PreviewText = sql;
    }

    [RelayCommand]
    private async Task CopyAllAsync()
    {
        if (_fullText is null)
        {
            return;
        }

        var builder = _fullText;
        var text = await BackgroundWork.RunAsync(ct => builder(ct), CancellationToken.None);
        await _services.Clipboard.SetTextAsync(text);
        Notice = $"Copied {Humanize.Plural(Rows.Count(r => r.IsStep), "statement")} to the clipboard.";
    }

    [RelayCommand]
    private async Task CopyStepAsync()
    {
        if (SelectedRow is { IsStep: true } row)
        {
            await _services.Clipboard.SetTextAsync(row.Sql);
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (_fullText is null)
        {
            return;
        }

        var builder = _fullText;
        var saved = await _services.Files.SaveAsync(
            new SaveFileRequest("Save SQL script", _suggestedName, [new FileTypeChoice("SQL script", ".sql")]),
            async (_, stream, ct) =>
            {
                var text = await BackgroundWork.RunAsync(c => builder(c), ct);
                await using var writer = new StreamWriter(stream);
                await writer.WriteAsync(text.AsMemory(), ct);
            },
            CancellationToken.None);
        if (saved is not null)
        {
            Notice = $"Saved to {saved.Path ?? saved.Name}.";
        }
    }

    private void Show(IReadOnlyList<ScriptStepRowViewModel> rows, Func<CancellationToken, Task<string>>? fullText, string suggestedName)
    {
        _fullText = fullText;
        _suggestedName = suggestedName;
        Rows = rows;
        Notice = null;
        SelectedRow = rows.FirstOrDefault(r => r.IsStep);
        OnPropertyChanged(nameof(IsEmpty));
    }

    private static ScriptStepRowViewModel Header(string text) => new(true, text, "");

    private static ScriptStepRowViewModel Step(ScriptStep step)
    {
        var firstLine = step.Sql.Split('\n', 2)[0];
        if (firstLine.Length > MaxLineChars)
        {
            firstLine = firstLine[..MaxLineChars] + "…";
        }

        return new ScriptStepRowViewModel(false, $"{step.Description}: {firstLine}", step.Sql);
    }
}
