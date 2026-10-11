using System.Text.Json;
using System.Text.Json.Serialization;
using DbMigrator.Core.Planning;

namespace DbMigrator.Desktop.Platform;

internal sealed record ProfileFile(int Version, List<ProfileEntry> Profiles);

internal sealed record ProfileEntry(
    Guid Id,
    string Name,
    string Provider,
    Dictionary<string, string> Values,
    string AdditionalSettings,
    bool RememberPassword,
    DateTimeOffset LastUsedUtc);

internal sealed record SettingsFile(
    int Version,
    IdentifierCase Naming,
    bool CopyData,
    bool CreateIndexes,
    bool CreateForeignKeys,
    bool VerifyRowCounts,
    Guid? LastSourceProfileId,
    Guid? LastTargetProfileId);

/// <summary>Just enough of a file to read its version, even when the rest is from a newer release.</summary>
internal sealed record VersionOnly(int Version);

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(ProfileFile))]
[JsonSerializable(typeof(SettingsFile))]
[JsonSerializable(typeof(VersionOnly))]
internal sealed partial class PlatformJsonContext : JsonSerializerContext;
