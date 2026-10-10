using System.Globalization;
using System.Text;
using DbMigrator.Core.Providers;

namespace DbMigrator.Core.Planning;

public static class IdentifierNaming
{
    public static string ApplyCase(string name, IdentifierCase identifierCase) => identifierCase switch
    {
        IdentifierCase.Preserve => name,
        IdentifierCase.Lower => name.ToLowerInvariant(),
        IdentifierCase.SnakeCase => ToSnakeCase(name),
        _ => throw new ArgumentOutOfRangeException(nameof(identifierCase), identifierCase, null),
    };

    /// <summary>
    /// <c>OrderID</c> → <c>order_id</c>, <c>HTMLParser</c> → <c>html_parser</c>,
    /// <c>Order Details</c> → <c>order_details</c>. Underscores already present are kept.
    /// </summary>
    public static string ToSnakeCase(string name)
    {
        var result = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0 && result.Length > 0 && result[^1] != '_')
                {
                    var previous = name[i - 1];
                    var nextIsLower = i + 1 < name.Length && char.IsLower(name[i + 1]);
                    if (char.IsLower(previous) || char.IsDigit(previous) || (char.IsUpper(previous) && nextIsLower))
                    {
                        result.Append('_');
                    }
                }

                result.Append(char.ToLowerInvariant(c));
            }
            else if (char.IsLetterOrDigit(c) || c == '_')
            {
                result.Append(c);
            }
            else if (result.Length > 0 && result[^1] != '_')
            {
                // Spaces, hyphens and other punctuation become a single separator.
                result.Append('_');
            }
        }

        if (!name.EndsWith('_'))
        {
            while (result.Length > 0 && result[^1] == '_')
            {
                result.Length--;
            }
        }

        return result.Length == 0 ? name : result.ToString();
    }

    /// <summary>
    /// Returns <paramref name="name"/> if it fits <paramref name="limit"/>; otherwise a prefix
    /// followed by a hash of the full name, so distinct long names stay distinct.
    /// </summary>
    public static string Shorten(string name, IdentifierLimit limit)
    {
        if (limit.Fits(name))
        {
            return name;
        }

        var hash = "_" + Fnv1a(name).ToString("x8", CultureInfo.InvariantCulture);
        return TakePrefix(name, limit.MaxLength - limit.Measure(hash), limit) + hash;
    }

    private static string TakePrefix(string name, int budget, IdentifierLimit limit)
    {
        var end = 0;
        var used = 0;
        while (end < name.Length)
        {
            var length = char.IsHighSurrogate(name[end]) && end + 1 < name.Length ? 2 : 1;
            var size = limit.Measure(name.Substring(end, length));
            if (used + size > budget)
            {
                break;
            }

            used += size;
            end += length;
        }

        return name[..end];
    }

    private static uint Fnv1a(string text)
    {
        var hash = 2166136261u;
        foreach (var b in Encoding.UTF8.GetBytes(text))
        {
            hash = (hash ^ b) * 16777619u;
        }

        return hash;
    }
}

/// <summary>Hands out names that are unique within one namespace, adding <c>_1</c>, <c>_2</c>… on clashes.</summary>
public sealed class NameScope(IdentifierLimit limit, StringComparer comparer)
{
    private readonly HashSet<string> _taken = new(comparer);

    public void Reserve(string name) => _taken.Add(name);

    public string Allocate(string preferred)
    {
        var candidate = IdentifierNaming.Shorten(preferred, limit);
        for (var n = 1; !_taken.Add(candidate); n++)
        {
            var suffix = "_" + n.ToString(CultureInfo.InvariantCulture);
            var room = limit with { MaxLength = limit.MaxLength - limit.Measure(suffix) };
            candidate = IdentifierNaming.Shorten(preferred, room) + suffix;
        }

        return candidate;
    }
}
