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

            try
            {
                var file = JsonSerializer.Deserialize(File.ReadAllText(path), PlatformJsonContext.Default.SettingsFile)
                    ?? throw new JsonException("The file is empty.");
                _readOnly = file.Version > Version;
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
            if (_readOnly)
            {
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
            PrivateFiles.WriteAtomically(paths.SettingsFile, JsonSerializer.Serialize(file, PlatformJsonContext.Default.SettingsFile));
        }
    }
}
