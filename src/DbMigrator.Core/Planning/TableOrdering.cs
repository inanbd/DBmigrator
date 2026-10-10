using DbMigrator.Core.Schema;

namespace DbMigrator.Core.Planning;

internal static class TableOrdering
{
    /// <summary>
    /// Orders tables so each comes after the tables its foreign keys reference, which lets rows
    /// load into tables whose foreign keys already exist. Ties and cycles fall back to name order.
    /// </summary>
    public static List<TableDefinition> ByDependencies(IReadOnlyList<TableDefinition> tables)
    {
        var comparer = QualifiedNameComparer.Ordinal;
        var byName = tables.ToDictionary(t => t.Name, comparer);
        var pending = new Dictionary<QualifiedName, int>(comparer);
        var dependents = new Dictionary<QualifiedName, List<QualifiedName>>(comparer);
        foreach (var table in tables)
        {
            var references = table.ForeignKeys
                .Select(fk => fk.ReferencedTable)
                .Where(r => !comparer.Equals(r, table.Name) && byName.ContainsKey(r))
                .Distinct(comparer)
                .ToList();
            pending[table.Name] = references.Count;
            foreach (var referenced in references)
            {
                if (!dependents.TryGetValue(referenced, out var list))
                {
                    dependents[referenced] = list = [];
                }

                list.Add(table.Name);
            }
        }

        var remaining = new SortedSet<QualifiedName>(byName.Keys, comparer);
        var ready = new SortedSet<QualifiedName>(remaining.Where(n => pending[n] == 0), comparer);
        var ordered = new List<TableDefinition>(tables.Count);
        while (remaining.Count > 0)
        {
            // An empty ready set means a cycle: break it at the first remaining table.
            var next = ready.Count > 0 ? ready.Min : remaining.Min;
            ready.Remove(next);
            remaining.Remove(next);
            ordered.Add(byName[next]);

            foreach (var dependent in dependents.GetValueOrDefault(next) ?? [])
            {
                if (--pending[dependent] == 0 && remaining.Contains(dependent))
                {
                    ready.Add(dependent);
                }
            }
        }

        return ordered;
    }
}
