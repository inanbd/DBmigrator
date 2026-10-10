namespace DbMigrator.Core.Schema;

/// <summary>A schema-qualified object name, spelled exactly as the database stores it.</summary>
public readonly record struct QualifiedName(string Schema, string Name)
{
    public override string ToString() => $"{Schema}.{Name}";
}

/// <summary>Compares qualified names with a given identifier comparer (case rules differ per engine).</summary>
public sealed class QualifiedNameComparer(StringComparer comparer) : IEqualityComparer<QualifiedName>, IComparer<QualifiedName>
{
    public static QualifiedNameComparer Ordinal { get; } = new(StringComparer.Ordinal);
    public static QualifiedNameComparer OrdinalIgnoreCase { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool Equals(QualifiedName x, QualifiedName y) =>
        comparer.Equals(x.Schema, y.Schema) && comparer.Equals(x.Name, y.Name);

    public int GetHashCode(QualifiedName obj) =>
        HashCode.Combine(comparer.GetHashCode(obj.Schema), comparer.GetHashCode(obj.Name));

    public int Compare(QualifiedName x, QualifiedName y)
    {
        var bySchema = comparer.Compare(x.Schema, y.Schema);
        return bySchema != 0 ? bySchema : comparer.Compare(x.Name, y.Name);
    }
}
