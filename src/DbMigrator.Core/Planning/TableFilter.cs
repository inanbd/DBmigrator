using System.Text.RegularExpressions;
using DbMigrator.Core.Schema;

namespace DbMigrator.Core.Planning;

/// <summary>
/// Selects tables by glob patterns (<c>*</c> and <c>?</c>), case-insensitively. A pattern
/// with a dot matches <c>schema.table</c>; without one it matches the table name in any schema.
/// No include patterns means every table is included.
/// </summary>
public sealed class TableFilter
{
    private readonly Regex[] _include;
    private readonly Regex[] _exclude;

    public TableFilter(IEnumerable<string> include, IEnumerable<string> exclude)
    {
        Include = include.ToArray();
        Exclude = exclude.ToArray();
        _include = Include.Select(ToRegex).ToArray();
        _exclude = Exclude.Select(ToRegex).ToArray();
    }

    public static TableFilter All { get; } = new([], []);

    public IReadOnlyList<string> Include { get; }

    public IReadOnlyList<string> Exclude { get; }

    public bool Matches(QualifiedName table) =>
        (_include.Length == 0 || _include.Any(r => IsMatch(r, table))) && !_exclude.Any(r => IsMatch(r, table));

    private static bool IsMatch(Regex pattern, QualifiedName table) =>
        pattern.IsMatch(table.Name) || pattern.IsMatch($"{table.Schema}.{table.Name}");

    private static Regex ToRegex(string pattern)
    {
        var body = Regex.Escape(pattern.Trim()).Replace(@"\*", ".*", StringComparison.Ordinal).Replace(@"\?", ".", StringComparison.Ordinal);
        // Matches tests both "table" and "schema.table", so a pattern without a dot
        // still selects the table in any schema.
        return new Regex($"^{body}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
