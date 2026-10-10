using System.Data.Common;
using System.Globalization;

namespace DbMigrator.Core.Providers;

public enum ConnectionRole
{
    Source,
    Target,
}

public enum ConnectionFieldKind
{
    Text,
    Integer,
    /// <summary>A password or key passphrase: masked on screen and never written to a profile file.</summary>
    Secret,
    /// <summary>A yes/no setting whose values are <c>True</c> and <c>False</c>.</summary>
    Toggle,
    Choice,
    FilePath,
}

/// <summary>What a field means, so front ends can describe a connection without knowing the engine.</summary>
public enum ConnectionFieldRole
{
    None,
    Server,
    Port,
    Database,
    User,
}

public sealed record ConnectionChoice(string Value, string Label);

/// <summary>Shown only while <paramref name="Keyword"/> holds one of <paramref name="Values"/> (its default when unset).</summary>
public sealed record ConnectionFieldCondition(string Keyword, IReadOnlyList<string> Values);

/// <param name="Keyword">The driver's canonical keyword, e.g. <c>Data Source</c> or <c>SSL Mode</c>.</param>
public sealed record ConnectionField(string Keyword, string Label, ConnectionFieldKind Kind)
{
    public ConnectionFieldRole Role { get; init; }

    /// <summary>Enforced only while the field is visible.</summary>
    public bool IsRequired { get; init; }

    /// <summary>Tucked away under "Advanced" in forms.</summary>
    public bool IsAdvanced { get; init; }

    public string? DefaultValue { get; init; }

    public string? Placeholder { get; init; }

    public string? Help { get; init; }

    public IReadOnlyList<ConnectionChoice> Choices { get; init; } = [];

    public IReadOnlyList<string> FileExtensions { get; init; } = [];

    public int? Minimum { get; init; }

    public int? Maximum { get; init; }

    public ConnectionFieldCondition? VisibleWhen { get; init; }
}

public sealed record ConnectionFieldError(string Keyword, string Message);

/// <param name="Values">Field values keyed by canonical keyword.</param>
/// <param name="AdditionalSettings">Every keyword that is not a field. Never holds a secret, because every secret keyword is a field.</param>
public sealed record ParsedConnectionString(IReadOnlyDictionary<string, string> Values, string AdditionalSettings);

/// <summary>
/// Describes an engine's connection string as form fields, and builds, parses and redacts it
/// through the driver's own <see cref="DbConnectionStringBuilder"/>, so keyword synonyms
/// (<c>pwd</c>, <c>uid</c>, <c>server</c>…) and value validation are the driver's.
/// </summary>
public sealed class ConnectionStringFormat
{
    private readonly Func<DbConnectionStringBuilder> _createBuilder;
    private readonly Dictionary<string, ConnectionField> _byKeyword;

