using System.Globalization;
using Compass.Data;
using Compass.Models;
using Microsoft.EntityFrameworkCore;

namespace Compass.Services;

public class WeeklyUpdateService : IWeeklyUpdateService
{
    private readonly CompassDbContext _context;
    private WeeklyWorkReportingConfig? _cachedConfig;

    public WeeklyUpdateService(CompassDbContext context)
    {
        _context = context;
    }

    public async Task<WeeklyWorkReportingConfig> GetOrCreateConfigAsync(CancellationToken cancellationToken = default)
    {
        if (_cachedConfig != null)
            return _cachedConfig;

        var config = await _context.WeeklyWorkReportingConfigs.FirstOrDefaultAsync(cancellationToken);
        if (config != null)
        {
            _cachedConfig = config;
            return config;
        }

        config = new WeeklyWorkReportingConfig();
        _context.WeeklyWorkReportingConfigs.Add(config);
        await _context.SaveChangesAsync(cancellationToken);
        _cachedConfig = config;
        return config;
    }

    public async Task<bool> IsProjectInWeeklyReportingScopeAsync(int projectId, CancellationToken cancellationToken = default)
    {
        var config = await GetOrCreateConfigAsync(cancellationToken);
        if (!config.IsActive)
            return false;

        return await _context.WeeklyWorkReportingScopeProjects.AsNoTracking()
            .AnyAsync(x => x.ProjectId == projectId, cancellationToken);
    }

    public WeeklyReportingPeriodInfo? TryGetReportingPeriod(int isoYear, int isoWeek)
    {
        var config = GetConfigSync();
        if (config == null || !config.IsActive)
            return null;

        var period = BuildPeriodInfo(config, isoYear, isoWeek);
        return IsPeriodOnOrAfterFirstReportingWeek(config, period) ? period : null;
    }

    public WeeklyReportingPeriodInfo? TryGetReportingPeriodForDate(DateTime date)
    {
        var config = GetConfigSync();
        if (config == null || !config.IsActive)
            return null;

        var isoYear = ISOWeek.GetYear(date);
        var isoWeek = ISOWeek.GetWeekOfYear(date);
        return BuildPeriodInfo(config, isoYear, isoWeek);
    }

    public UpdateSubmissionStatus CalculateUpdateStatus(int isoYear, int isoWeek, DateTime? submittedDate)
    {
        if (submittedDate.HasValue)
            return UpdateSubmissionStatus.Submitted;

        var period = TryGetReportingPeriod(isoYear, isoWeek);
        if (period == null)
            return UpdateSubmissionStatus.Upcoming;

        var nowDate = DateTime.UtcNow.Date;
        if (nowDate < period.SubmissionOpens.Date)
            return UpdateSubmissionStatus.Upcoming;
        if (nowDate > period.SubmissionCloses.Date)
            return UpdateSubmissionStatus.Late;
        return UpdateSubmissionStatus.Due;
    }

    public bool IsWeeklyReportEditingAllowed(int isoYear, int isoWeek)
    {
        var period = TryGetReportingPeriod(isoYear, isoWeek);
        if (period == null)
            return false;

        var nowDate = DateTime.UtcNow.Date;
        if (nowDate >= period.SubmissionOpens.Date && nowDate <= period.SubmissionCloses.Date)
            return true;

        if (nowDate > period.SubmissionCloses.Date)
        {
            var next = TryGetReportingPeriod(isoYear, isoWeek + 1)
                       ?? TryGetReportingPeriod(isoYear + 1, 1);
            if (next == null)
                return true;
            return nowDate < next.SubmissionOpens.Date;
        }

        return false;
    }

    public DateTime GetSubmissionWindowOpens(int isoYear, int isoWeek)
    {
        var period = TryGetReportingPeriod(isoYear, isoWeek);
        return period?.SubmissionOpens.Date ?? DateTime.UtcNow.Date;
    }

    public DateTime GetSubmissionWindowCloses(int isoYear, int isoWeek)
    {
        var period = TryGetReportingPeriod(isoYear, isoWeek);
        return period?.SubmissionCloses.Date ?? DateTime.UtcNow.Date;
    }

    public DateTime GetWeeklyUpdateDueDate(int isoYear, int isoWeek) =>
        GetSubmissionWindowCloses(isoYear, isoWeek);

