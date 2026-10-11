using System.Globalization;

namespace DbMigrator.Desktop.ViewModels;

/// <summary>Short, readable numbers and durations for the screens.</summary>
public static class Humanize
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    /// <summary>1,234 → "1,234"; 61,234,567 → "61.2 M"; 4,500,000,000 → "4.5 B".</summary>
    public static string Count(long value) => Math.Abs(value) switch
    {
        >= 1_000_000_000 => (value / 1_000_000_000d).ToString("0.#", Culture) + " B",
        >= 10_000_000 => (value / 1_000_000d).ToString("0", Culture) + " M",
        >= 1_000_000 => (value / 1_000_000d).ToString("0.#", Culture) + " M",
        _ => value.ToString("N0", Culture),
    };

    /// <summary>182,345.6 → "182 k".</summary>
    public static string Rate(double perSecond) => perSecond switch
    {
        >= 1_000_000 => (perSecond / 1_000_000).ToString("0.#", Culture) + " M",
        >= 10_000 => (perSecond / 1_000).ToString("0", Culture) + " k",
        _ => perSecond.ToString("N0", Culture),
    };

    /// <summary>"0:21:14" for long runs, "12.3s" for short ones.</summary>
    public static string Duration(TimeSpan elapsed) =>
        elapsed.TotalSeconds < 60
            ? elapsed.TotalSeconds.ToString("0.0", Culture) + "s"
            : ((int)elapsed.TotalHours).ToString(Culture) + elapsed.ToString(@"\:mm\:ss", Culture);

    /// <summary>"about 35 min", "about 2 h 10 min", "less than a minute".</summary>
    public static string Remaining(TimeSpan remaining) => remaining.TotalMinutes switch
    {
        < 1 => "less than a minute",
        < 90 => $"about {Math.Ceiling(remaining.TotalMinutes).ToString(Culture)} min",
        _ => $"about {((int)remaining.TotalHours).ToString(Culture)} h {remaining.Minutes.ToString(Culture)} min",
    };

    public static string Plural(long count, string singular, string? plural = null) =>
        $"{count.ToString("N0", Culture)} {(count == 1 ? singular : plural ?? singular + "s")}";
}
