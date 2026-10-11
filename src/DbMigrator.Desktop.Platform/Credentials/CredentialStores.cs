using DbMigrator.Desktop.ViewModels;

namespace DbMigrator.Desktop.Platform;

/// <summary>Picks the operating system's password store. There is never a file or environment fallback.</summary>
public static class CredentialStores
{
    /// <summary>The name entries are filed under in every store.</summary>
    internal const string Service = "DbMigrator";

    public static ICredentialStore ForCurrentPlatform()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsCredentialStore();
        }

        if (OperatingSystem.IsMacOS())
        {
            return new MacKeychainCredentialStore();
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD())
        {
            return new LinuxSecretServiceCredentialStore();
        }

        return new UnavailableCredentialStore("Password store", "This operating system has no supported password store, so passwords cannot be remembered.");
    }
}

/// <summary>A store that cannot be used, with the reason shown under the Remember checkbox.</summary>
public sealed class UnavailableCredentialStore(string displayName, string reason) : ICredentialStore
{
    public string DisplayName => displayName;

    public string? ProbeAvailability() => reason;

    public void Save(string key, string secret) => throw new CredentialStoreException(reason);

    public string? Find(string key) => null;

    public void Delete(string key)
    {
    }
}