    public (int IsoYear, int IsoWeek) ResolveDashboardReportingPeriod(DateTime utcNow)
    {
        var nowDate = utcNow.Date;
        var config = GetConfigSync();
        if (config != null && config.IsActive && nowDate < config.FirstReportingPeriodStart.Date)
        {
            var firstYear = ISOWeek.GetYear(config.FirstReportingPeriodStart);
            var firstWeek = ISOWeek.GetWeekOfYear(config.FirstReportingPeriodStart);
            return (firstYear, firstWeek);
        }

        var currentYear = ISOWeek.GetYear(nowDate);
        var currentWeek = ISOWeek.GetWeekOfYear(nowDate);

        if (IsWeeklyReportEditingAllowed(currentYear, currentWeek))
            return (currentYear, currentWeek);

        var prevDate = nowDate.AddDays(-7);
        var prevYear = ISOWeek.GetYear(prevDate);
        var prevWeek = ISOWeek.GetWeekOfYear(prevDate);
        if (IsWeeklyReportEditingAllowed(prevYear, prevWeek))
            return (prevYear, prevWeek);

        var period = TryGetReportingPeriod(currentYear, currentWeek);
        if (period != null && nowDate <= period.DueDate.Date)
            return (currentYear, currentWeek);

        return (prevYear, prevWeek);
    }

    public IEnumerable<WeeklyReportingPeriodInfo> EnumerateRecentPeriods(DateTime utcNow, int count)
    {
        if (count <= 0)
            yield break;

        var (startYear, startWeek) = ResolveDashboardReportingPeriod(utcNow);
        var year = startYear;
        var week = startWeek;

        var yielded = 0;
        while (yielded < count)
        {
            var period = TryGetReportingPeriod(year, week);
            if (period == null)
                yield break;

            yield return period;
            yielded++;

            var anchor = ISOWeek.ToDateTime(year, week, DayOfWeek.Monday).AddDays(-7);
            year = ISOWeek.GetYear(anchor);
            week = ISOWeek.GetWeekOfYear(anchor);
        }
    }

    public bool TryParsePeriodKey(string periodKey, out int isoYear, out int isoWeek)
    {
        isoYear = 0;
        isoWeek = 0;
        if (string.IsNullOrWhiteSpace(periodKey))
            return false;

        var parts = periodKey.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
            return false;
        if (!parts[1].StartsWith("W", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!int.TryParse(parts[0], out isoYear))
            return false;
        if (!int.TryParse(parts[1][1..], out isoWeek))
            return false;
        return isoWeek is >= 1 and <= 53;
    }

    public string BuildPeriodKey(int isoYear, int isoWeek) => $"{isoYear}-W{isoWeek:D2}";

    private WeeklyWorkReportingConfig? GetConfigSync()
    {
        if (_cachedConfig != null)
            return _cachedConfig;

        _cachedConfig = _context.WeeklyWorkReportingConfigs.AsNoTracking().FirstOrDefault();
        return _cachedConfig;
    }

    private WeeklyReportingPeriodInfo BuildPeriodInfo(WeeklyWorkReportingConfig config, int isoYear, int isoWeek)
    {
        var weekMonday = ISOWeek.ToDateTime(isoYear, isoWeek, DayOfWeek.Monday);
        var periodStart = AddDaysFromMonday(weekMonday, config.PeriodStartDayOfWeek);
        var periodEnd = AddDaysFromMonday(weekMonday, config.PeriodEndDayOfWeek);
        if (periodEnd < periodStart)
            periodEnd = periodEnd.AddDays(7);

        var dueWeekMonday = weekMonday.AddDays((int)config.DueWeekOffset * 7);
        var dueDate = AddDaysFromMonday(dueWeekMonday, config.DueDayOfWeek);

        return new WeeklyReportingPeriodInfo
        {
            IsoYear = isoYear,
            IsoWeek = isoWeek,
            PeriodKey = BuildPeriodKey(isoYear, isoWeek),
            PeriodLabel = FormatPeriodLabel(periodStart, periodEnd),
            PeriodStart = periodStart.Date,
            PeriodEnd = periodEnd.Date,
            SubmissionOpens = periodStart.Date,
            SubmissionCloses = dueDate.Date,
            DueDate = dueDate.Date
        };
    }

    private static DateTime AddDaysFromMonday(DateTime weekMonday, DayOfWeek targetDay)
    {
        var offset = ((int)targetDay - (int)DayOfWeek.Monday + 7) % 7;
        return weekMonday.AddDays(offset);
    }

    private static bool IsPeriodOnOrAfterFirstReportingWeek(
        WeeklyWorkReportingConfig config,
        WeeklyReportingPeriodInfo period) =>
        period.PeriodStart.Date >= config.FirstReportingPeriodStart.Date;

    public static string FormatPeriodLabel(DateTime periodStart, DateTime periodEnd)
    {
        var culture = CultureInfo.GetCultureInfo("en-GB");
        var start = periodStart.ToString("d MMM", culture);
        var end = periodEnd.ToString(
            periodStart.Year == periodEnd.Year ? "d MMMM" : "d MMMM yyyy",
            culture);
        return $"{start} to {end}";
    }
}
