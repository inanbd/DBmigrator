using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbMigrator.Core.Diagnostics;
using DbMigrator.Core.Providers;

namespace DbMigrator.Desktop.ViewModels;

public enum ConnectionStatus
{
    Idle,
    Testing,
    Connecting,
    Connected,
    /// <summary>Fields changed after connecting; connect again to continue.</summary>
    Changed,
    Failed,
}

/// <summary>An engine as offered in a picker.</summary>
public sealed record ProviderOption(IDatabaseProvider Provider)
{
    public string DisplayName => Provider.DisplayName;
}

public sealed record ProfileListItemViewModel(ConnectionProfile Profile, string Description)
{
    public string Name => Profile.Name;
}

/// <summary>An issue as a row: severity word, code, subject and message.</summary>
public sealed record IssueRowViewModel(MigrationIssue Issue)
{
    public string Severity => Issue.Severity switch
    {
        IssueSeverity.Error => "Error",
        IssueSeverity.Warning => "Warning",
        _ => "Note",
    };

    public bool IsError => Issue.Severity == IssueSeverity.Error;

    public bool IsWarning => Issue.Severity == IssueSeverity.Warning;

    public string Code => Issue.Code;

    public string Subject => Issue.Subject;

    public string Message => Issue.Message;
}

/// <summary>
/// Shared by the Source and Target steps: an engine, a saved or new connection, an optional
/// remembered password, and Test / Connect.
/// </summary>
public abstract partial class ConnectionStepViewModel : WizardStepViewModel
{
    private readonly ConnectionRole _role;
    private string? _connectedFingerprint;
    private bool _profilesLoaded;
    private bool _loadingProfile;

    protected ConnectionStepViewModel(ShellServices services, MigrationSession session, ConnectionRole role)
    {
        Services = services;
        Session = session;
        _role = role;
        Providers = (role == ConnectionRole.Source ? services.Providers.Sources : services.Providers.Targets).Select(p => new ProviderOption(p)).ToList();
        SelectedProvider = Providers.Count > 0 ? Providers[0] : null;
        CredentialStoreName = services.Credentials.DisplayName;
    }

    protected ShellServices Services { get; }

    protected MigrationSession Session { get; }

    public IReadOnlyList<ProviderOption> Providers { get; }

    [ObservableProperty]
    public partial ProviderOption? SelectedProvider { get; set; }

    [ObservableProperty]
    public partial ConnectionEditorViewModel? Editor { get; private set; }

    public ObservableCollection<ProfileListItemViewModel> Profiles { get; } = [];

