using Compass.Models;

namespace Compass.Services;

public interface IWeeklyUpdateService
{
    Task<WeeklyWorkReportingConfig> GetOrCreateConfigAsync(CancellationToken cancellationToken = default);

    Task<bool> IsProjectInWeeklyReportingScopeAsync(int projectId, CancellationToken cancellationToken = default);

    WeeklyReportingPeriodInfo? TryGetReportingPeriod(int isoYear, int isoWeek);

    WeeklyReportingPeriodInfo? TryGetReportingPeriodForDate(DateTime date);

    UpdateSubmissionStatus CalculateUpdateStatus(int isoYear, int isoWeek, DateTime? submittedDate);

    bool IsWeeklyReportEditingAllowed(int isoYear, int isoWeek);

    DateTime GetSubmissionWindowOpens(int isoYear, int isoWeek);

    DateTime GetSubmissionWindowCloses(int isoYear, int isoWeek);

    DateTime GetWeeklyUpdateDueDate(int isoYear, int isoWeek);

    (int IsoYear, int IsoWeek) ResolveDashboardReportingPeriod(DateTime utcNow);

    IEnumerable<WeeklyReportingPeriodInfo> EnumerateRecentPeriods(DateTime utcNow, int count);

    bool TryParsePeriodKey(string periodKey, out int isoYear, out int isoWeek);

    string BuildPeriodKey(int isoYear, int isoWeek);
}

public sealed class WeeklyReportingPeriodInfo
{
    public int IsoYear { get; init; }
    public int IsoWeek { get; init; }
    public string PeriodKey { get; init; } = string.Empty;
    public string PeriodLabel { get; init; } = string.Empty;
    public DateTime PeriodStart { get; init; }
    public DateTime PeriodEnd { get; init; }
    public DateTime SubmissionOpens { get; init; }
    public DateTime SubmissionCloses { get; init; }
    public DateTime DueDate { get; init; }
}
