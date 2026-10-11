using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text;
using System.Text.Json;
using DbMigrator.Core.Providers;
using DbMigrator.Desktop.ViewModels;
using DbMigrator.TestSupport;

namespace DbMigrator.Desktop.Testing;

/// <summary>Timers that fire only when the test calls <see cref="Tick"/>.</summary>
public sealed class ManualUiScheduler : IUiScheduler
{
    private readonly List<Timer> _timers = [];

    public int ActiveTimers => _timers.Count;

    public IReadOnlyList<TimeSpan> Intervals => _timers.Select(t => t.Interval).ToList();

    public IDisposable StartTimer(TimeSpan interval, Action tick)
    {
        var timer = new Timer(this, interval, tick);
        _timers.Add(timer);
        return timer;
    }

    /// <summary>Fires every running timer once, on the calling thread.</summary>
    public void Tick()
    {
        foreach (var timer in _timers.ToList())
        {
            timer.Callback();
        }
    }

    private sealed class Timer(ManualUiScheduler owner, TimeSpan interval, Action tick) : IDisposable
    {
        public TimeSpan Interval => interval;

        public Action Callback => tick;

        public void Dispose() => owner._timers.Remove(this);
    }
}

/// <summary>
/// Answers dialogs shown through a <see cref="DialogHostViewModel"/> from a script, and records
/// every dialog shown. A dialog with no scripted answer stays open for the test to inspect.
/// </summary>
public sealed class ScriptedDialogs
{
    private readonly ConcurrentQueue<Func<DialogViewModel, bool>> _answers = new();

    public ScriptedDialogs(DialogHostViewModel host)
    {
        Host = host;
        host.PropertyChanged += OnHostChanged;
    }

    public DialogHostViewModel Host { get; }

    public List<DialogViewModel> Shown { get; } = [];

    public IEnumerable<ConfirmDialogViewModel> Confirms => Shown.OfType<ConfirmDialogViewModel>();

    public ConfirmDialogViewModel? LastConfirm => Confirms.LastOrDefault();

    /// <summary>Error dialogs close by themselves unless this is false.</summary>
    public bool CloseErrors { get; set; } = true;

    public void Confirm(bool answer) => _answers.Enqueue(dialog =>
    {
        if (dialog is not ConfirmDialogViewModel confirm)
        {
            return false;
        }

        (answer ? confirm.ConfirmCommand : confirm.CancelCommand).Execute(null);
        return true;
    });

    public void Prompt(string? text) => _answers.Enqueue(dialog =>
    {
        if (dialog is not PromptDialogViewModel prompt)
        {
            return false;
        }

        if (text is null)
        {
            prompt.CancelCommand.Execute(null);
        }
        else
        {
            prompt.Text = text;
            prompt.ConfirmCommand.Execute(null);
        }

        return true;
    });

    private void OnHostChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DialogHostViewModel.Current) || Host.Current is not { } dialog)
        {
            return;
        }

        Shown.Add(dialog);
        void Answer()
        {
            if (Host.Current != dialog)
            {
                return;
            }

            if (dialog is ErrorDialogViewModel error && CloseErrors)
            {
                error.CloseCommand.Execute(null);
            }
            else if (_answers.TryPeek(out var answer) && answer(dialog))
            {
                _answers.TryDequeue(out _);
            }
        }

        // Answer after the dialog is fully shown, as a user would.
        if (SynchronizationContext.Current is { } context)
        {
            context.Post(_ => Answer(), null);
        }
        else
        {
            Answer();
        }
    }
}

public sealed record SavedFileRecord(SaveFileRequest Request, FileTypeChoice Type, string Name, byte[] Bytes)
{
    public string Text => Encoding.UTF8.GetString(Bytes);
}

/// <summary>Save and open dialogs that need no user: saves are captured in memory.</summary>
public sealed class FakeFileService : IFileService
{
    public List<SaveFileRequest> SaveRequests { get; } = [];

    public List<SavedFileRecord> Saved { get; } = [];

    /// <summary>The next save dialog is cancelled.</summary>
    public bool CancelSave { get; set; }

    /// <summary>The file type chosen in save dialogs, by extension; the first offered when null.</summary>
    public string? ChooseExtension { get; set; }

    public string? NextPick { get; set; }

    /// <summary>When set, saving throws an <see cref="UnauthorizedAccessException"/> with this message.</summary>
    public string? SaveFailure { get; set; }

    public async Task<SavedFile?> SaveAsync(SaveFileRequest request, Func<FileTypeChoice, Stream, CancellationToken, Task> write, CancellationToken cancellationToken)
    {
        SaveRequests.Add(request);
        if (CancelSave)
        {
            return null;
        }

        if (SaveFailure is { } failure)
        {
            throw new UnauthorizedAccessException(failure);
        }

        var type = request.Types.FirstOrDefault(t => t.Extension == ChooseExtension) ?? request.Types[0];
        using var stream = new MemoryStream();
        await write(type, stream, cancellationToken);
        var name = Path.ChangeExtension(request.SuggestedName, type.Extension);
        Saved.Add(new SavedFileRecord(request, type, name, stream.ToArray()));
        return new SavedFile(name, "/saved/" + name);
    }

