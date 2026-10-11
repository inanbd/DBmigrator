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
                    bullets.Add($"The table being copied, {current.Source}, is rolled back; none of its rows are kept.");
                }

                var notYet = tracker.Rows.Where(r => r.Status == TableRunStatus.Waiting).ToList();
                var emptied = notYet.Append(tracker.Copying).OfType<TableProgressRowViewModel>()
                    .Where(r => r.Table.Action is TableAction.Truncate or TableAction.DropAndCreate)
                    .ToList();
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
                bullets.Add($"All data is copied. {tracker.StepsDone:N0} of {steps:N0} key and index steps are built; "
                    + $"the remaining {steps - tracker.StepsDone:N0} will not run (their SQL will be in the report).");
                break;

            default:
                var notChecked = tracker.Rows.Count(r => r.Status == TableRunStatus.Copied);
                bullets.Add($"All data is copied and keys are built. Row counts for {Humanize.Plural(notChecked, "table")} will not be checked.");
                break;
        }

        return bullets;
    }

    private static string Names(List<TableProgressRowViewModel> rows) =>
        string.Join(", ", rows.Take(NamesListed).Select(r => r.Table.Target)) + (rows.Count > NamesListed ? $", and {rows.Count - NamesListed} more" : "");
}
