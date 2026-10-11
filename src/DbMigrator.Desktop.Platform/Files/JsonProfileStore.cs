using System.Text.Json;
using DbMigrator.Core.Providers;
using DbMigrator.Desktop.ViewModels;

namespace DbMigrator.Desktop.Platform;

/// <summary>
/// Saved connections in <c>profiles.json</c>. Holds no secret: every profile passes through
/// <see cref="ProfileFactory"/> on the way in and again on the way out. Each write re-reads the
/// file and replaces only the profile it is about, so two windows do not undo each other.
/// </summary>
public sealed class JsonProfileStore(AppPaths paths, ProviderCatalog catalog, TimeProvider time) : IProfileStore
{
    internal const int Version = 1;

    private readonly Lock _lock = new();
    private bool _readOnly;

    public ProfileLoadResult Load()
    {
        lock (_lock)
        {
            var path = paths.ProfilesFile;
            if (!File.Exists(path))
            {
                return new ProfileLoadResult([], null, IsReadOnly: false);
            }

            ProfileFile file;
            try
            {
                file = Parse(File.ReadAllText(path));
            }
            catch (JsonException)
            {
                var aside = PrivateFiles.Quarantine(path, time.GetUtcNow());
                return new ProfileLoadResult([], $"Saved connections could not be read, so the file was set aside as {aside}. You start with none.", IsReadOnly: false);
            }

            _readOnly = file.Version > Version;
            var profiles = file.Profiles
                .Select(p => ProfileFactory.Restore(catalog, ToStored(p)))
                .OfType<ConnectionProfile>()
                .OrderByDescending(p => p.LastUsedUtc)
                .ToList();
            var notice = _readOnly
                ? "Saved connections were written by a newer version of DbMigrator. They are shown, but changes to them are not saved."
                : null;
            return new ProfileLoadResult(profiles, notice, _readOnly);
        }
    }

    public void Save(ConnectionProfile profile) => Update(entries =>
    {
        entries.RemoveAll(e => e.Id == profile.Id);
        entries.Add(ToEntry(profile.ToStored()));
    });

    public void Delete(Guid id) => Update(entries => entries.RemoveAll(e => e.Id == id));

    private void Update(Action<List<ProfileEntry>> change)
    {
        lock (_lock)
        {
            if (_readOnly)
            {
                throw new InvalidOperationException("Saved connections were written by a newer version of DbMigrator, so they are not changed.");
            }

            var path = paths.ProfilesFile;
            var entries = File.Exists(path) ? Parse(File.ReadAllText(path)).Profiles : [];
            change(entries);
            var file = new ProfileFile(Version, entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList());
            PrivateFiles.WriteAtomically(path, JsonSerializer.Serialize(file, PlatformJsonContext.Default.ProfileFile));
        }
    }

    private static ProfileFile Parse(string json)
    {
        var file = JsonSerializer.Deserialize(json, PlatformJsonContext.Default.ProfileFile) ?? throw new JsonException("The file is empty.");
        return file with { Profiles = file.Profiles ?? [] };
    }

    private static StoredProfile ToStored(ProfileEntry e) =>
        new(e.Id, e.Name ?? "", e.Provider ?? "", e.Values ?? [], e.AdditionalSettings ?? "", e.RememberPassword, e.LastUsedUtc);

    private static ProfileEntry ToEntry(StoredProfile p) =>
        new(p.Id, p.Name, p.ProviderId, new Dictionary<string, string>(p.Values, StringComparer.OrdinalIgnoreCase), p.AdditionalSettings, p.RememberPassword, p.LastUsedUtc);
}
