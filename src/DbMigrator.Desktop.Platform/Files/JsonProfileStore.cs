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

            var text = File.ReadAllText(path);
            if (ReadVersion(text) is not { } version)
            {
                var aside = PrivateFiles.Quarantine(path, time.GetUtcNow());
                return new ProfileLoadResult([], $"Saved connections could not be read, so the file was set aside as {aside}. You start with none.", IsReadOnly: false);
            }

            // A newer version's file is never set aside or rewritten, even when this version cannot read it.
            _readOnly = version > Version;
            ProfileFile file;
            try
            {
                file = Parse(text);
            }
            catch (JsonException) when (_readOnly)
            {
                return new ProfileLoadResult([], NewerNotice + " This version cannot read them.", IsReadOnly: true);
            }
            catch (JsonException)
            {
                var aside = PrivateFiles.Quarantine(path, time.GetUtcNow());
                return new ProfileLoadResult([], $"Saved connections could not be read, so the file was set aside as {aside}. You start with none.", IsReadOnly: false);
            }

            var profiles = file.Profiles
                .Select(p => ProfileFactory.Restore(catalog, ToStored(p)))
                .OfType<ConnectionProfile>()
                .OrderByDescending(p => p.LastUsedUtc)
                .ToList();
            return new ProfileLoadResult(profiles, _readOnly ? NewerNotice + " They are shown, but changes to them are not saved." : null, _readOnly);
        }
    }

    public void Save(ConnectionProfile profile) => Update(entries =>
    {
        entries.RemoveAll(e => e.Id == profile.Id);
        entries.Add(ToEntry(profile.ToStored()));
    });

    public void Delete(Guid id) => Update(entries => entries.RemoveAll(e => e.Id == id));

    private const string NewerNotice = "Saved connections were written by a newer version of DbMigrator.";

    private void Update(Action<List<ProfileEntry>> change)
    {
        lock (_lock)
        {
            if (_readOnly)
            {
                throw ReadOnlyError();
            }

            var path = paths.ProfilesFile;

            // Another window may write the file too: read, change and write it under one lock.
            using var fileLock = PrivateFiles.Lock(path, TimeSpan.FromSeconds(10));
            List<ProfileEntry> entries = [];
            if (File.Exists(path))
            {
                var text = File.ReadAllText(path);
                if (ReadVersion(text) is > Version)
                {
                    // A newer version wrote it since this window loaded it: never downgrade it.
                    _readOnly = true;
                    throw ReadOnlyError();
                }

                try
                {
                    entries = Parse(text).Profiles;
                }
                catch (JsonException ex)
                {
                    throw new InvalidOperationException("The saved connections file could not be read, so it was not changed.", ex);
                }
            }

            change(entries);

            // Every entry passes through ProfileFactory again, so a secret edited into the file by hand never survives a write.
            var cleaned = entries
                .Select(e => ProfileFactory.Restore(catalog, ToStored(e)) is { } restored ? ToEntry(restored.ToStored()) : e)
                .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            PrivateFiles.WriteAtomically(path, JsonSerializer.Serialize(new ProfileFile(Version, cleaned), PlatformJsonContext.Default.ProfileFile));
        }
    }

    private static InvalidOperationException ReadOnlyError() =>
        new("Saved connections were written by a newer version of DbMigrator, so they are not changed.");

    /// <summary>The file's version, or null when it is not even a JSON object with one.</summary>
    internal static int? ReadVersion(string json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, PlatformJsonContext.Default.VersionOnly)?.Version;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ProfileFile Parse(string json)
    {
        var file = JsonSerializer.Deserialize(json, PlatformJsonContext.Default.ProfileFile) ?? throw new JsonException("The file is empty.");

        // Nullability is not enforced by the parser: a hand-edited null entry or value is dropped, not crashed on.
        var profiles = (file.Profiles ?? []).Where(p => p is not null).ToList();
        return file with { Profiles = profiles };
    }

    private static StoredProfile ToStored(ProfileEntry e) =>
        new(
            e.Id,
            e.Name ?? "",
            e.Provider ?? "",
            (e.Values ?? []).Where(v => v.Value is not null).ToDictionary(v => v.Key, v => v.Value, StringComparer.OrdinalIgnoreCase),
            e.AdditionalSettings ?? "",
            e.RememberPassword,
            e.LastUsedUtc);

    private static ProfileEntry ToEntry(StoredProfile p) =>
        new(p.Id, p.Name, p.ProviderId, new Dictionary<string, string>(p.Values, StringComparer.OrdinalIgnoreCase), p.AdditionalSettings, p.RememberPassword, p.LastUsedUtc);
}
