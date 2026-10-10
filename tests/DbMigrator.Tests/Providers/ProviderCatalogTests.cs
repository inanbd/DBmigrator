using DbMigrator.Core.Providers;
using DbMigrator.Providers;
using DbMigrator.TestSupport;

namespace DbMigrator.Tests.Providers;

public class ProviderCatalogTests
{
    [Fact]
    public void Built_in_catalog_splits_sources_and_targets()
    {
        var catalog = BuiltInProviders.Catalog;

        Assert.Equal(["sqlserver"], catalog.Sources.Select(p => p.Id));
        Assert.Equal(["postgresql"], catalog.Targets.Select(p => p.Id));
        Assert.Same(catalog.Get("SQLServer"), catalog.Find("sqlserver"));
        Assert.Null(catalog.Find("oracle"));
    }

    [Fact]
    public void Unknown_ids_list_the_known_ones() =>
        Assert.Contains("sqlserver", Assert.Throws<KeyNotFoundException>(() => BuiltInProviders.Catalog.Get("oracle")).Message);

    [Fact]
    public void Duplicate_ids_are_rejected()
    {
        var provider = new InMemoryProvider(new InMemorySourceDatabase(SchemaBuilder.Source()), new InMemoryTargetDatabase());

        Assert.Throws<ArgumentException>(() => new ProviderCatalog([provider, provider]));
    }
}
