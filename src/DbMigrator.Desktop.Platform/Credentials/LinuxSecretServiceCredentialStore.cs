using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using DbMigrator.Desktop.ViewModels;

namespace DbMigrator.Desktop.Platform;

/// <summary>
/// The freedesktop Secret Service (GNOME Keyring, KWallet 5.97+, KeePassXC) through libsecret's
/// non-variadic <c>*v_sync</c> calls. The password crosses in memory: no secret-tool process,
/// nothing on any command line.
/// </summary>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("freebsd")]
internal sealed unsafe partial class LinuxSecretServiceCredentialStore : ICredentialStore
{
    private const string LibSecret = "libsecret-1.so.0";
    private const string LibGlib = "libglib-2.0.so.0";
    private const string SchemaName = "org.dbmigrator.ConnectionSecret";
    private const string AttributeName = "key";

    // sizeof(SecretSchema) on 64-bit: name, flags, 32 attributes of (name, type), and reserved fields.
    private const int SchemaSize = 592;

    private static readonly Lazy<IntPtr> Schema = new(CreateSchema);
    private readonly Lock _lock = new();
    private (IntPtr Hash, IntPtr Equal)? _glib;
    private string? _probe;
    private bool _probed;

    public string DisplayName => "Secret Service";

    public string? ProbeAvailability()
    {
        lock (_lock)
        {
            if (!_probed)
            {
                _probe = Probe();
                _probed = true;
            }

            return _probe;
        }
    }

    public void Save(string key, string secret)
    {
        RequireAvailable();
        var password = Utf8(secret);
        try
        {
            using var attributes = new Attributes(_glib!.Value, key);
            fixed (byte* label = Utf8($"{CredentialStores.Service}: {key}"))
            fixed (byte* pw = password)
            {
                IntPtr error = default;
                var ok = secret_password_storev_sync(Schema.Value, attributes.Table, IntPtr.Zero, label, pw, IntPtr.Zero, &error);
                ThrowIfError(error, "save");
                if (!ok)
                {
                    throw new CredentialStoreException("The keyring did not save the password.");
                }
            }
        }
        finally
        {
            Array.Clear(password);
        }
    }

    public string? Find(string key)
    {
        RequireAvailable();
        return Lookup(key);
    }

    public void Delete(string key)
    {
        RequireAvailable();
        using var attributes = new Attributes(_glib!.Value, key);
        IntPtr error = default;
        _ = secret_password_clearv_sync(Schema.Value, attributes.Table, IntPtr.Zero, &error);
        ThrowIfError(error, "delete");
    }

    private string? Probe()
    {
        if (!NativeLibrary.TryLoad(LibSecret, out _))
        {
            return "libsecret is not installed, so passwords cannot be remembered (install libsecret-1-0 or libsecret).";
        }

        if (!NativeLibrary.TryLoad(LibGlib, out var glib))
        {
            return "GLib is not installed, so passwords cannot be remembered.";
        }

        _glib = (NativeLibrary.GetExport(glib, "g_str_hash"), NativeLibrary.GetExport(glib, "g_str_equal"));
        // A key nothing is stored under: reaching the service needs no unlock prompt.
        _ = Lookup("availability-probe", out var error);
        return error is null ? null : $"The Secret Service cannot be reached, so passwords cannot be remembered: {error}";
    }

    private string? Lookup(string key)
    {
        var result = Lookup(key, out var error);
        return error is null ? result : throw new CredentialStoreException($"The keyring could not read the password: {error}");
    }

    private string? Lookup(string key, out string? errorMessage)
    {
        using var attributes = new Attributes(_glib!.Value, key);
        IntPtr error = default;
        var result = secret_password_lookupv_sync(Schema.Value, attributes.Table, IntPtr.Zero, &error);
        errorMessage = TakeError(error);
        if (result == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return errorMessage is null ? Marshal.PtrToStringUTF8(result) : null;
        }
        finally
        {
            // Clears the memory before freeing it.
            secret_password_free(result);
        }
    }

    private void RequireAvailable()
    {
        if (ProbeAvailability() is { } reason)
        {
            throw new CredentialStoreException(reason);
        }
    }

    private static void ThrowIfError(IntPtr error, string action)
    {
        if (TakeError(error) is { } message)
        {
            throw new CredentialStoreException($"The keyring could not {action} the password: {message}");
        }
    }

    /// <summary>The message of a GError, which is then freed; null when there was none.</summary>
    private static string? TakeError(IntPtr error)
    {
        if (error == IntPtr.Zero)
        {
            return null;
        }

        // GError { GQuark domain; gint code; gchar *message; }
        var message = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(error, 8)) ?? "unknown error";
        g_error_free(error);
        return message;
    }

    private static byte[] Utf8(string value)
    {
        var bytes = new byte[Encoding.UTF8.GetByteCount(value) + 1];
        Encoding.UTF8.GetBytes(value, bytes);
        return bytes;
    }

    private static IntPtr CreateSchema()
    {
        // Lives for the process; libsecret keeps no reference beyond each call.
        var schema = (IntPtr)NativeMemory.AllocZeroed(SchemaSize);
        Marshal.WriteIntPtr(schema, 0, Marshal.StringToCoTaskMemUTF8(SchemaName));
        Marshal.WriteInt32(schema, 8, 0); // SECRET_SCHEMA_NONE
        Marshal.WriteIntPtr(schema, 16, Marshal.StringToCoTaskMemUTF8(AttributeName));
        Marshal.WriteInt32(schema, 24, 0); // SECRET_SCHEMA_ATTRIBUTE_STRING
        return schema;
    }

    /// <summary>A GHashTable of attribute name to value, both UTF-8 strings owned by this object.</summary>
    private sealed class Attributes : IDisposable
    {
        private readonly IntPtr _name = Marshal.StringToCoTaskMemUTF8(AttributeName);
        private readonly IntPtr _value;

        public Attributes((IntPtr Hash, IntPtr Equal) glib, string key)
        {
            _value = Marshal.StringToCoTaskMemUTF8(key);
            Table = g_hash_table_new(glib.Hash, glib.Equal);
            _ = g_hash_table_insert(Table, _name, _value);
        }

        public IntPtr Table { get; }

        public void Dispose()
        {
            g_hash_table_unref(Table);
            Marshal.FreeCoTaskMem(_name);
            Marshal.FreeCoTaskMem(_value);
        }
    }

    [LibraryImport(LibSecret)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool secret_password_storev_sync(IntPtr schema, IntPtr attributes, IntPtr collection, byte* label, byte* password, IntPtr cancellable, IntPtr* error);

    [LibraryImport(LibSecret)]
    private static partial IntPtr secret_password_lookupv_sync(IntPtr schema, IntPtr attributes, IntPtr cancellable, IntPtr* error);

    [LibraryImport(LibSecret)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool secret_password_clearv_sync(IntPtr schema, IntPtr attributes, IntPtr cancellable, IntPtr* error);

    [LibraryImport(LibSecret)]
    private static partial void secret_password_free(IntPtr password);

    [LibraryImport(LibGlib)]
    private static partial IntPtr g_hash_table_new(IntPtr hashFunc, IntPtr equalFunc);

    [LibraryImport(LibGlib)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool g_hash_table_insert(IntPtr table, IntPtr key, IntPtr value);

    [LibraryImport(LibGlib)]
    private static partial void g_hash_table_unref(IntPtr table);

    [LibraryImport(LibGlib)]
    private static partial void g_error_free(IntPtr error);
}
