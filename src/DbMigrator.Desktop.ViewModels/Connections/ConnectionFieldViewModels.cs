using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbMigrator.Core.Providers;

namespace DbMigrator.Desktop.ViewModels;

/// <summary>One connection-form field. Each kind has its own subclass so the view can pick a control by type.</summary>
public abstract partial class ConnectionFieldViewModel : ObservableObject
{
    protected ConnectionFieldViewModel(ConnectionField field)
    {
        Field = field;
    }

    public ConnectionField Field { get; }

    public string Keyword => Field.Keyword;

    public string Label => Field.IsRequired ? Field.Label + " *" : Field.Label;

    public string? Help => Field.Help;

    public bool HasHelp => !string.IsNullOrEmpty(Field.Help);

    public string Placeholder => Field.Placeholder ?? Field.DefaultValue ?? "";

    /// <summary>The value as text, as the connection-string builder takes it. Empty means unset.</summary>
    [ObservableProperty]
    public partial string Value { get; set; } = "";

    [ObservableProperty]
    public partial bool IsVisible { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; set; }

    public bool HasError => Error is not null;

    /// <summary>Lets a subclass raise its typed view of <see cref="Value"/>.</summary>
    protected virtual void ValueChanged()
    {
    }

    partial void OnValueChanged(string value) => ValueChanged();
}

public sealed class TextFieldViewModel(ConnectionField field) : ConnectionFieldViewModel(field);

public sealed partial class IntegerFieldViewModel : ConnectionFieldViewModel
{
    public IntegerFieldViewModel(ConnectionField field)
        : base(field)
    {
    }

    public decimal Minimum => Field.Minimum ?? 0;

    public decimal Maximum => Field.Maximum ?? int.MaxValue;

    /// <summary>The value as a number for a numeric box; null when unset.</summary>
    public decimal? Number
    {
        get => decimal.TryParse(Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : null;
        set => Value = value is null ? "" : decimal.Truncate(value.Value).ToString(CultureInfo.InvariantCulture);
    }

    protected override void ValueChanged() => OnPropertyChanged(nameof(Number));
}

public sealed partial class SecretFieldViewModel : ConnectionFieldViewModel
{
    public SecretFieldViewModel(ConnectionField field)
        : base(field)
    {
    }

    /// <summary>Whether the box shows the text instead of dots.</summary>
    [ObservableProperty]
    public partial bool IsRevealed { get; set; }
}

public sealed partial class ToggleFieldViewModel : ConnectionFieldViewModel
{
    public ToggleFieldViewModel(ConnectionField field)
        : base(field)
    {
    }

    public bool IsChecked
    {
        get => string.Equals(Value.Length == 0 ? Field.DefaultValue : Value, "True", StringComparison.OrdinalIgnoreCase);
        set => Value = value ? "True" : "False";
    }

    protected override void ValueChanged() => OnPropertyChanged(nameof(IsChecked));
}

public sealed partial class ChoiceFieldViewModel : ConnectionFieldViewModel
{
    public ChoiceFieldViewModel(ConnectionField field)
        : base(field)
    {
        Choices = field.Choices;
    }

    public IReadOnlyList<ConnectionChoice> Choices { get; }

    public ConnectionChoice? SelectedChoice
    {
        get
        {
            var value = Value.Length == 0 ? Field.DefaultValue : Value;
            return Choices.FirstOrDefault(c => string.Equals(c.Value, value, StringComparison.OrdinalIgnoreCase));
        }

        set => Value = value?.Value ?? "";
    }

    protected override void ValueChanged() => OnPropertyChanged(nameof(SelectedChoice));
}

public sealed partial class FilePathFieldViewModel(ConnectionField field, IFileService files) : ConnectionFieldViewModel(field)
{
    [RelayCommand]
    private async Task BrowseAsync()
    {
        var path = await files.PickFileAsync(Field.Label, Field.FileExtensions);
        if (path is not null)
        {
            Value = path;
        }
    }
}
