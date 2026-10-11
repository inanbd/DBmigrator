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

    public IReadOnlyList<SecretFieldViewModel> VisibleSecrets => AllFields.OfType<SecretFieldViewModel>().Where(f => f.IsVisible).ToList();

    public bool HasVisibleSecret => AllFields.OfType<SecretFieldViewModel>().Any(f => f.IsVisible);

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
    public IReadOnlyDictionary<string, string> CurrentValues() =>
        AllFields.Where(f => f.Value.Trim().Length > 0).ToDictionary(f => f.Keyword, f => f.Value.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>Secret values that are filled in and visible, by keyword.</summary>
    public IReadOnlyDictionary<string, string> SecretValues() =>
        AllFields.OfType<SecretFieldViewModel>().Where(f => f.IsVisible && f.Value.Length > 0).ToDictionary(f => f.Keyword, f => f.Value);

    /// <summary>Checks required fields and ranges; shows each problem under its field.</summary>
    public bool Validate()
    {
        if (IsRawMode && !ApplyRawText())
        {
            return false;
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

            AdditionalSettings = additionalSettings;
            IsRawMode = false;
            RawError = null;
            Notice = null;
        }
        finally
        {
            _updating = false;
        }

        Refresh();
    }

    partial void OnAdditionalSettingsChanged(string value) => Refresh();

    partial void OnIsRawModeChanged(bool value)
    {
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
            foreach (var (keyword, value) in parsed.Values)
            {
                var field = AllFields.First(f => string.Equals(f.Keyword, keyword, StringComparison.OrdinalIgnoreCase));
                if (field.Value.Length == 0)
                {
                    field.Value = value;
                }

                movedSecret |= field.Field.Kind == ConnectionFieldKind.Secret;
            }

            AdditionalSettings = parsed.AdditionalSettings;
            if (movedSecret)
            {
                Notice = "Moved the password from the additional settings into the password box.";
            }
        }
        finally
        {
            _updating = false;
        }

        Refresh();
        return true;
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
        OnPropertyChanged(nameof(VisibleSecrets));
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
