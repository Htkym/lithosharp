using System.Globalization;
using PageSharp.Configuration;

namespace PageSharp;

/// <summary>
/// A shared helper that formats dates using the time zone from the site settings.
/// </summary>
public static class SiteFormatting
{
    /// <summary>Formats a date for display as "yyyy-MM-dd HH:mm LABEL".</summary>
    /// <param name="site">Site settings.</param>
    /// <param name="value">The date to format.</param>
    /// <returns>The formatted string.</returns>
    public static string FormatDateTime(SiteSettings site, DateTimeOffset value)
    {
        ArgumentNullException.ThrowIfNull(site);
        var timeZone = ResolveTimeZone(site.TimeZone);
        var siteTime = TimeZoneInfo.ConvertTime(value, timeZone);
        return $"{siteTime:yyyy-MM-dd HH:mm} {GetTimeZoneLabel(site.TimeZone, timeZone.BaseUtcOffset)}";
    }

    /// <summary>Converts the date to the site time zone and formats it as "yyyy/MM".</summary>
    /// <param name="site">Site settings.</param>
    /// <param name="value">The date to format.</param>
    /// <returns>The formatted year and month.</returns>
    public static string FormatMonth(SiteSettings site, DateTimeOffset value) =>
        ToSiteTime(site, value).ToString("yyyy/MM", CultureInfo.InvariantCulture);

    /// <summary>Converts the date to the site time zone.</summary>
    /// <param name="site">Site settings.</param>
    /// <param name="value">The date to convert.</param>
    /// <returns>The date in the site time zone.</returns>
    public static DateTimeOffset ToSiteTime(SiteSettings site, DateTimeOffset value)
    {
        ArgumentNullException.ThrowIfNull(site);
        return TimeZoneInfo.ConvertTime(value, ResolveTimeZone(site.TimeZone));
    }

    private static string GetTimeZoneLabel(string? timeZoneId, TimeSpan offset)
    {
        if (timeZoneId is "Asia/Tokyo" or "Tokyo Standard Time")
        {
            return "JST";
        }

        var sign = offset < TimeSpan.Zero ? "-" : "+";
        var absolute = offset.Duration();
        return $"UTC{sign}{absolute:hh\\:mm}";
    }

    private static TimeZoneInfo ResolveTimeZone(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            return TimeZoneInfo.Utc;
        }

        if (TryFindTimeZone(timeZoneId, out var timeZone))
        {
            return timeZone;
        }

        if (TimeZoneInfo.TryConvertIanaIdToWindowsId(timeZoneId, out var windowsId) &&
            TryFindTimeZone(windowsId, out timeZone))
        {
            return timeZone;
        }

        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(timeZoneId, out var ianaId) &&
            TryFindTimeZone(ianaId, out timeZone))
        {
            return timeZone;
        }

        return TimeZoneInfo.Utc;
    }

    private static bool TryFindTimeZone(string timeZoneId, out TimeZoneInfo timeZone)
    {
        try
        {
            timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
        }
        catch (InvalidTimeZoneException)
        {
        }

        timeZone = TimeZoneInfo.Utc;
        return false;
    }
}
