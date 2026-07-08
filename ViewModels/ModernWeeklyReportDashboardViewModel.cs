using Compass.Services;

namespace Compass.ViewModels;

/// <summary>Data for the modern weekly reporting dashboard (<c>/modern/reporting/weekly-update</c>).</summary>
public class ModernWeeklyReportDashboardViewModel : ModernMonthlyReportDashboardViewModel
{
    public int IsoYear { get; set; }
    public int IsoWeek { get; set; }
    public string PeriodLabel { get; set; } = "";

    public int DefaultIsoYear { get; set; }
    public int DefaultIsoWeek { get; set; }

    public bool HasPreviousWeekNav { get; set; }
    public bool HasNextWeekNav { get; set; }
    public int? PreviousNavIsoYear { get; set; }
    public int? PreviousNavIsoWeek { get; set; }
    public int? NextNavIsoYear { get; set; }
    public int? NextNavIsoWeek { get; set; }

    public List<WeeklyReportingPeriodInfo> RecentPeriods { get; set; } = new();
}
