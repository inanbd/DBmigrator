namespace DbMigrator.Core.Providers;

/// <summary>The engines a front end offers, looked up by <see cref="IDatabaseProvider.Id"/>.</summary>
public sealed class ProviderCatalog
{
    private readonly Dictionary<string, IDatabaseProvider> _byId;

    /// <exception cref="ArgumentException">Two providers share an id.</exception>
    public ProviderCatalog(IEnumerable<IDatabaseProvider> providers)
    {
        All = providers.ToList();
        _byId = new Dictionary<string, IDatabaseProvider>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in All)
        {
            if (!_byId.TryAdd(provider.Id, provider))
            {
                throw new ArgumentException($"Two providers have the id '{provider.Id}'.", nameof(providers));
            }
        }

        Sources = All.Where(p => p.SupportsSource).ToList();
        Targets = All.Where(p => p.SupportsTarget).ToList();
    }

    public IReadOnlyList<IDatabaseProvider> All { get; }

    public IReadOnlyList<IDatabaseProvider> Sources { get; }

    public IReadOnlyList<IDatabaseProvider> Targets { get; }

    public IDatabaseProvider? Find(string id) => _byId.GetValueOrDefault(id);

    /// <exception cref="KeyNotFoundException">No provider has that id.</exception>
    public IDatabaseProvider Get(string id) =>
        Find(id) ?? throw new KeyNotFoundException($"Unknown engine '{id}'. Known engines: {string.Join(", ", _byId.Keys)}.");
}