    private List<ConnectionProfile> _allProfiles = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteProfileCommand))]
    public partial ProfileListItemViewModel? SelectedProfile { get; set; }

    /// <summary>Whether Save also keeps the password in the OS password store. Off by default.</summary>
    [ObservableProperty]
    public partial bool RememberPassword { get; set; }

    public string CredentialStoreName { get; }

    /// <summary>Why the password store cannot be used, when it cannot; null while unknown or usable.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRememberPassword), nameof(RememberPasswordNote))]
    public partial string? CredentialStoreUnavailableReason { get; private set; }

    public bool CanRememberPassword => CredentialStoreUnavailableReason is null && Editor?.HasVisibleSecret == true;

    public string? RememberPasswordNote =>
        CredentialStoreUnavailableReason is { } reason ? $"{reason} You will be asked for the password each time."
        : Editor?.HasVisibleSecret == false ? "This connection has no password to remember."
        : null;

    /// <summary>A banner about profiles, e.g. that a corrupt profiles file was set aside.</summary>
    [ObservableProperty]
    public partial string? ProfileNotice { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnected), nameof(IsFailed), nameof(StatusText))]
    public partial ConnectionStatus Status { get; private set; }

    public bool IsConnected => Status == ConnectionStatus.Connected;

    [ObservableProperty]
    public partial ConnectionCheck? Check { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<IssueRowViewModel> Findings { get; private set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<string> Hints { get; private set; } = [];

    public virtual string StatusText => Status switch
    {
        ConnectionStatus.Idle => IdleText,
        ConnectionStatus.Testing => "Testing the connection...",
        ConnectionStatus.Connecting => "Connecting...",
        ConnectionStatus.Changed => "Changed since you connected: connect again to continue.",
        ConnectionStatus.Failed => "Could not connect.",
        _ => "Connected.",
    };

    protected abstract string IdleText { get; }

    /// <summary>The text of the Connect button.</summary>
    public abstract string ConnectLabel { get; }

    /// <summary>The status card shows the error with its suggestions, so the window does not repeat it.</summary>
    public override bool ShowsErrorBanner => false;

    public bool IsFailed => Status == ConnectionStatus.Failed;

    public override bool CanAdvance => Status == ConnectionStatus.Connected && !IsBusy && Editor?.Fingerprint == _connectedFingerprint;

    public override string? BlockingReason => CanAdvance ? null : _role == ConnectionRole.Source ? "Connect to the source to continue." : "Connect to the target to continue.";

    public override async Task OnEnteringAsync(CancellationToken cancellationToken)
    {
        if (_profilesLoaded)
        {
            return;
        }

        _profilesLoaded = true;
        var credentials = Services.Credentials;
        var profiles = Services.Profiles;
        try
        {
            var (reason, loaded) = await BackgroundWork.RunAsync(() => (credentials.ProbeAvailability(), profiles.Load()), cancellationToken);
            CredentialStoreUnavailableReason = reason;
            _allProfiles = loaded.Profiles.ToList();
            ProfileNotice = loaded.Notice;
            RefreshProfiles();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Services.Log.Error("Loading profiles failed.", ex);
            ProfileNotice = $"Saved connections could not be loaded: {ex.Message}";
        }
    }

    partial void OnSelectedProviderChanged(ProviderOption? value)
    {
        if (Editor is { } old)
        {
            old.PropertyChanged -= OnEditorChanged;
        }

        Editor = value is null ? null : new ConnectionEditorViewModel(value.Provider.Connection, Services.Files);
        if (Editor is not null)
        {
            Editor.PropertyChanged += OnEditorChanged;
        }

        if (!_loadingProfile)
        {
            SelectedProfile = null;
            RefreshProfiles();
        }

        OnEditorFingerprintChanged();
        OnPropertyChanged(nameof(CanRememberPassword));
        OnPropertyChanged(nameof(RememberPasswordNote));
    }

    partial void OnSelectedProfileChanged(ProfileListItemViewModel? value)
    {
        if (value is not null && !_loadingProfile)
        {
            _ = LoadProfileAsync(value.Profile);
        }
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task TestConnectionAsync(CancellationToken cancellationToken)
    {
        if (!TryResolve(out var connection))
        {
            return;
        }

        Status = ConnectionStatus.Testing;
        Hints = [];
        ConnectionCheck? check = null;
        var ok = await GuardAsync(async ct => check = await MigrationSession.TestAsync(connection.Provider, connection.ConnectionString, _role, ct), "Testing the connection...", cancellationToken);
        if (ok && check is not null)
        {
            Check = check;
            Findings = check.Findings.Select(f => new IssueRowViewModel(f)).ToList();
            Status = IsStillConnected ? ConnectionStatus.Connected : ConnectionStatus.Idle;
            StatusTestPassed = $"Connection works: {check.ServerVersion}, database {check.Database}, login {check.User}.";
        }
        else
        {
            OnFailed(cancellationToken);
        }
    }

    /// <summary>The result of the last successful Test, shown until something changes.</summary>
    [ObservableProperty]
    public partial string? StatusTestPassed { get; private set; }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        if (!TryResolve(out var connection))
        {
            return;
        }

        Status = ConnectionStatus.Connecting;
        Hints = [];
        StatusTestPassed = null;
        var fingerprint = Editor!.Fingerprint;
        var ok = await GuardAsync(ct => ConnectCoreAsync(connection, ct), ConnectingText, cancellationToken);
        if (!ok)
        {
            OnFailed(cancellationToken);
            return;
        }

        _connectedFingerprint = fingerprint;
        Check = _role == ConnectionRole.Source ? Session.SourceCheck : Session.TargetCheck;
        Findings = (Check?.Findings ?? []).Select(f => new IssueRowViewModel(f)).ToList();
        Status = ConnectionStatus.Connected;
        OnConnected();
        RaiseCanAdvanceChanged();
        await RecordUseAsync();
    }

    protected abstract string ConnectingText { get; }

    protected abstract Task ConnectCoreAsync(ResolvedConnection connection, CancellationToken cancellationToken);

    /// <summary>Called on the UI thread after a successful connect, to build the summary.</summary>
    protected abstract void OnConnected();

    [RelayCommand]
    private async Task SaveProfileAsync()
    {
        if (SelectedProvider is not { } provider || Editor is not { } editor || !editor.Validate())
        {
            return;
        }

        var name = await Services.Dialogs.PromptAsync(new PromptRequest(
            "Save connection", "Name", SelectedProfile?.Name ?? editor.Description, "Save"));
        if (name is null)
        {
            return;
        }

        var existing = _allProfiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) && p.ProviderId == provider.Provider.Id);
        var id = existing?.Id ?? SelectedProfile?.Profile.Id ?? Guid.NewGuid();
        if (existing is null && SelectedProfile is { } selected && !string.Equals(selected.Name, name, StringComparison.OrdinalIgnoreCase))
        {
            // A new name saves a new profile rather than renaming the selected one.
            id = Guid.NewGuid();
        }

        var remember = RememberPassword && CanRememberPassword;
        var created = ProfileFactory.Create(id, name, provider.Provider, editor.CurrentValues(), editor.AdditionalSettings, remember, Services.Time.GetUtcNow());
        var secrets = editor.SecretValues();
        var keys = provider.Provider.Connection.SecretKeywords;
        var credentials = Services.Credentials;
        var profiles = Services.Profiles;
        var profile = created.Profile;
        string? storeError = null;
        try
        {
            storeError = await BackgroundWork.RunAsync(() =>
            {
                string? error = null;
                if (remember)
                {
                    try
                    {
                        foreach (var keyword in keys)
                        {
                            var key = ProfileFactory.SecretKey(id, keyword);
                            if (secrets.TryGetValue(keyword, out var secret))
                            {
                                credentials.Save(key, secret);
                            }
                            else
                            {
                                credentials.Delete(key);
                            }
                        }
                    }
                    catch (CredentialStoreException ex)
                    {
                        error = ex.Message;
                        profile = ProfileFactory.Create(id, name, provider.Provider, profile.Values, profile.AdditionalSettings, rememberPassword: false, profile.LastUsedUtc).Profile;
                    }
                }

                if (!profile.RememberPassword)
                {
                    DeleteSecrets(credentials, keys, id);
                }

                profiles.Save(profile);
                return error;
            }, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ShowError(ex);
            return;
        }

        ProfileNotice = storeError is not null
            ? $"Couldn't save the password to {CredentialStoreName}: {storeError} The connection was saved without it."
            : created.DroppedSecret && !remember
                ? "Saved without the password."
                : null;
        RememberPassword = profile.RememberPassword;
        _allProfiles.RemoveAll(p => p.Id == profile.Id);
        _allProfiles.Add(profile);
        _loadingProfile = true;
        RefreshProfiles();
        SelectedProfile = Profiles.FirstOrDefault(p => p.Profile.Id == profile.Id);
        _loadingProfile = false;
    }

    private bool CanDeleteProfile() => SelectedProfile is not null;

    [RelayCommand(CanExecute = nameof(CanDeleteProfile))]
    private async Task DeleteProfileAsync()
    {
        if (SelectedProfile is not { } selected)
        {
            return;
        }

        if (!await Services.Dialogs.ConfirmAsync(new ConfirmRequest(
            "Delete saved connection?",
            $"\"{selected.Name}\" will be removed{(selected.Profile.RememberPassword ? $", along with its password in {CredentialStoreName}" : "")}.",
            [],
            "Delete",
            IsDestructive: true)))
        {
            return;
        }

        var provider = Services.Providers.Find(selected.Profile.ProviderId);
        var keys = provider?.Connection.SecretKeywords ?? [];
        var credentials = Services.Credentials;
        var profiles = Services.Profiles;
        try
        {
            await BackgroundWork.RunAsync(() =>
            {
                DeleteSecrets(credentials, keys, selected.Profile.Id);
                profiles.Delete(selected.Profile.Id);
            }, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError(ex);
            return;
        }

        _allProfiles.RemoveAll(p => p.Id == selected.Profile.Id);
        SelectedProfile = null;
        RefreshProfiles();
    }

    [RelayCommand]
    private async Task CopyErrorAsync()
    {
        if (Error is not null)
        {
            await Services.Clipboard.SetTextAsync($"{Error}{Environment.NewLine}{ErrorDetails}");
        }
    }

    private static void DeleteSecrets(ICredentialStore credentials, IReadOnlyList<string> keys, Guid id)
    {
        foreach (var keyword in keys)
        {
            try
            {
                credentials.Delete(ProfileFactory.SecretKey(id, keyword));
            }
            catch (CredentialStoreException)
            {
                // Best effort: an entry that cannot be removed is harmless without its profile.
            }
        }
    }

    private bool TryResolve(out ResolvedConnection connection)
    {
        connection = null!;
        if (SelectedProvider is not { } provider || Editor is not { } editor || !editor.Validate())
        {
            return false;
        }

        string connectionString;
        try
        {
            connectionString = editor.BuildConnectionString();
        }
        catch (ArgumentException ex)
        {
            ShowError(ex);
            return false;
        }

        connection = new ResolvedConnection(provider.Provider, editor.CurrentValues(), editor.AdditionalSettings, connectionString, editor.Description, SelectedProfile?.Profile.Id);
        return true;
    }

    private bool IsStillConnected => _connectedFingerprint is not null && Editor?.Fingerprint == _connectedFingerprint;

    private void OnFailed(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            // Cancelling leaves things as they were before the attempt.
            Status = IsStillConnected ? ConnectionStatus.Connected : ConnectionStatus.Idle;
        }
        else
        {
            Status = ConnectionStatus.Failed;
            _connectedFingerprint = null;
        }

        RaiseCanAdvanceChanged();
    }

    private async Task LoadProfileAsync(ConnectionProfile profile)
    {
        var provider = Services.Providers.Find(profile.ProviderId);
        if (provider is null)
        {
            return;
        }

        _loadingProfile = true;
        try
        {
            SelectedProvider = Providers.FirstOrDefault(p => p.Provider == provider) ?? SelectedProvider;
        }
        finally
        {
            _loadingProfile = false;
        }

        IReadOnlyDictionary<string, string> secrets = new Dictionary<string, string>();
        string? missing = null;
        if (profile.RememberPassword && CredentialStoreUnavailableReason is null)
        {
            var credentials = Services.Credentials;
            try
            {
                var found = await BackgroundWork.RunAsync(() => provider.Connection.SecretKeywords
                    .Select(k => (Keyword: k, Secret: credentials.Find(ProfileFactory.SecretKey(profile.Id, k))))
                    .Where(s => s.Secret is not null)
                    .ToDictionary(s => s.Keyword, s => s.Secret!), CancellationToken.None);
                secrets = found;
                if (found.Count == 0)
                {
                    missing = $"No saved password found in {CredentialStoreName}; enter it.";
                }
            }
            catch (CredentialStoreException ex)
            {
                missing = $"Couldn't read the password from {CredentialStoreName}: {ex.Message}";
            }
        }

        Editor?.Load(profile.Values, profile.AdditionalSettings, secrets);
        RememberPassword = profile.RememberPassword;
        ProfileNotice = missing;
    }

    private async Task RecordUseAsync()
    {
        if (SelectedProfile is not { } selected)
        {
            return;
        }

        var profile = selected.Profile;
        var provider = Services.Providers.Find(profile.ProviderId);
        if (provider is null)
        {
            return;
        }

        var used = ProfileFactory.Create(profile.Id, profile.Name, provider, profile.Values, profile.AdditionalSettings, profile.RememberPassword, Services.Time.GetUtcNow()).Profile;
        var profiles = Services.Profiles;
        try
        {
            await BackgroundWork.RunAsync(() => profiles.Save(used), CancellationToken.None);
            _allProfiles.RemoveAll(p => p.Id == used.Id);
            _allProfiles.Add(used);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Services.Log.Error("Recording when a profile was last used failed.", ex);
        }
    }

    private void RefreshProfiles()
    {
        var providerId = SelectedProvider?.Provider.Id;
        var keep = SelectedProfile?.Profile.Id;
        Profiles.Clear();
        foreach (var profile in _allProfiles.Where(p => p.ProviderId == providerId).OrderByDescending(p => p.LastUsedUtc))
        {
            var description = Services.Providers.Find(profile.ProviderId)?.Connection.Describe(profile.Values) ?? "";
            Profiles.Add(new ProfileListItemViewModel(profile, description));
        }

        if (keep is not null)
        {
            _loadingProfile = true;
            SelectedProfile = Profiles.FirstOrDefault(p => p.Profile.Id == keep);
            _loadingProfile = false;
        }
    }

    private void OnEditorChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConnectionEditorViewModel.Fingerprint))
        {
            OnEditorFingerprintChanged();
        }
        else if (e.PropertyName == nameof(ConnectionEditorViewModel.HasVisibleSecret))
        {
            OnPropertyChanged(nameof(CanRememberPassword));
            OnPropertyChanged(nameof(RememberPasswordNote));
        }
    }

    private void OnEditorFingerprintChanged()
    {
        StatusTestPassed = null;
        if (_connectedFingerprint is not null)
        {
            Status = Editor?.Fingerprint == _connectedFingerprint ? ConnectionStatus.Connected : ConnectionStatus.Changed;
        }

        RaiseCanAdvanceChanged();
    }

    protected override void ShowError(Exception exception)
    {
        base.ShowError(exception);
        Hints = ConnectionHints.For(exception);
    }
}
