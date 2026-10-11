using DbMigrator.Core.Planning;

namespace DbMigrator.Desktop.ViewModels;

public sealed record ConfirmRequest(
    string Title,
    string Message,
    IReadOnlyList<string> Bullets,
    string ConfirmText,
    string CancelText = "Cancel",
    bool IsDestructive = false);

public sealed record PromptRequest(string Title, string Label, string InitialValue, string ConfirmText = "OK");

public sealed record ErrorRequest(string Title, string Message, string? Details = null);

/// <summary>Modal questions. The app shows them as an overlay; tests script the answers.</summary>
public interface IDialogService
{
    Task<bool> ConfirmAsync(ConfirmRequest request);

    /// <returns>The text entered, or null when cancelled.</returns>
    Task<string?> PromptAsync(PromptRequest request);

    Task ShowErrorAsync(ErrorRequest request);
}

/// <summary>A file type offered in a save dialog, e.g. Markdown (*.md).</summary>
public sealed record FileTypeChoice(string Name, string Extension);

public sealed record SaveFileRequest(string Title, string SuggestedName, IReadOnlyList<FileTypeChoice> Types);

/// <summary>Where a file was saved. <see cref="Path"/> is null when the platform gives no local path.</summary>
public sealed record SavedFile(string Name, string? Path);

public interface IFileService
{
    /// <summary>
    /// Asks where to save, then calls <paramref name="write"/> with the chosen type and an open stream.
    /// Returns null when the user cancels.
    /// </summary>
    Task<SavedFile?> SaveAsync(SaveFileRequest request, Func<FileTypeChoice, Stream, CancellationToken, Task> write, CancellationToken cancellationToken);

    /// <summary>Asks for an existing file; returns its local path, or null when cancelled.</summary>
    Task<string?> PickFileAsync(string title, IReadOnlyList<string> extensions);
}

public interface IClipboardService
{
    Task SetTextAsync(string text);
}

/// <summary>Repeating timers on the UI thread.</summary>
public interface IUiScheduler
{
    /// <summary>Calls <paramref name="tick"/> on the UI thread every <paramref name="interval"/> until disposed.</summary>
    IDisposable StartTimer(TimeSpan interval, Action tick);
}

/// <summary>An OS password store failed (for example a locked keychain).</summary>
public sealed class CredentialStoreException : Exception
{
    public CredentialStoreException()
    {
    }

    public CredentialStoreException(string message)
        : base(message)
    {
    }

    public CredentialStoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The operating system's password store. Methods block (an OS unlock prompt may appear), so call
/// them only through <see cref="BackgroundWork"/>. Implementations never fall back to a file.
/// </summary>
public interface ICredentialStore
{
    /// <summary>"Windows Credential Manager", "macOS Keychain" or "Secret Service".</summary>
    string DisplayName { get; }

    /// <summary>Null when the store can be used; otherwise why not, in words for the user.</summary>
    string? ProbeAvailability();

    /// <exception cref="CredentialStoreException">The store refused.</exception>
    void Save(string key, string secret);

    /// <returns>The secret, or null when there is none for <paramref name="key"/>.</returns>
    string? Find(string key);

    /// <summary>Removes the secret; does nothing when there is none.</summary>
    void Delete(string key);
}

/// <summary>A saved connection as it is stored: never holds a secret.</summary>
public sealed record StoredProfile(
    Guid Id,
    string Name,
    string ProviderId,
    IReadOnlyDictionary<string, string> Values,
    string AdditionalSettings,
    bool RememberPassword,
    DateTimeOffset LastUsedUtc);

/// <param name="Notice">Something the user should know, e.g. that a corrupt file was set aside.</param>
/// <param name="IsReadOnly">The file was written by a newer version, so it is not overwritten.</param>
public sealed record ProfileLoadResult(IReadOnlyList<ConnectionProfile> Profiles, string? Notice, bool IsReadOnly);

/// <summary>Saved connections. Blocking file I/O: call through <see cref="BackgroundWork"/>.</summary>
public interface IProfileStore
{
    ProfileLoadResult Load();

    void Save(ConnectionProfile profile);

    void Delete(Guid id);
}

/// <summary>Choices the app remembers between runs. Never holds a secret.</summary>
public sealed record AppSettings
{
    public IdentifierCase Naming { get; init; } = IdentifierCase.Lower;

    public bool CopyData { get; init; } = true;

    public bool CreateIndexes { get; init; } = true;

    public bool CreateForeignKeys { get; init; } = true;

    public bool VerifyRowCounts { get; init; } = true;

    public Guid? LastSourceProfileId { get; init; }

    public Guid? LastTargetProfileId { get; init; }
}

public interface ISettingsStore
{
    AppSettings Load();

    void Save(AppSettings settings);
}

/// <summary>Where finished runs are saved automatically.</summary>
public interface IReportArchive
{
    /// <summary>Writes the report as Markdown and JSON; returns the Markdown file's path.</summary>
    string Save(string baseName, string markdown, string json);
}

public interface IAppLog
{
    string LogFolder { get; }

    void Info(string message);

    void Error(string message, Exception? exception = null);
}
