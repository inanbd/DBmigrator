using System.Text.Json;
using DbMigrator.Desktop.ViewModels;

namespace DbMigrator.Desktop.Platform;

/// <summary>Choices remembered between runs, in <c>settings.json</c>. Never holds a secret.</summary>
public sealed class JsonSettingsStore(AppPaths paths, TimeProvider time, IAppLog log) : ISettingsStore
{
    internal const int Version = 1;

    private readonly Lock _lock = new();
    private bool _readOnly;

    public AppSettings Load()
    {
        lock (_lock)
        {
            var path = paths.SettingsFile;
            if (!File.Exists(path))
            {
                return new AppSettings();
            }

            var text = File.ReadAllText(path);
            var version = JsonProfileStore.ReadVersion(text);
            _readOnly = version > Version;
            try
            {
                var file = JsonSerializer.Deserialize(text, PlatformJsonContext.Default.SettingsFile)
                    ?? throw new JsonException("The file is empty.");
                return new AppSettings
                {
                    Naming = file.Naming,
                    CopyData = file.CopyData,
                    CreateIndexes = file.CreateIndexes,
                    CreateForeignKeys = file.CreateForeignKeys,
                    VerifyRowCounts = file.VerifyRowCounts,
                    LastSourceProfileId = file.LastSourceProfileId,
                    LastTargetProfileId = file.LastTargetProfileId,
                };
            }
            catch (JsonException ex) when (_readOnly)
            {
                // A newer version's settings this version cannot read: use defaults, keep the file.
                log.Error("Settings from a newer version could not be read; using defaults.", ex);
                return new AppSettings();
            }
            catch (JsonException ex)
            {
                var aside = PrivateFiles.Quarantine(path, time.GetUtcNow());
                log.Error($"Settings could not be read; the file was set aside as {aside}.", ex);
                return new AppSettings();
            }
        }
    }

    public void Save(AppSettings settings)
    {
        lock (_lock)
        {
            var path = paths.SettingsFile;
            using var fileLock = PrivateFiles.Lock(path, TimeSpan.FromSeconds(10));
            if (_readOnly || (File.Exists(path) && JsonProfileStore.ReadVersion(File.ReadAllText(path)) > Version))
            {
                _readOnly = true;
                throw new InvalidOperationException("Settings were written by a newer version of DbMigrator, so they are not changed.");
            }

            var file = new SettingsFile(
                Version,
                settings.Naming,
                settings.CopyData,
                settings.CreateIndexes,
                settings.CreateForeignKeys,
                settings.VerifyRowCounts,
                settings.LastSourceProfileId,
                settings.LastTargetProfileId);
            PrivateFiles.WriteAtomically(path, JsonSerializer.Serialize(file, PlatformJsonContext.Default.SettingsFile));
        }
    }
}