    public Task<string?> PickFileAsync(string title, IReadOnlyList<string> extensions) => Task.FromResult(NextPick);
}

public sealed class FakeClipboard : IClipboardService
{
    public List<string> Texts { get; } = [];

    public string? Text => Texts.LastOrDefault();

    public Task SetTextAsync(string text)
    {
        Texts.Add(text);
        return Task.CompletedTask;
    }
}

/// <summary>A password store in memory. It can be made unavailable or made to fail.</summary>
public sealed class InMemoryCredentialStore : ICredentialStore
{
    private readonly ConcurrentDictionary<string, string> _secrets = new();

    public string DisplayName => "Test keychain";

    public string? UnavailableReason { get; set; }

    /// <summary>When set, saving throws a <see cref="CredentialStoreException"/> with this message.</summary>
    public string? SaveFailure { get; set; }

    public ThreadGuard Thread { get; } = new();

    /// <summary>Holds every Find until released, like an unlock prompt the user has not answered.</summary>
    public Gate? FindGate { get; set; }

    public IReadOnlyDictionary<string, string> Secrets => _secrets;

    public string? ProbeAvailability()
    {
        Thread.Check();
        return UnavailableReason;
    }

    public void Save(string key, string secret)
    {
        Thread.Check();
        if (SaveFailure is { } failure)
        {
            throw new CredentialStoreException(failure);
        }

        _secrets[key] = secret;
    }

    public string? Find(string key)
    {
        Thread.Check();
        FindGate?.PassAsync(CancellationToken.None).GetAwaiter().GetResult();
        return _secrets.TryGetValue(key, out var secret) ? secret : null;
    }

    public void Delete(string key)
    {
        Thread.Check();
        _secrets.TryRemove(key, out _);
    }
}

/// <summary>Saved connections in memory, also kept as the JSON a file would hold, to search for secrets.</summary>
public sealed class InMemoryProfileStore(ProviderCatalog catalog) : IProfileStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly ConcurrentDictionary<Guid, StoredProfile> _profiles = new();

    public ThreadGuard Thread { get; } = new();

    public string? Notice { get; set; }

    /// <summary>Like a file from a newer version: loads, but every change throws.</summary>
    public bool IsReadOnly { get; set; }

    /// <summary>When set, writes throw an <see cref="IOException"/> with this message.</summary>
    public string? WriteFailure { get; set; }

    public IReadOnlyCollection<StoredProfile> Stored => _profiles.Values.ToList();

    /// <summary>Everything stored, as JSON text.</summary>
    public string Text => JsonSerializer.Serialize(_profiles.Values.OrderBy(p => p.Name, StringComparer.Ordinal).ToList(), Json);

    public void Add(StoredProfile profile) => _profiles[profile.Id] = profile;

    public ProfileLoadResult Load()
    {
        Thread.Check();
        var profiles = _profiles.Values.Select(p => ProfileFactory.Restore(catalog, p)).OfType<ConnectionProfile>().ToList();
        return new ProfileLoadResult(profiles, Notice, IsReadOnly);
    }

    public void Save(ConnectionProfile profile)
    {
        Thread.Check();
        RefuseWrites();
        _profiles[profile.Id] = profile.ToStored();
    }

    public void Delete(Guid id)
    {
        Thread.Check();
        RefuseWrites();
        _profiles.TryRemove(id, out _);
    }

    private void RefuseWrites()
    {
        if (IsReadOnly)
        {
            throw new InvalidOperationException("Saved connections were written by a newer version of DbMigrator, so they are not changed.");
        }

        if (WriteFailure is { } failure)
        {
            throw new IOException(failure);
        }
    }
}

public sealed class InMemorySettingsStore : ISettingsStore
{
    public AppSettings Settings { get; set; } = new();

    public int SaveCount { get; private set; }

    public ThreadGuard Thread { get; } = new();

    public AppSettings Load()
    {
        Thread.Check();
        return Settings;
    }

    public void Save(AppSettings settings)
    {
        Thread.Check();
        Settings = settings;
        SaveCount++;
    }
}

public sealed record ArchivedReport(string BaseName, string Markdown, string Json);

public sealed class InMemoryReportArchive : IReportArchive
{
    public List<ArchivedReport> Saved { get; } = [];

    /// <summary>When set, saving throws an <see cref="IOException"/> with this message.</summary>
    public string? Failure { get; set; }

    public ThreadGuard Thread { get; } = new();

    public string Save(string baseName, string markdown, string json)
    {
        Thread.Check();
        if (Failure is { } failure)
        {
            throw new IOException(failure);
        }

        Saved.Add(new ArchivedReport(baseName, markdown, json));
        return $"/reports/{baseName}.md";
    }
}

public sealed class InMemoryAppLog : IAppLog
{
    private readonly ConcurrentQueue<string> _lines = new();

    public string LogFolder => "/logs";

    public IReadOnlyCollection<string> Lines => _lines;

    public void Info(string message) => _lines.Enqueue($"INFO {message}");

    public void Error(string message, Exception? exception = null) => _lines.Enqueue($"ERROR {message} {exception}");
}
