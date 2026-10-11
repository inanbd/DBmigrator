using System.Runtime.InteropServices;
using DbMigrator.Desktop.Platform;
using DbMigrator.Desktop.Testing;
using DbMigrator.Desktop.ViewModels;

namespace DbMigrator.Desktop.Tests.Platform;

/// <summary>What every password store must do. Each test uses its own key and removes it.</summary>
public abstract class CredentialStoreContract
{
    private readonly string _key = $"test/{Guid.NewGuid():N}/Password";

    protected abstract ICredentialStore CreateStore();

    [Fact]
    public void Saves_finds_overwrites_and_deletes()
    {
        var store = CreateStore();
        try
        {
            store.Save(_key, "first-sécret");
            Assert.Equal("first-sécret", store.Find(_key));

            store.Save(_key, "second");
            Assert.Equal("second", store.Find(_key));

            store.Delete(_key);
            Assert.Null(store.Find(_key));
        }
        finally
        {
            store.Delete(_key);
        }
    }

    [Fact]
    public void An_absent_key_is_null_and_deleting_it_does_nothing()
    {
        var store = CreateStore();

        Assert.Null(store.Find(_key));
        store.Delete(_key);
    }
}

public sealed class InMemoryCredentialStoreTests : CredentialStoreContract
{
    protected override ICredentialStore CreateStore() => new InMemoryCredentialStore();
}

/// <summary>
/// The real store of the machine running the tests. Off by default, because it needs an unlocked
/// keyring; the os-keyring workflow turns it on with DBMIGRATOR_TEST_OS_KEYRING=1.
/// </summary>
public sealed class OsCredentialStoreTests : CredentialStoreContract
{
    protected override ICredentialStore CreateStore()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("DBMIGRATOR_TEST_OS_KEYRING") == "1", "Set DBMIGRATOR_TEST_OS_KEYRING=1 to test the OS password store.");
        var store = CredentialStores.ForCurrentPlatform();
        if (store.ProbeAvailability() is { } reason)
        {
            Assert.Fail($"{store.DisplayName} is not available: {reason}");
        }

        return store;
    }
}

public sealed class LinuxSecretServiceAvailabilityTests
{
    [Fact]
    public void A_missing_libsecret_gives_a_reason_instead_of_a_crash()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only.");
        var store = CredentialStores.ForCurrentPlatform();

        var reason = store.ProbeAvailability();

        Assert.Equal("Secret Service", store.DisplayName);
        if (!NativeLibrary.TryLoad("libsecret-1.so.0", out _))
        {
            Assert.Equal("libsecret is not installed, so passwords cannot be remembered (install libsecret-1-0 or libsecret).", reason);
            var error = Assert.Throws<CredentialStoreException>(() => store.Save("k", "v"));
            Assert.Equal(reason, error.Message);
        }
    }
}
