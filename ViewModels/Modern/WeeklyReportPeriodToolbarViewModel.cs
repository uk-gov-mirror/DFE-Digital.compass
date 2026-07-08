using Compass.Models;
using Compass.Services;
using Compass.ViewModels;

namespace Compass.ViewModels.Modern;

/// <summary>Shared period navigation and filters for modern weekly reporting pages.</summary>
public class WeeklyReportPeriodToolbarViewModel
{
    public string FormAction { get; set; } = "";
    public string IdPrefix { get; set; } = "wr";

    public int IsoYear { get; set; }
    public int IsoWeek { get; set; }
    public string PeriodLabel { get; set; } = "";

    public int? FilterBusinessAreaId { get; set; }
    public int? FilterDirectorateId { get; set; }

    public List<BusinessAreaLookup> BusinessAreas { get; set; } = new();
    public List<Division> Directorates { get; set; } = new();

    public List<WeeklyReportingPeriodInfo> RecentPeriods { get; set; } = new();

    public bool HasPreviousWeekNav { get; set; }
    public bool HasNextWeekNav { get; set; }
    public int? PreviousNavIsoYear { get; set; }
    public int? PreviousNavIsoWeek { get; set; }
    public int? NextNavIsoYear { get; set; }
    public int? NextNavIsoWeek { get; set; }

    public string? PeriodMeta { get; set; }

    public string FilterPeriodId => $"{IdPrefix}-filter-period";
    public string FilterBusinessAreaIdElement => $"{IdPrefix}-filter-business-area";
    public string FilterDirectorateIdElement => $"{IdPrefix}-filter-directorate";
    public string FilterApplyId => $"{IdPrefix}-filter-apply";

    public string PreviousNavUrl { get; set; } = "#";
    public string NextNavUrl { get; set; } = "#";

    public static WeeklyReportPeriodToolbarViewModel FromSubmissionProgress(
        ModernWeeklySubmissionProgressViewModel m,
        string formAction,
        Func<int?, int?, int?, int?, string> navUrlBuilder)
    {
        var periodMetaParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(m.SubmissionWindowDescription))
            periodMetaParts.Add(m.SubmissionWindowDescription);
        periodMetaParts.Add($"Submission window {m.SubmissionWindowStart:d MMM} – {m.SubmissionWindowEnd:d MMM yyyy}");

        return Create(
            m,
            formAction,
            navUrlBuilder,
            idPrefix: "wsp",
            periodMeta: periodMetaParts.Count == 0 ? null : " · " + string.Join(" · ", periodMetaParts));
    }

    public static WeeklyReportPeriodToolbarViewModel FromWeeklyReport(
        ModernWeeklyReportDashboardViewModel m,
        string formAction,
        Func<int?, int?, int?, int?, string> navUrlBuilder)
    {
        var periodMeta = m.MonthlyUpdateStats != null
            ? $" · Due {m.MonthlyUpdateStats.DueDate:d MMM yyyy}"
            : null;

        return Create(m, formAction, navUrlBuilder, idPrefix: "wr", periodMeta: periodMeta);
    }

    private static WeeklyReportPeriodToolbarViewModel Create(
        int isoYear,
        int isoWeek,
        string periodLabel,
        int? filterBusinessAreaId,
        int? filterDirectorateId,
        List<BusinessAreaLookup> businessAreas,
        List<Division> directorates,
        List<WeeklyReportingPeriodInfo> recentPeriods,
        bool hasPreviousWeekNav,
        bool hasNextWeekNav,
        int? previousNavIsoYear,
        int? previousNavIsoWeek,
        int? nextNavIsoYear,
        int? nextNavIsoWeek,
        string formAction,
        Func<int?, int?, int?, int?, string> navUrlBuilder,
        string idPrefix,
        string? periodMeta)
    {
        return new WeeklyReportPeriodToolbarViewModel
        {
            FormAction = formAction,
            IdPrefix = idPrefix,
            IsoYear = isoYear,
            IsoWeek = isoWeek,
            PeriodLabel = periodLabel,
            FilterBusinessAreaId = filterBusinessAreaId,
            FilterDirectorateId = filterDirectorateId,
            BusinessAreas = businessAreas,
            Directorates = directorates,
            RecentPeriods = recentPeriods,
            HasPreviousWeekNav = hasPreviousWeekNav,
            HasNextWeekNav = hasNextWeekNav,
            PreviousNavIsoYear = previousNavIsoYear,
            PreviousNavIsoWeek = previousNavIsoWeek,
            NextNavIsoYear = nextNavIsoYear,
            NextNavIsoWeek = nextNavIsoWeek,
            PeriodMeta = periodMeta,
            PreviousNavUrl = hasPreviousWeekNav && previousNavIsoYear.HasValue && previousNavIsoWeek.HasValue
                ? navUrlBuilder(previousNavIsoYear, previousNavIsoWeek, filterBusinessAreaId, filterDirectorateId)
                : "#",
            NextNavUrl = hasNextWeekNav && nextNavIsoYear.HasValue && nextNavIsoWeek.HasValue
                ? navUrlBuilder(nextNavIsoYear, nextNavIsoWeek, filterBusinessAreaId, filterDirectorateId)
                : "#"
        };
    }

    private static WeeklyReportPeriodToolbarViewModel Create(
        ModernWeeklySubmissionProgressViewModel m,
        string formAction,
        Func<int?, int?, int?, int?, string> navUrlBuilder,
        string idPrefix,
        string? periodMeta) =>
        Create(
            m.IsoYear, m.IsoWeek, m.PeriodLabel,
            m.FilterBusinessAreaId, m.FilterDirectorateId, m.BusinessAreas, m.Directorates, m.RecentPeriods,
            m.HasPreviousWeekNav, m.HasNextWeekNav,
            m.PreviousNavIsoYear, m.PreviousNavIsoWeek, m.NextNavIsoYear, m.NextNavIsoWeek,
            formAction, navUrlBuilder, idPrefix, periodMeta);

    private static WeeklyReportPeriodToolbarViewModel Create(
        ModernWeeklyReportDashboardViewModel m,
        string formAction,
        Func<int?, int?, int?, int?, string> navUrlBuilder,
        string idPrefix,
        string? periodMeta) =>
        Create(
            m.IsoYear, m.IsoWeek, m.PeriodLabel,
            m.FilterBusinessAreaId, m.FilterDirectorateId, m.BusinessAreas, m.Directorates, m.RecentPeriods,
            m.HasPreviousWeekNav, m.HasNextWeekNav,
            m.PreviousNavIsoYear, m.PreviousNavIsoWeek, m.NextNavIsoYear, m.NextNavIsoWeek,
            formAction, navUrlBuilder, idPrefix, periodMeta);
}
