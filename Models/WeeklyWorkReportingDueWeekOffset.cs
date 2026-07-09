using System.ComponentModel.DataAnnotations;

namespace Compass.Models;

/// <summary>Which week the submission due date falls in, relative to the reporting period week.</summary>
public enum WeeklyWorkReportingDueWeekOffset
{
    [Display(Name = "Same week as the reporting period")]
    SameWeek = 0,

    [Display(Name = "Week after the reporting period")]
    WeekAfter = 1,
}
