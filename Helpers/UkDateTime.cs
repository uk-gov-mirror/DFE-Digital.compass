namespace Compass.Helpers;

/// <summary>UK civil time (Europe/London / GMT Standard Time), including BST.</summary>
public static class UkDateTime
{
    public static TimeZoneInfo TimeZone =>
        TimeZoneInfo.FindSystemTimeZoneById(
            OperatingSystem.IsWindows() ? "GMT Standard Time" : "Europe/London");

    public static DateTime Now() =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZone);

    public static DateTime ToUk(DateTime utc)
    {
        var utcValue = utc.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(utc, DateTimeKind.Utc)
            : utc.ToUniversalTime();
        return TimeZoneInfo.ConvertTimeFromUtc(utcValue, TimeZone);
    }

    public static DateTime ToUtc(DateTime ukUnspecified)
    {
        var unspecified = DateTime.SpecifyKind(ukUnspecified, DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(unspecified, TimeZone);
    }

    /// <summary>UTC instant for the start of the given UK calendar day.</summary>
    public static DateTime StartOfUkDateUtc(DateTime ukDate) =>
        ToUtc(ukDate.Date);

    /// <summary>Inclusive start and exclusive end (UTC) of today's UK calendar day.</summary>
    public static (DateTime StartUtc, DateTime EndUtc) TodayRangeUtc()
    {
        var startUk = Now().Date;
        return (ToUtc(startUk), ToUtc(startUk.AddDays(1)));
    }

    /// <summary>Example: 18 August 2026 at 6:02am (UK).</summary>
    public static string FormatUk(DateTime utc)
    {
        var uk = ToUk(utc);
        var time = uk.ToString("h:mmtt", System.Globalization.CultureInfo.GetCultureInfo("en-GB"))
            .ToLowerInvariant();
        return $"{uk:d MMMM yyyy} at {time} (UK)";
    }
}
