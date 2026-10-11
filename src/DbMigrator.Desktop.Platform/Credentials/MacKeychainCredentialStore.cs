using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using DbMigrator.Desktop.ViewModels;

namespace DbMigrator.Desktop.Platform;

/// <summary>
/// The login keychain through Security.framework's generic-password calls. The password crosses
/// as a byte buffer: no <c>security</c> process is started, so it never reaches a command line.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed unsafe partial class MacKeychainCredentialStore : ICredentialStore
{
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const int ErrSecItemNotFound = -25300;
    private const int ErrSecDuplicateItem = -25299;
    private const int ErrSecInteractionNotAllowed = -25308;
    private const int ErrSecAuthFailed = -25293;
    private const int UserCanceled = -128;

    public string DisplayName => "macOS Keychain";

    public string? ProbeAvailability()
    {
        try
        {
            _ = Find("availability-probe");
            return null;
        }
        catch (CredentialStoreException ex)
        {
            return ex.Message;
        }
    }

    public void Save(string key, string secret)
    {
        var service = Encoding.UTF8.GetBytes(CredentialStores.Service);
        var account = Encoding.UTF8.GetBytes(key);
        var data = Encoding.UTF8.GetBytes(secret);
        try
        {
            var status = SecKeychainAddGenericPassword(IntPtr.Zero, (uint)service.Length, service, (uint)account.Length, account, (uint)data.Length, data, out var item);
            if (status == ErrSecDuplicateItem)
            {
                status = FindItem(service, account, out item);
                if (status == 0)
                {
                    status = SecKeychainItemModifyAttributesAndData(item, IntPtr.Zero, (uint)data.Length, data);
                }
            }

            Release(item);
            Check(status, "save");
        }
        finally
        {
            Array.Clear(data);
        }
    }

    public string? Find(string key)
    {
        var service = Encoding.UTF8.GetBytes(CredentialStores.Service);
        var account = Encoding.UTF8.GetBytes(key);
        var status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)service.Length, service, (uint)account.Length, account, out var length, out var data, out var item);
        try
        {
            if (status == ErrSecItemNotFound)
            {
                return null;
            }

            Check(status, "read");
            return Marshal.PtrToStringUTF8(data, (int)length);
        }
        finally
        {
            if (data != IntPtr.Zero)
            {
                _ = SecKeychainItemFreeContent(IntPtr.Zero, data);
            }

            Release(item);
        }
    }

    public void Delete(string key)
    {
        var service = Encoding.UTF8.GetBytes(CredentialStores.Service);
        var account = Encoding.UTF8.GetBytes(key);
        var status = FindItem(service, account, out var item);
        if (status == ErrSecItemNotFound)
        {
            return;
        }

        try
        {
            Check(status, "find");
            Check(SecKeychainItemDelete(item), "delete");
        }
        finally
        {
            Release(item);
        }
    }

    /// <summary>
    /// Finds the item without reading its password: reading it is what asks the user for access, which
    /// replacing or deleting the item does not need.
    /// </summary>
    private static int FindItem(byte[] service, byte[] account, out IntPtr item) =>
        SecKeychainFindItem(IntPtr.Zero, (uint)service.Length, service, (uint)account.Length, account, null, null, out item);

    private static void Check(int status, string action)
    {
        if (status != 0)
        {
            throw new CredentialStoreException(status switch
            {
                ErrSecInteractionNotAllowed => "The keychain is locked and cannot ask to be unlocked right now.",
                UserCanceled => "Access to the keychain was cancelled.",
                ErrSecAuthFailed => "The keychain password was not accepted.",
                _ => $"The macOS keychain could not {action} the password (OSStatus {status}).",
            });
        }
    }

    private static void Release(IntPtr item)
    {
        if (item != IntPtr.Zero)
        {
            CFRelease(item);
        }
    }

    [LibraryImport(Security)]
    private static partial int SecKeychainAddGenericPassword(IntPtr keychain, uint serviceNameLength, byte[] serviceName, uint accountNameLength, byte[] accountName, uint passwordLength, byte[] passwordData, out IntPtr itemRef);

    [LibraryImport(Security)]
    private static partial int SecKeychainFindGenericPassword(IntPtr keychainOrArray, uint serviceNameLength, byte[] serviceName, uint accountNameLength, byte[] accountName, out uint passwordLength, out IntPtr passwordData, out IntPtr itemRef);

    [LibraryImport(Security, EntryPoint = "SecKeychainFindGenericPassword")]
    private static partial int SecKeychainFindItem(IntPtr keychainOrArray, uint serviceNameLength, byte[] serviceName, uint accountNameLength, byte[] accountName, uint* passwordLength, IntPtr* passwordData, out IntPtr itemRef);

    [LibraryImport(Security)]
    private static partial int SecKeychainItemModifyAttributesAndData(IntPtr itemRef, IntPtr attrList, uint length, byte[] data);

    [LibraryImport(Security)]
    private static partial int SecKeychainItemDelete(IntPtr itemRef);

    [LibraryImport(Security)]
    private static partial int SecKeychainItemFreeContent(IntPtr attrList, IntPtr data);

    [LibraryImport(CoreFoundation)]
    private static partial void CFRelease(IntPtr cf);
}
