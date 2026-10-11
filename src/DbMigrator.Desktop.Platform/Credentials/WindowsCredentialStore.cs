using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using System.Text;
using DbMigrator.Desktop.ViewModels;

namespace DbMigrator.Desktop.Platform;

/// <summary>Windows Credential Manager: generic credentials, protected per user by Windows (DPAPI).</summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsCredentialStore : ICredentialStore
{
    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;
    private const int MaxBlobBytes = 2560; // CRED_MAX_CREDENTIAL_BLOB_SIZE
    private const int ErrorNotFound = 1168;
    private const int ErrorNoSuchLogonSession = 1312;

    public string DisplayName => "Windows Credential Manager";

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

    public unsafe void Save(string key, string secret)
    {
        var blob = Encoding.Unicode.GetBytes(secret);
        try
        {
            if (blob.Length > MaxBlobBytes)
            {
                throw new CredentialStoreException($"The password is too long for Windows Credential Manager (at most {MaxBlobBytes / 2} characters).");
            }

            fixed (char* target = Target(key))
            fixed (char* user = Environment.UserName)
            fixed (byte* data = blob)
            {
                var credential = new Credential
                {
                    Type = CredTypeGeneric,
                    TargetName = (IntPtr)target,
                    UserName = (IntPtr)user,
                    CredentialBlobSize = (uint)blob.Length,
                    CredentialBlob = (IntPtr)data,
                    Persist = CredPersistLocalMachine,
                };
                if (!CredWrite(&credential, 0))
                {
                    throw Failure("save", Marshal.GetLastPInvokeError());
                }
            }
        }
        finally
        {
            Array.Clear(blob);
        }
    }

    public string? Find(string key)
    {
        if (!CredRead(Target(key), CredTypeGeneric, 0, out var pointer))
        {
            var error = Marshal.GetLastPInvokeError();
            return error == ErrorNotFound ? null : throw Failure("read", error);
        }

        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            return Marshal.PtrToStringUni(credential.CredentialBlob, (int)credential.CredentialBlobSize / 2);
        }
        finally
        {
            CredFree(pointer);
        }
    }

    public void Delete(string key)
    {
        if (!CredDelete(Target(key), CredTypeGeneric, 0) && Marshal.GetLastPInvokeError() is var error and not ErrorNotFound)
        {
            throw Failure("delete", error);
        }
    }

    private static CredentialStoreException Failure(string action, int error) => error == ErrorNoSuchLogonSession
        ? new CredentialStoreException("Windows Credential Manager is not available to this account (no logon session), so passwords cannot be remembered.")
        : new CredentialStoreException($"Windows Credential Manager could not {action} the password (error {error}).");

    private static string Target(string key) => $"{CredentialStores.Service}/{key}";

    [StructLayout(LayoutKind.Sequential)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool CredWrite(Credential* credential, uint flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [LibraryImport("advapi32.dll", EntryPoint = "CredDeleteW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredDelete(string target, uint type, uint flags);

    [LibraryImport("advapi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void CredFree(IntPtr buffer);
}
