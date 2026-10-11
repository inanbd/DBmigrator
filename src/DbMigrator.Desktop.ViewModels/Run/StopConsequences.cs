using DbMigrator.Core.Execution;
using DbMigrator.Core.Planning;

namespace DbMigrator.Desktop.ViewModels;

/// <summary>What stopping now would leave behind, in plain sentences, for the Stop and close confirmations.</summary>
public static class StopConsequences
{
    private const int NamesListed = 5;

    public static IReadOnlyList<string> From(RunTracker tracker)
    {
        var bullets = new List<string>();
        var steps = tracker.StepsTotal;
        var options = tracker.Migration.Options;
        switch (tracker.Phase)
        {
            case null or MigrationPhase.CreatingTables:
                bullets.Add("Nothing has been changed yet: creating the tables runs in one transaction, which is rolled back.");
                break;

            case MigrationPhase.CopyingData:
                var copied = tracker.Rows.Where(r => r.IsDone).ToList();
                bullets.Add($"Tables already copied stay: {Humanize.Plural(copied.Count, "table")}, {Humanize.Count(copied.Sum(r => r.RowsCopied))} rows.");
                if (tracker.Copying is { } current)
                {
                    bullets.Add(EmptiedAtStart(current)
                        ? $"The table being copied, {current.Source}, is rolled back; none of its rows are kept. It was emptied at the start of the run, so {current.Table.Target} will stay EMPTY until you run again."
                        : $"The table being copied, {current.Source}, is rolled back; none of its rows are kept.");
                }

                var notYet = tracker.Rows.Where(r => r.Status == TableRunStatus.Waiting).ToList();
                var emptied = notYet.Where(EmptiedAtStart).ToList();
                var notYetText = $"{Humanize.Plural(notYet.Count, "table is", "tables are")} not copied yet.";
                if (emptied.Count > 0)
                {
                    notYetText += $" {Humanize.Plural(emptied.Count, "of them was", "of them were")} emptied or dropped and recreated at the start of the run "
                        + $"({Names(emptied)}) and will stay EMPTY until you run again.";
                }

                bullets.Add(notYetText);
                if (steps > 0)
                {
                    bullets.Add($"Keys, indexes and foreign keys are not built yet: {Humanize.Plural(steps, "step")}. Their SQL will be in the report.");
                }

                break;

            case MigrationPhase.CreatingConstraints:
                bullets.Add($"{CopyOutcome(tracker, options.CopyData)} {tracker.StepsDone:N0} of {steps:N0} key and index steps are built; "
                    + $"the remaining {steps - tracker.StepsDone:N0} will not run (their SQL will be in the report).");
                break;

            default:
                var notChecked = tracker.Rows.Count(r => r.Status == TableRunStatus.Copied);
                bullets.Add($"{CopyOutcome(tracker, options.CopyData)} The keys and indexes are done. Row counts for {Humanize.Plural(notChecked, "table")} will not be checked.");
                break;
        }

        return bullets;
    }

    /// <summary>Emptied, or dropped and recreated, in the opening transaction: stopping before it is reloaded leaves it empty.</summary>
    private static bool EmptiedAtStart(TableProgressRowViewModel row) => row.Table.Action is TableAction.Truncate or TableAction.DropAndCreate;

    /// <summary>How the data copy ended, in one sentence, without claiming more than happened.</summary>
    private static string CopyOutcome(RunTracker tracker, bool copyData)
    {
        if (!copyData)
        {
            return "No data was to be copied.";
        }

        var failed = tracker.Rows.Count(r => r.Status == TableRunStatus.Failed);
        return failed == 0 ? "All data is copied." : $"The data copy is over; {Humanize.Plural(failed, "table")} failed.";
    }

    private static string Names(List<TableProgressRowViewModel> rows) =>
        string.Join(", ", rows.Take(NamesListed).Select(r => r.Table.Target)) + (rows.Count > NamesListed ? $", and {rows.Count - NamesListed} more" : "");
}
