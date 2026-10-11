using DbMigrator.Core.Providers;

namespace DbMigrator.Desktop.ViewModels;

/// <summary>
/// A saved connection. It has no member that can hold a secret, and only
/// <see cref="ProfileFactory"/> can create one, which strips every secret on the way in.
/// </summary>
public sealed class ConnectionProfile
{
    internal ConnectionProfile(Guid id, string name, string providerId, IReadOnlyDictionary<string, string> values, string additionalSettings, bool rememberPassword, DateTimeOffset lastUsedUtc)
    {
        Id = id;
        Name = name;
        ProviderId = providerId;
        Values = values;
        AdditionalSettings = additionalSettings;
        RememberPassword = rememberPassword;
        LastUsedUtc = lastUsedUtc;
    }

    public Guid Id { get; }

    public string Name { get; }

    public string ProviderId { get; }

    /// <summary>Field values by canonical keyword, without secrets.</summary>
    public IReadOnlyDictionary<string, string> Values { get; }

    public string AdditionalSettings { get; }

    /// <summary>Whether the profile's secrets are kept in the OS password store.</summary>
    public bool RememberPassword { get; }

    public DateTimeOffset LastUsedUtc { get; }

    public StoredProfile ToStored() => new(Id, Name, ProviderId, Values, AdditionalSettings, RememberPassword, LastUsedUtc);
}

/// <param name="Profile">The profile, without any secret.</param>
/// <param name="DroppedSecret">True when a secret had to be removed, e.g. one pasted into the additional settings.</param>
public sealed record ProfileCreation(ConnectionProfile Profile, bool DroppedSecret);

public static class ProfileFactory
{
    /// <summary>The OS password store key for one secret field of one profile.</summary>
    public static string SecretKey(Guid profileId, string keyword) => $"profile/{profileId:N}/{keyword}";

    public static ProfileCreation Create(
        Guid id,
        string name,
        IDatabaseProvider provider,
        IReadOnlyDictionary<string, string> values,
        string additionalSettings,
        bool rememberPassword,
        DateTimeOffset lastUsedUtc)
    {
        var format = provider.Connection;
        var kept = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var dropped = false;
        foreach (var (keyword, value) in values)
        {
            var field = format.FindField(keyword);
            if (field is null)
            {
                continue;
            }

            if (field.Kind == ConnectionFieldKind.Secret)
            {
                dropped |= value.Length > 0;
                continue;
            }

            if (value.Length > 0)
            {
                kept[field.Keyword] = value;
            }
        }

        // The additional settings go through the driver's parser, so a secret typed there under
        // any spelling (pwd=, PSW=, SSL Password=...) is recognised as a field and dropped.
        var additional = "";
        if (!string.IsNullOrWhiteSpace(additionalSettings))
        {
            ParsedConnectionString parsed;
            try
            {
                parsed = format.Parse(additionalSettings);
            }
            catch (ArgumentException)
            {
                // Unparseable text cannot be proven secret-free, so it is not kept.
                return new ProfileCreation(new ConnectionProfile(id, name, provider.Id, kept, "", rememberPassword, lastUsedUtc), DroppedSecret: true);
            }

            foreach (var (keyword, value) in parsed.Values)
            {
                if (format.FindField(keyword)?.Kind == ConnectionFieldKind.Secret)
                {
                    dropped = true;
                }
                else
                {
                    kept.TryAdd(keyword, value);
                }
            }

            additional = parsed.AdditionalSettings;
        }

        return new ProfileCreation(new ConnectionProfile(id, name.Trim(), provider.Id, kept, additional, rememberPassword, lastUsedUtc), dropped);
    }

    /// <summary>Rebuilds a profile read from disk, stripping secrets again in case the file was edited by hand.</summary>
    public static ConnectionProfile? Restore(ProviderCatalog catalog, StoredProfile stored)
    {
        var provider = catalog.Find(stored.ProviderId);
        return provider is null
            ? null
            : Create(stored.Id, stored.Name, provider, stored.Values, stored.AdditionalSettings, stored.RememberPassword, stored.LastUsedUtc).Profile;
    }
}
