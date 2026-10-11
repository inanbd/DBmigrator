using DbMigrator.Core.Execution;
using DbMigrator.Core.Planning;

namespace DbMigrator.Desktop.ViewModels;

public enum ReportOutcome
{
    Succeeded,
    CompletedWithProblems,
    Cancelled,

    /// <summary>The engine stopped the run, e.g. because creating the tables failed.</summary>
    Stopped,

    /// <summary>The run failed unexpectedly; the report was rebuilt from what the screen had seen.</summary>
    Crashed,
}

/// <summary>The outcome of a run in a word and a sentence, shared by the Report step and its exports.</summary>
public static class ReportSummary
{
    public static ReportOutcome Outcome(MigrationReport report, Exception? failure) =>
        failure is not null ? ReportOutcome.Crashed
        : report.Cancelled ? ReportOutcome.Cancelled
        : report.FatalError is not null ? ReportOutcome.Stopped
        : report.Succeeded ? ReportOutcome.Succeeded
        : ReportOutcome.CompletedWithProblems;

    public static string Headline(MigrationReport report, ReportOutcome outcome) => outcome switch
    {
        ReportOutcome.Succeeded => "Migration succeeded",
        ReportOutcome.CompletedWithProblems => "Finished with problems",
        ReportOutcome.Cancelled => $"Cancelled while {ReportWriter.DescribePhase(report.CancelledDuring!.Value)} after {ReportWriter.FormatElapsed(report.Elapsed)}",
        ReportOutcome.Stopped => "The migration stopped",
        _ => "The migration stopped unexpectedly",
    };

    public static string SubHeadline(MigrationReport report, ReportOutcome outcome, MigrationOptions options)
    {
        var copiedTables = report.Tables.Count(t => t.Status is TableStatus.Copied or TableStatus.CountMismatch);
        var copied = $"Copied {report.TotalRowsCopied:N0} rows into {Humanize.Plural(copiedTables, "table")} in {ReportWriter.FormatElapsed(report.Elapsed)}";
        var stepsOk = report.Steps.Count(s => s.Succeeded);
        var stepsTotal = report.Steps.Count + report.StepsNotRun.Count;
        switch (outcome)
        {
            case ReportOutcome.Succeeded:
                var parts = new List<string>
                {
                    options.CopyData ? copied : $"Created {Humanize.Plural(report.Tables.Count, "table")} without data in {ReportWriter.FormatElapsed(report.Elapsed)}",
                    $"built {stepsOk:N0} of {stepsTotal:N0} key, index and identity steps",
                };
                if (options.CopyData)
                {
                    parts.Add(options.VerifyRowCounts ? "row counts match" : "row counts not checked");
                }

                return string.Join(" · ", parts);

            case ReportOutcome.CompletedWithProblems:
                var problems = new List<string>();
                var failed = report.Tables.Count(t => t.Status == TableStatus.Failed);
                var mismatched = report.Tables.Count(t => t.Status == TableStatus.CountMismatch);
                var failedSteps = report.Steps.Count(s => !s.Succeeded);
                if (failed > 0)
                {
                    problems.Add($"{Humanize.Plural(failed, "table")} failed");
                }

                if (mismatched > 0)
                {
                    problems.Add(Humanize.Plural(mismatched, "row-count mismatch", "row-count mismatches"));
                }

                if (failedSteps > 0)
                {
                    problems.Add($"{Humanize.Plural(failedSteps, "step")} failed");
                }

                return $"{string.Join(", ", problems)} · {copied}";

            case ReportOutcome.Cancelled when report.CancelledDuring == MigrationPhase.CreatingTables:
                return "That step runs in one transaction, which was rolled back. Nothing was changed.";

            case ReportOutcome.Cancelled:
                var empty = ReportWriter.LeftEmpty(report).Count;
                return empty > 0
                    ? $"{copied} before the cancel · {Humanize.Plural(empty, "table was", "tables were")} left EMPTY"
                    : $"{copied} before the cancel";

            default:
                return report.FatalError ?? "";
        }
    }
}

/// <summary>One thing in a "needs attention" group, e.g. a failed table and its error.</summary>
/// <param name="Step">The SQL behind it, when it is a step, for "Show SQL".</param>
public sealed record AttentionItemViewModel(string Title, string? Detail, ScriptStep? Step = null)
{
    public bool HasStep => Step is not null;
}

/// <summary>A group of things the user must know about after a run, shown as an expander.</summary>
/// <param name="Count">How many there are; <paramref name="Items"/> may list fewer.</param>
public sealed record AttentionGroupViewModel(AttentionKind Kind, string Title, string Description, bool IsSevere, int Count, IReadOnlyList<AttentionItemViewModel> Items)
{
    public string Header => $"{Title} ({Count:N0})";

    /// <summary>Severe groups start open.</summary>
    public bool IsExpanded => IsSevere;

    /// <summary>Steps not run come with a "Save SQL still to run" button.</summary>
    public bool OffersRemainingSql => Kind is AttentionKind.FailedSteps or AttentionKind.StepsNotRun;
}

public enum AttentionKind
{
    LeftEmpty,
    FailedTables,
    Mismatches,
    FailedSteps,
    RolledBack,
    NotStarted,
    StepsNotRun,
    CountsNotChecked,
}

/// <summary>Works out what needs attention after a run, most serious first.</summary>
public static class ReportAttention
{
    private const int MaxItems = 500;

