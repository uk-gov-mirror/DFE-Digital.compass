using System.ComponentModel.DataAnnotations;

namespace Compass.Models;

/// <summary>Singleton admin configuration for weekly work reporting cycles.</summary>
public class WeeklyWorkReportingConfig
{
    public int Id { get; set; } = 1;

    /// <summary>First day of the reporting period (typically Monday).</summary>
    public DayOfWeek PeriodStartDayOfWeek { get; set; } = DayOfWeek.Monday;

    /// <summary>Last day of the reporting period (typically Friday).</summary>
    public DayOfWeek PeriodEndDayOfWeek { get; set; } = DayOfWeek.Friday;

    /// <summary>Day of week submissions are due.</summary>
    public DayOfWeek DueDayOfWeek { get; set; } = DayOfWeek.Friday;

    /// <summary>Whether due date is in the same week as the period or the following week.</summary>
    public WeeklyWorkReportingDueWeekOffset DueWeekOffset { get; set; } = WeeklyWorkReportingDueWeekOffset.SameWeek;

    /// <summary>First reporting period start date (must match <see cref="PeriodStartDayOfWeek"/>). Earlier weeks are excluded.</summary>
    public DateTime FirstReportingPeriodStart { get; set; } = new(2026, 6, 29, 0, 0, 0, DateTimeKind.Utc);

    public bool IsActive { get; set; } = true;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
