using DbMigrator.Core.Diagnostics;
using DbMigrator.Core.Execution;
using DbMigrator.Core.Planning;

namespace DbMigrator.Desktop.ViewModels;

/// <param name="Current">The plan made from the target as it is now.</param>
/// <param name="Differences">How it differs from the reviewed plan, in sentences; empty when it does not.</param>
public sealed record PreflightResult(PreparedMigration Current, IReadOnlyList<string> Differences)
{
    public bool CanRun => Differences.Count == 0 && !Current.Plan.HasErrors;
}

/// <summary>Compares two plans for what a DBA must know before running: tables, actions, schemas, errors.</summary>
public static class PlanFingerprint
{
    public static IReadOnlyList<string> Of(PreparedMigration migration)
    {
        var plan = migration.Plan;
        return plan.Tables.Select(t => $"table {t.Source.Name} -> {t.Target} {t.Action} exists={t.TargetExists}")
            .Concat(plan.SchemasToCreate.Select(s => $"schema {s}"))
            .Concat(plan.Issues.Where(i => i.Severity == IssueSeverity.Error).Select(i => $"error {i.Code} {i.Subject}"))
            .ToList();
    }

    public static IReadOnlyList<string> Diff(PreparedMigration reviewed, PreparedMigration current)
    {
        var differences = new List<string>();
        var before = reviewed.Plan.Tables.ToDictionary(t => t.Source.Name);
        foreach (var table in current.Plan.Tables)
        {
            if (!before.TryGetValue(table.Source.Name, out var old))
            {
                differences.Add($"{table.Source.Name} is now part of the plan.");
                continue;
            }

            if (table.TargetExists && !old.TargetExists)
            {
                differences.Add($"{table.Target} now exists in the target.");
            }
            else if (!table.TargetExists && old.TargetExists)
            {
                differences.Add($"{table.Target} no longer exists in the target.");
            }
            else if (table.Action != old.Action)
            {
                differences.Add($"{table.Source.Name} would now be handled as \"{ReportWriter.DescribeAction(table.Action)}\" instead of \"{ReportWriter.DescribeAction(old.Action)}\".");
            }
        }

        var schemasBefore = reviewed.Plan.SchemasToCreate.ToHashSet(StringComparer.Ordinal);
        var schemasNow = current.Plan.SchemasToCreate.ToHashSet(StringComparer.Ordinal);
        differences.AddRange(schemasBefore.Except(schemasNow).Select(s => $"Schema {s} no longer needs creating."));
        differences.AddRange(schemasNow.Except(schemasBefore).Select(s => $"Schema {s} now needs creating."));

        var errorsBefore = reviewed.Plan.Issues.Where(i => i.Severity == IssueSeverity.Error).Select(i => (i.Code, i.Subject)).ToHashSet();
        differences.AddRange(current.Plan.Issues
            .Where(i => i.Severity == IssueSeverity.Error && !errorsBefore.Contains((i.Code, i.Subject)))
            .Select(i => $"New problem with {i.Subject}: {i.Message}"));
        return differences;
    }
}