    public static IReadOnlyList<AttentionGroupViewModel> Build(MigrationReport report, MigrationOptions options)
    {
        var groups = new List<AttentionGroupViewModel>();
        var leftEmpty = ReportWriter.LeftEmpty(report);
        Add(groups, AttentionKind.LeftEmpty, "Left empty by the cancellation",
            "These tables were emptied, or dropped and recreated, at the start of the run and never reloaded. Run again to fill them.",
            isSevere: true,
            leftEmpty.Select(t => new AttentionItemViewModel($"{t.Table.Target}", $"{Capitalise(ReportWriter.DescribeAction(t.Table.Action))}; not reloaded.")));

        Add(groups, AttentionKind.FailedTables, "Failed tables",
            "Each table is copied in one transaction, so none of a failed table's rows were kept.",
            isSevere: true,
            report.Tables.Where(t => t.Status == TableStatus.Failed)
                .Select(t => new AttentionItemViewModel($"{t.Table.Source.Name} → {t.Table.Target}", $"{t.Error} None of its rows were kept.")));

        Add(groups, AttentionKind.Mismatches, "Row-count mismatches",
            "The copy finished, but the target does not hold the number of rows the source does.",
            isSevere: true,
            report.Tables.Where(t => t.Status == TableStatus.CountMismatch)
                .Select(t => new AttentionItemViewModel(
                    $"{t.Table.Source.Name} → {t.Table.Target}",
                    $"source {Count(t.SourceRowCount)} / copied {t.RowsCopied:N0} / target {Count(t.TargetRowCount)}")));

        Add(groups, AttentionKind.FailedSteps, "Failed steps",
            "These keys, indexes or identity steps failed. Fix the cause, then run their SQL.",
            isSevere: true,
            report.Steps.Where(s => !s.Succeeded).Select(s => new AttentionItemViewModel(s.Step.Description, s.Error, s.Step)));

        Add(groups, AttentionKind.RolledBack, "Rolled back",
            "This table was being copied when the run was cancelled; its rows were rolled back.",
            isSevere: false,
            report.InterruptedTable is { } interrupted ? [new AttentionItemViewModel($"{interrupted.Source.Name} → {interrupted.Target}", null)] : []);

        var emptyNames = leftEmpty.Select(t => t.Table.Source.Name).ToHashSet();
        var notStarted = report.FatalError is not null && report.Tables.Count == 0
            ? report.Plan.Tables.Where(t => t.ReceivesData && options.CopyData)
            : report.Tables.Where(t => (t.Status == TableStatus.Cancelled && !ReferenceEquals(t.Table, report.InterruptedTable) && !emptyNames.Contains(t.Table.Source.Name))
                    || (t.Status == TableStatus.NotCopied && t.Error is not null))
                .Select(t => t.Table);
        Add(groups, AttentionKind.NotStarted, "Not started",
            "The run stopped before these tables were copied.",
            isSevere: false,
            notStarted.Select(t => new AttentionItemViewModel($"{t.Source.Name} → {t.Target}", null)));

        Add(groups, AttentionKind.StepsNotRun, "Steps not run",
            "The run stopped before these steps. They are not failures; their SQL is under \"SQL still to run\".",
            isSevere: false,
            report.StepsNotRun.Select(s => new AttentionItemViewModel(s.Description, null, s)));

        if (options.VerifyRowCounts && options.CopyData && (report.Cancelled || report.FatalError is not null))
        {
            Add(groups, AttentionKind.CountsNotChecked, "Row counts not checked",
                "These tables were copied, but the run stopped before their rows were counted.",
                isSevere: false,
                report.Tables.Where(t => t.Status == TableStatus.Copied && t.SourceRowCount is null)
                    .Select(t => new AttentionItemViewModel($"{t.Table.Source.Name} → {t.Table.Target}", $"{t.RowsCopied:N0} rows copied")));
        }

        return groups;
    }

    /// <summary>
    /// The SQL the run did not complete: failed steps first, then those never run. When the cancel
    /// or failure came while creating tables, that transaction was rolled back, so its SQL is included too.
    /// </summary>
    public static IReadOnlyList<(string Heading, IReadOnlyList<ScriptStep> Steps)> RemainingSql(MigrationReport report, MigrationScript script)
    {
        var sections = new List<(string, IReadOnlyList<ScriptStep>)>
        {
            ("Failed: fix the cause, then run these", report.Steps.Where(s => !s.Succeeded).Select(s => s.Step).ToList()),
        };
        var creationRolledBack = report.CancelledDuring == MigrationPhase.CreatingTables
            || (report.FatalError is not null && report.Tables.Count == 0);
        if (creationRolledBack)
        {
            sections.Add(("Not run: creating the tables (rolled back; no data was copied either)", script.BeforeData));
        }

        sections.Add(("Not run", report.StepsNotRun));
        return sections;
    }

    private static void Add(List<AttentionGroupViewModel> groups, AttentionKind kind, string title, string description, bool isSevere, IEnumerable<AttentionItemViewModel> items)
    {
        var all = items.ToList();
        if (all.Count == 0)
        {
            return;
        }

        var shown = all.Take(MaxItems).ToList();
        if (all.Count > MaxItems)
        {
            shown.Add(new AttentionItemViewModel($"... and {all.Count - MaxItems:N0} more; see the Tables tab or the saved report.", null));
        }

        groups.Add(new AttentionGroupViewModel(kind, title, description, isSevere, all.Count, shown));
    }

    private static string Count(long? value) => value is { } count ? count.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) : "?";

    private static string Capitalise(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
