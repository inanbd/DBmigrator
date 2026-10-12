using System.Security.Cryptography;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using DbMigrator.Core.Providers;

namespace DbMigrator.Desktop.ViewModels;

/// <summary>
/// A connection form generated from an engine's <see cref="ConnectionStringFormat"/>, with an
/// "edit as connection string" mode that never shows a secret.
/// </summary>
public sealed partial class ConnectionEditorViewModel : ObservableObject
{
    private bool _updating;

    public ConnectionEditorViewModel(ConnectionStringFormat format, IFileService files)
    {
        Format = format;
        AllFields = format.Fields.Select(f => Create(f, files)).ToList();
        Fields = AllFields.Where(f => !f.Field.IsAdvanced).ToList();
        AdvancedFields = AllFields.Where(f => f.Field.IsAdvanced).ToList();
        Secrets = AllFields.OfType<SecretFieldViewModel>().ToList();
        foreach (var field in AllFields)
        {
            field.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ConnectionFieldViewModel.Value))
                {
                    field.Error = null;
                    Refresh();
                }
            };
        }

        Refresh();
    }

    public ConnectionStringFormat Format { get; }

    public IReadOnlyList<ConnectionFieldViewModel> AllFields { get; }

    /// <summary>The fields shown in the main form.</summary>
    public IReadOnlyList<ConnectionFieldViewModel> Fields { get; }

    /// <summary>The fields under "Advanced".</summary>
    public IReadOnlyList<ConnectionFieldViewModel> AdvancedFields { get; }

    /// <summary>
    /// The password boxes shown under the connection string. The list never changes, so a box being
    /// typed in is never rebuilt; each box hides itself while its field does not apply.
    /// </summary>
    public IReadOnlyList<SecretFieldViewModel> Secrets { get; }

    public bool HasVisibleSecret => Secrets.Any(f => f.IsVisible);

    /// <summary>Whether "Advanced" is open. <see cref="Validate"/> opens it when a problem is inside.</summary>
    [ObservableProperty]
    public partial bool IsAdvancedExpanded { get; set; }

    /// <summary>
    /// In connection-string mode, the problems with fields that have no box on screen there,
    /// such as a missing database.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHiddenFieldErrors))]
    public partial string? HiddenFieldErrors { get; private set; }

    public bool HasHiddenFieldErrors => HiddenFieldErrors is not null;

    /// <summary>Keywords that are not fields, as "Key=Value;Key=Value".</summary>
    [ObservableProperty]
    public partial string AdditionalSettings { get; set; } = "";

    [ObservableProperty]
    public partial bool IsRawMode { get; set; }

    /// <summary>The connection string while in raw mode, with secrets removed.</summary>
    [ObservableProperty]
    public partial string RawText { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRawError))]
    public partial string? RawError { get; set; }

    public bool HasRawError => RawError is not null;

    /// <summary>Something the editor did on the user's behalf, such as moving a pasted password.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    public partial string? Notice { get; set; }

    public bool HasNotice => Notice is not null;

    /// <summary>A hash of everything entered, kept in memory only, to notice changes after connecting.</summary>
    [ObservableProperty]
    public partial string Fingerprint { get; private set; } = "";

    /// <summary>"Shop on sql01 as migrator".</summary>
    [ObservableProperty]
    public partial string Description { get; private set; } = "";

    /// <summary>Every non-empty value, secrets included.</summary>
    /// <summary>Filled-in values by keyword. Passwords are kept exactly as typed; other values are trimmed.</summary>
    public IReadOnlyDictionary<string, string> CurrentValues() =>
        AllFields.Select(f => (f.Keyword, Value: f.Field.Kind == ConnectionFieldKind.Secret ? f.Value : f.Value.Trim()))
            .Where(f => f.Value.Length > 0)
            .ToDictionary(f => f.Keyword, f => f.Value, StringComparer.OrdinalIgnoreCase);

    /// <summary>Secret values that are filled in and visible, by keyword.</summary>
    public IReadOnlyDictionary<string, string> SecretValues() =>
        AllFields.OfType<SecretFieldViewModel>().Where(f => f.IsVisible && f.Value.Length > 0).ToDictionary(f => f.Keyword, f => f.Value);

    /// <summary>
    /// Checks required fields and ranges; shows each problem under its field, and makes sure it is
    /// in view (see <see cref="Problems"/> for all of them in one place).
    /// </summary>
    public bool Validate()
    {
        HiddenFieldErrors = null;
        var valid = ValidateFields();
        if (!valid)
        {
            RevealProblems();
        }

        return valid;
    }

    /// <summary>What the last <see cref="Validate"/> or build found wrong, one sentence each.</summary>
    public IReadOnlyList<string> Problems() =>
        (RawError is { } raw ? [raw] : Array.Empty<string>())
            .Concat(AllFields.Where(f => f.IsVisible && f.Error is not null).Select(f => f.Error!))
            .ToList();

    private bool ValidateFields()
    {
        if (IsRawMode)
        {
            if (!ApplyRawText())
            {
                // The fields still hold the last text that parsed, so their problems are not about this text.
                // A password box is on screen with what it holds, so its problem stands.
                foreach (var field in AllFields.Where(f => f is not SecretFieldViewModel))
                {
                    field.Error = null;
                }

                return false;
            }
        }
        else
        {
            // In the form only the additional settings and the build set this, and both run again.
            RawError = null;
        }

        if (!MoveFieldsOutOfAdditionalSettings())
        {
            return false;
        }

        foreach (var field in AllFields)
        {
            field.Error = null;
        }

        var errors = Format.Validate(CurrentValues());
        foreach (var error in errors)
        {
            var field = AllFields.First(f => string.Equals(f.Keyword, error.Keyword, StringComparison.OrdinalIgnoreCase));
            field.Error = error.Message;
        }

        return errors.Count == 0;
    }

    /// <summary>
    /// A problem under a field that is out of sight would leave the user wondering why nothing happened:
    /// in connection-string mode only the password boxes show, so the rest are said under the text;
    /// in the form, "Advanced" opens when a problem is inside it.
    /// </summary>
    private void RevealProblems()
    {
        if (IsRawMode)
        {
            var hidden = AllFields.Where(f => f.IsVisible && f.Error is not null && f is not SecretFieldViewModel).Select(f => f.Error!).ToList();
            HiddenFieldErrors = hidden.Count == 0 ? null : string.Join(" ", hidden);
        }
        else if (RawError is not null || AdvancedFields.Any(f => f.IsVisible && f.Error is not null))
        {
            IsAdvancedExpanded = true;
        }
    }

    /// <exception cref="ArgumentException">The driver rejected a value; it is also shown under the field.</exception>
    public string BuildConnectionString()
    {
        try
        {
            return Format.Build(CurrentValues(), AdditionalSettings);
        }
        catch (ArgumentException ex)
        {
            var field = AllFields.FirstOrDefault(f => string.Equals(f.Keyword, ex.ParamName, StringComparison.OrdinalIgnoreCase));
            if (field is not null)
            {
                field.Error = ex.Message;
            }
            else
            {
                RawError = ex.Message;
            }

            RevealProblems();

            throw;
        }
    }

    /// <summary>Fills the form from a saved profile and any secrets found for it.</summary>
    public void Load(IReadOnlyDictionary<string, string> values, string additionalSettings, IReadOnlyDictionary<string, string> secrets)
    {
        _updating = true;
        try
        {
            foreach (var field in AllFields)
            {
                field.Value = (field.Field.Kind == ConnectionFieldKind.Secret ? secrets : values).GetValueOrDefault(field.Keyword) ?? "";
                field.Error = null;
            }

            HideSecrets();

            AdditionalSettings = additionalSettings;
            IsRawMode = false;
            RawError = null;
            HiddenFieldErrors = null;
            Notice = null;
        }
        finally
        {
            _updating = false;
        }

        Refresh();
    }

    partial void OnAdditionalSettingsChanged(string value)
    {
        // Like a field's error, a problem with the additional settings goes once they are edited.
        if (!_updating && !IsRawMode)
        {
            RawError = null;
        }

        Refresh();
    }

    /// <summary>Like a field's error, a problem said under the text goes once the text is edited.</summary>
    partial void OnRawTextChanged(string value) => HiddenFieldErrors = null;

    partial void OnIsRawModeChanged(bool value)
    {
        HiddenFieldErrors = null;
        if (_updating)
        {
            return;
        }

        if (value)
        {
            RawError = null;
            try
            {
                RawText = Format.Redact(Format.Build(CurrentValues(), AdditionalSettings));
            }
            catch (ArgumentException ex)
            {
                RawText = AdditionalSettings;
                RawError = ex.Message;
            }
        }
        else if (!ApplyRawText())
        {
            _updating = true;
            IsRawMode = true;
            _updating = false;
        }
    }

    /// <summary>Reads the raw text back into the form. Secrets found in it move into their boxes.</summary>
    private bool ApplyRawText()
    {
        ParsedConnectionString parsed;
        try
        {
            parsed = Format.Parse(RawText);
        }
        catch (ArgumentException ex)
        {
            RawError = ex.Message;
            return false;
        }

        RawError = null;
        _updating = true;
        try
        {
            var movedSecret = false;
            foreach (var field in AllFields)
            {
                var found = parsed.Values.GetValueOrDefault(field.Keyword);
                if (field.Field.Kind == ConnectionFieldKind.Secret)
                {
                    // The raw text was shown without secrets, so an absent secret keeps its box.
                    if (found is not null)
                    {
                        field.Value = found;
                        movedSecret = true;
                    }
                }
                else
                {
                    field.Value = found ?? "";
                }
            }

            AdditionalSettings = parsed.AdditionalSettings;
            Notice = movedSecret ? "Moved the password into the password box; it is never shown in the connection string." : null;
            if (movedSecret)
            {
                // Out of the visible text, so it is not shown and cannot overwrite a corrected password later.
                RawText = Format.Redact(RawText);
                HideSecrets();
            }
        }
        finally
        {
            _updating = false;
        }

        Refresh();
        return true;
    }

    /// <summary>Keywords typed into the additional settings that are fields move into their boxes.</summary>
    private bool MoveFieldsOutOfAdditionalSettings()
    {
        if (string.IsNullOrWhiteSpace(AdditionalSettings))
        {
            return true;
        }

        ParsedConnectionString parsed;
        try
        {
            parsed = Format.Parse(AdditionalSettings);
        }
        catch (ArgumentException ex)
        {
            RawError = $"Additional settings: {ex.Message}";
            return false;
        }

        if (parsed.Values.Count == 0)
        {
            return true;
        }

        _updating = true;
        try
        {
            var movedSecret = false;
            var moved = new List<string>();
            foreach (var (keyword, value) in parsed.Values)
            {
                // Typed here most recently, so it replaces what the box held.
                var field = AllFields.First(f => string.Equals(f.Keyword, keyword, StringComparison.OrdinalIgnoreCase));
                field.Value = value;
                if (field.Field.Kind == ConnectionFieldKind.Secret)
                {
                    movedSecret = true;
                }
                else
                {
                    moved.Add(field.Field.Label);
                }
            }

            AdditionalSettings = parsed.AdditionalSettings;
            if (movedSecret)
            {
                HideSecrets();
            }

            Notice = (movedSecret, moved.Count) switch
            {
                (true, 0) => "Moved the password from the additional settings into the password box.",
                (true, _) => $"Moved the password and {string.Join(", ", moved)} from the additional settings into their boxes.",
                (false, > 0) => $"Moved {string.Join(", ", moved)} from the additional settings into {(moved.Count == 1 ? "its box" : "their boxes")}.",
                _ => Notice,
            };
        }
        finally
        {
            _updating = false;
        }

        Refresh();
        return true;
    }

    /// <summary>A password that arrives from elsewhere (a profile, pasted text) starts hidden.</summary>
    private void HideSecrets()
    {
        foreach (var secret in AllFields.OfType<SecretFieldViewModel>())
        {
            secret.IsRevealed = false;
        }
    }

    private void Refresh()
    {
        if (_updating)
        {
            return;
        }

        var values = CurrentValues();
        foreach (var field in AllFields)
        {
            field.IsVisible = Format.IsVisible(field.Field, values);
        }

        var canonical = string.Join("\n", values.Where(v => Format.IsVisible(Format.FindField(v.Key)!, values))
            .OrderBy(v => v.Key, StringComparer.OrdinalIgnoreCase)
            .Select(v => $"{v.Key}={v.Value}")) + "\n" + AdditionalSettings.Trim();
        Fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        Description = Format.Describe(values);
        OnPropertyChanged(nameof(HasVisibleSecret));
    }

    private static ConnectionFieldViewModel Create(ConnectionField field, IFileService files) => field.Kind switch
    {
        ConnectionFieldKind.Integer => new IntegerFieldViewModel(field),
        ConnectionFieldKind.Secret => new SecretFieldViewModel(field),
        ConnectionFieldKind.Toggle => new ToggleFieldViewModel(field),
        ConnectionFieldKind.Choice => new ChoiceFieldViewModel(field),
        ConnectionFieldKind.FilePath => new FilePathFieldViewModel(field, files),
        _ => new TextFieldViewModel(field),
    };
}