    public ConnectionStringFormat(Func<DbConnectionStringBuilder> createBuilder, IReadOnlyList<ConnectionField> fields)
    {
        _createBuilder = createBuilder;
        Fields = fields;
        _byKeyword = fields.ToDictionary(f => f.Keyword, StringComparer.OrdinalIgnoreCase);
        SecretKeywords = fields.Where(f => f.Kind == ConnectionFieldKind.Secret).Select(f => f.Keyword).ToList();
        Defaults = fields.Where(f => f.DefaultValue is not null).ToDictionary(f => f.Keyword, f => f.DefaultValue!, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<ConnectionField> Fields { get; }

    public IReadOnlyList<string> SecretKeywords { get; }

    public IReadOnlyDictionary<string, string> Defaults { get; }

    public ConnectionField? FindField(string keyword) => _byKeyword.GetValueOrDefault(keyword);

    public bool IsVisible(ConnectionField field, IReadOnlyDictionary<string, string> values)
    {
        if (field.VisibleWhen is not { } condition)
        {
            return true;
        }

        var value = ValueOf(condition.Keyword, values);
        return condition.Values.Any(v => string.Equals(v, value, StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<ConnectionFieldError> Validate(IReadOnlyDictionary<string, string> values)
    {
        var errors = new List<ConnectionFieldError>();
        foreach (var field in Fields.Where(f => IsVisible(f, values)))
        {
            var value = values.GetValueOrDefault(field.Keyword)?.Trim() ?? "";
            if (value.Length == 0)
            {
                if (field.IsRequired)
                {
                    errors.Add(new(field.Keyword, $"{field.Label} is required."));
                }

                continue;
            }

            if (field.Kind == ConnectionFieldKind.Integer)
            {
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                {
                    errors.Add(new(field.Keyword, $"{field.Label} must be a whole number."));
                }
                else if (number < field.Minimum || number > field.Maximum)
                {
                    errors.Add(new(field.Keyword, $"{field.Label} must be between {field.Minimum} and {field.Maximum}."));
                }
            }
            else if (field.Kind == ConnectionFieldKind.Choice && field.Choices.Count > 0
                && !field.Choices.Any(c => string.Equals(c.Value, value, StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add(new(field.Keyword, $"{field.Label} must be one of {string.Join(", ", field.Choices.Select(c => c.Label))}."));
            }
        }

        return errors;
    }

    /// <summary>Builds the connection string from visible, non-empty field values on top of <paramref name="additionalSettings"/>.</summary>
    /// <exception cref="ArgumentException">The driver rejected a value or the additional settings.</exception>
    public string Build(IReadOnlyDictionary<string, string> values, string additionalSettings)
    {
        var builder = _createBuilder();
        if (!string.IsNullOrWhiteSpace(additionalSettings))
        {
            builder.ConnectionString = additionalSettings;
        }

        foreach (var field in Fields)
        {
            // A field's keyword in the additional settings never wins over the field itself.
            builder.Remove(field.Keyword);
        }

        foreach (var field in Fields.Where(f => IsVisible(f, values)))
        {
            var value = values.GetValueOrDefault(field.Keyword)?.Trim();
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            try
            {
                builder[field.Keyword] = value;
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidCastException)
            {
                throw new ArgumentException($"{field.Label}: {ex.Message}", field.Keyword, ex);
            }
        }

        return builder.ConnectionString;
    }

    /// <summary>Splits a connection string into field values and everything else.</summary>
    /// <exception cref="ArgumentException">The string is malformed or names a keyword the driver does not know.</exception>
    public ParsedConnectionString Parse(string connectionString)
    {
        var builder = _createBuilder();
        try
        {
            builder.ConnectionString = connectionString;
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or InvalidOperationException)
        {
            throw new ArgumentException(ex.Message, nameof(connectionString), ex);
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in Fields)
        {
            // Some drivers list every keyword they know; ShouldSerialize says which were set.
            if (builder.ShouldSerialize(field.Keyword))
            {
                values[field.Keyword] = Convert.ToString(builder[field.Keyword], CultureInfo.InvariantCulture) ?? "";
                builder.Remove(field.Keyword);
            }
        }

        return new ParsedConnectionString(values, builder.ConnectionString);
    }

    /// <summary>The connection string without any secret, for display and logs.</summary>
    public string Redact(string connectionString)
    {
        var builder = _createBuilder();
        try
        {
            builder.ConnectionString = connectionString;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidCastException or InvalidOperationException)
        {
            return "(unreadable connection string)";
        }

        foreach (var keyword in SecretKeywords)
        {
            builder.Remove(keyword);
        }

        return builder.ConnectionString;
    }

    /// <summary>A short human description such as <c>Shop on sql01 as migrator</c>. Never includes a secret.</summary>
    public string Describe(IReadOnlyDictionary<string, string> values)
    {
        string? Of(ConnectionFieldRole role)
        {
            var field = Fields.FirstOrDefault(f => f.Role == role && IsVisible(f, values));
            var value = field is null ? null : values.GetValueOrDefault(field.Keyword)?.Trim();
            return string.IsNullOrEmpty(value) ? null : value;
        }

        var server = Of(ConnectionFieldRole.Server);
        var port = Of(ConnectionFieldRole.Port);
        var database = Of(ConnectionFieldRole.Database);
        var user = Of(ConnectionFieldRole.User);
        var where = server is null ? null : port is null ? server : $"{server}:{port}";
        var text = (database, where) switch
        {
            ({ } d, { } w) => $"{d} on {w}",
            ({ } d, null) => d,
            (null, { } w) => w,
            _ => "(no server)",
        };
        return user is null ? text : $"{text} as {user}";
    }

    private string? ValueOf(string keyword, IReadOnlyDictionary<string, string> values)
    {
        var value = values.GetValueOrDefault(keyword);
        return string.IsNullOrWhiteSpace(value) ? Defaults.GetValueOrDefault(keyword) : value.Trim();
    }
}
