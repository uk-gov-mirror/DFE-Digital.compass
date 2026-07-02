using System.Globalization;
using Compass.Controllers;
using Compass.Models;
using Compass.Services.Aiss;
using Compass.ViewModels;
using Microsoft.EntityFrameworkCore;

namespace Compass.Services;

public partial class ModernMonthlyReportService
{
    public async Task<ModernWeeklyReportDashboardViewModel> BuildWeeklyDashboardAsync(
        int? isoYear,
        int? isoWeek,
        int? businessAreaId,
        int? directorateId,
        CancellationToken cancellationToken = default)
    {
        var utcNow = DateTime.UtcNow;
        var (defaultIsoYear, defaultIsoWeek) = _weeklyUpdateService.ResolveDashboardReportingPeriod(utcNow);
        var (reportIsoYear, reportIsoWeek, period) = ResolveWeeklyReportingPeriod(isoYear, isoWeek, defaultIsoYear, defaultIsoWeek);
        if (period == null)
            throw new InvalidOperationException("Weekly reporting period is not configured or not available.");

        var periodStart = period.PeriodStart;
        var periodEnd = period.PeriodEnd.Date.AddHours(23).AddMinutes(59).AddSeconds(59);
        var todayUtc = utcNow.Date;
        var upcomingWindowEnd = periodStart.AddDays(30);

        var allProjects = await LoadWeeklyScopedProjectsAsync(
            businessAreaId,
            directorateId,
            query => query
                .Include(p => p.PrimaryContactUser)
                .Include(p => p.DeliveryPriority)
                .Include(p => p.BusinessAreaLookup)
                .Include(p => p.Milestones)
                .Include(p => p.WeeklyWorkUpdates)
                .Include(p => p.RagStatusLookup)
                .Include(p => p.RagHistory)
                .Include(p => p.Directorates),
            cancellationToken);

        var businessAreas = await LoadActiveBusinessAreasAsync(cancellationToken);
        var directorates = await LoadActiveDirectoratesAsync(cancellationToken);

        var newProjectsThisPeriod = allProjects
            .Where(p => p.CreatedAt >= periodStart && p.CreatedAt <= periodEnd)
            .OrderByDescending(p => p.CreatedAt)
            .ToList();

        var milestonesAchieved = allProjects
            .SelectMany(p => p.Milestones
                .Where(m => !m.IsDeleted &&
                            m.Status == "complete" &&
                            m.ActualDate.HasValue &&
                            m.ActualDate.Value >= periodStart &&
                            m.ActualDate.Value <= periodEnd)
                .Select(m => new MilestoneWithProject { Project = p, Milestone = m }))
            .OrderBy(x => x.Milestone.ActualDate)
            .ToList();

        var upcomingMilestones30 = allProjects
            .SelectMany(p => p.Milestones
                .Where(m => !m.IsDeleted &&
                            m.Status != "complete" &&
                            m.Status != "cancelled" &&
                            m.DueDate >= periodStart &&
                            m.DueDate < upcomingWindowEnd)
                .Select(m => new MilestoneWithProject { Project = p, Milestone = m }))
            .OrderBy(x => x.Milestone.DueDate)
            .ToList();

        var lateMilestones = allProjects
            .SelectMany(p => p.Milestones
                .Where(m => !m.IsDeleted &&
                            m.Status != "complete" &&
                            m.Status != "cancelled" &&
                            m.DueDate.Date < todayUtc)
                .Select(m => new MilestoneWithProject { Project = p, Milestone = m }))
            .OrderBy(x => x.Milestone.DueDate)
            .ToList();

        var dueDateForSubmission = _weeklyUpdateService.GetWeeklyUpdateDueDate(reportIsoYear, reportIsoWeek);
        var nowUtcForSubmission = DateTime.UtcNow;
        var weeklyUpdateStats = CalculateWeeklyUpdateStats(allProjects, reportIsoYear, reportIsoWeek, dueDateForSubmission);

        var ragDistribution = BuildRagDistribution(allProjects);
        var priorityDistribution = BuildPriorityDistribution(allProjects);

        var businessAreaRows = BuildWeeklyBusinessAreaDashboardRows(
            allProjects,
            reportIsoYear,
            reportIsoWeek,
            periodStart,
            periodEnd,
            upcomingWindowEnd,
            todayUtc,
            nowUtcForSubmission,
            dueDateForSubmission);

        var businessAreaSubmissionProgress = businessAreaRows
            .Select(r => new BusinessAreaSubmissionProgressRow
            {
                BusinessArea = r.BusinessArea,
                BusinessAreaId = r.BusinessAreaId,
                TotalToReport = r.TotalProjects,
                Submitted = r.SubmittedCount,
                InProgress = r.InProgressCount,
                Late = r.LateCount,
                NotStarted = r.NotStartedCount,
                CompletionRatePercent = r.CompletionRatePercent
            })
            .ToList();

        var projectsWithPathToGreen = allProjects
            .Where(p => !string.IsNullOrWhiteSpace(p.PathToGreen) &&
                        RagBucket(p) != "Green" &&
                        !string.IsNullOrWhiteSpace(NormalizeRagStatus(p.RagStatusLookup?.Name ?? p.RagStatus)))
            .OrderBy(p =>
            {
                var rag = RagBucket(p);
                return rag switch
                {
                    "Red" => 1,
                    "Amber-Red" => 2,
                    "Not Set" => 3,
                    "Amber-Green" => 4,
                    _ => 99
                };
            })
            .ThenBy(p => p.BusinessAreaLookup?.Name ?? "ZZZ")
            .ThenBy(p => p.Title)
            .ToList();

        var ragOrder = new[] { "Red", "Amber-Red", "Amber-Green", "Green", "Not Set" };
        var priOrder = new[] { "Not Set", "Low", "Medium", "High", "Critical" };
        var matrix = new List<RagPriorityMatrixCell>();
        foreach (var r in ragOrder)
        {
            foreach (var pr in priOrder)
            {
                var c = allProjects.Count(p => RagBucket(p) == r && PriorityBucket(p) == pr);
                matrix.Add(new RagPriorityMatrixCell { Rag = r, Priority = pr, Count = c });
            }
        }

        var (prevIsoYear, prevIsoWeek, _) = GetAdjacentWeeklyPeriod(reportIsoYear, reportIsoWeek, -1);
        var prevPeriod = _weeklyUpdateService.TryGetReportingPeriod(prevIsoYear, prevIsoWeek);
        var prevPeriodName = prevPeriod?.PeriodLabel ?? "Previous period";

        var prevProjects = await LoadWeeklyScopedProjectsAsync(
            businessAreaId,
            directorateId,
            query => query
                .Include(p => p.WeeklyWorkUpdates)
                .Include(p => p.RagStatusLookup)
                .Include(p => p.DeliveryPriority)
                .Include(p => p.Directorates),
            cancellationToken);

        var prevPeriodRagDistribution = await BuildPrevMonthRagDistributionAsync(prevProjects, periodStart, cancellationToken);
        var prevPeriodPriorityDistribution = BuildPriorityDistribution(prevProjects);

        var allowedProjectIds = allProjects.Select(p => p.Id).ToHashSet();

        var ragHistoryDuringPeriod = await _db.ProjectRagHistories
            .AsNoTracking()
            .Include(rh => rh.RagStatusLookup)
            .Where(rh => rh.ChangedAt >= periodStart && rh.ChangedAt <= periodEnd && allowedProjectIds.Contains(rh.ProjectId))
            .ToListAsync(cancellationToken);

        var projectsWithRagChange = ragHistoryDuringPeriod.Select(rh => rh.ProjectId).Distinct().Count();

        var projectsWithPriorityChange = allProjects
            .Where(p => p.UpdatedAt >= periodStart &&
                        p.UpdatedAt <= periodEnd &&
                        prevProjects.Any(pp => pp.Id == p.Id &&
                            ((pp.DeliveryPriorityId == null && p.DeliveryPriorityId != null) ||
                             (pp.DeliveryPriorityId != null && p.DeliveryPriorityId == null) ||
                             (pp.DeliveryPriorityId != p.DeliveryPriorityId))))
            .Count();

        var projectIds = allProjects.Select(p => p.Id).ToList();
        var ragHistoriesForProjects = await _db.ProjectRagHistories
            .AsNoTracking()
            .Include(rh => rh.RagStatusLookup)
            .Where(rh => projectIds.Contains(rh.ProjectId))
            .OrderByDescending(rh => rh.ChangedAt)
            .ToListAsync(cancellationToken);

        var historyByProject = ragHistoriesForProjects
            .GroupBy(rh => rh.ProjectId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.ChangedAt).ToList());

        var recentPeriods = _weeklyUpdateService.EnumerateRecentPeriods(utcNow, 12).ToList();
        var ragTrend = BuildWeeklyRagTrend(allProjects, historyByProject, recentPeriods, reportIsoYear, reportIsoWeek);
        var priorityTrend = BuildWeeklyPriorityTrend(allProjects, recentPeriods, reportIsoYear, reportIsoWeek);

        var ragChanges = BuildRagChangeDetails(allProjects, ragHistoryDuringPeriod, historyByProject, periodStart, reportIsoYear, reportIsoWeek);
        var priorityChanges = BuildPriorityChangeDetails(allProjects, prevProjects, periodStart, periodEnd, reportIsoYear, reportIsoWeek);

        var ragSixWeekTrendRows = BuildWeeklyRagSixWeekTrendRows(
            allProjects,
            historyByProject,
            reportIsoYear,
            reportIsoWeek);

        var (hasPreviousWeekNav, previousNavIsoYear, previousNavIsoWeek) =
            GetWeeklyNavPrevious(reportIsoYear, reportIsoWeek);
        var (hasNextWeekNav, nextNavIsoYear, nextNavIsoWeek) =
            GetWeeklyNavNext(reportIsoYear, reportIsoWeek, defaultIsoYear, defaultIsoWeek);

        AissPlatformSummary? accessibilitySummary = null;
        string? accessibilityError = null;
        IReadOnlyList<AissByBusinessAreaRow> accessibilityAreaRows = Array.Empty<AissByBusinessAreaRow>();
        try
        {
            var (acc, accErr) = await _aissSummary.GetSummaryAsync(cancellationToken);
            accessibilitySummary = acc;
            accessibilityError = accErr;
            if (acc?.ByBusinessArea is { Count: > 0 } baList)
            {
                if (businessAreaId is int fbaid)
                {
                    var compBaName = businessAreas.FirstOrDefault(b => b.Id == fbaid)?.Name;
                    if (!string.IsNullOrWhiteSpace(compBaName))
                    {
                        var n = compBaName.Trim();
                        var match = baList.FirstOrDefault(r =>
                            string.Equals((r.BusinessArea ?? "").Trim(), n, StringComparison.OrdinalIgnoreCase));
                        accessibilityAreaRows = match is null
                            ? Array.Empty<AissByBusinessAreaRow>()
                            : new[] { match };
                    }
                }
                else
                {
                    accessibilityAreaRows = baList;
                }
            }
        }
        catch
        {
            if (string.IsNullOrEmpty(accessibilityError))
                accessibilityError = "Accessibility data could not be loaded.";
        }

        AissIssueCriteriaBlock? accessibilityIssueCriteria = null;
        if (accessibilitySummary is { } forCriteria)
        {
            if (businessAreaId is null)
                accessibilityIssueCriteria = forCriteria.IssueCriteria;
            else if (accessibilityAreaRows.Count == 1)
                accessibilityIssueCriteria = accessibilityAreaRows[0].IssueCriteria;
        }

        var raidSummary = await BuildRaidSummaryAsync(businessAreaId, directorateId, cancellationToken);

        var filterBaName = businessAreaId is int fba
            ? businessAreas.FirstOrDefault(b => b.Id == fba)?.Name
            : null;

        var intelligence = await MonthlyReportIntelligenceBuilder.BuildAsync(
            _db,
            _aissSummary,
            businessAreaId,
            directorateId,
            period.PeriodLabel,
            prevPeriodName,
            filterBaName,
            allProjects,
            prevProjects,
            weeklyUpdateStats,
            ragChanges,
            priorityChanges,
            raidSummary,
            accessibilityAreaRows,
            accessibilitySummary,
            periodStart,
            periodEnd,
            cancellationToken);

        string? businessAreaNarrative = null;
        if (businessAreaId.HasValue)
        {
            var baName = businessAreas.FirstOrDefault(x => x.Id == businessAreaId.Value)?.Name ?? "This business area";
            businessAreaNarrative = BuildBusinessAreaSummaryNarrative(
                baName,
                period.PeriodLabel,
                periodStart,
                reportIsoYear,
                reportIsoWeek,
                allProjects,
                newProjectsThisPeriod.Count,
                milestonesAchieved.Count,
                upcomingMilestones30,
                lateMilestones,
                weeklyUpdateStats,
                ragDistribution,
                priorityDistribution,
                prevPeriodRagDistribution,
                prevPeriodName,
                ragTrend,
                projectsWithPathToGreen.Count,
                projectsWithRagChange,
                projectsWithPriorityChange,
                ragChanges,
                priorityChanges);
        }

        var recentPeriodOptions = _weeklyUpdateService.EnumerateRecentPeriods(utcNow, 26).ToList();

        return new ModernWeeklyReportDashboardViewModel
        {
            IsoYear = reportIsoYear,
            IsoWeek = reportIsoWeek,
            PeriodLabel = period.PeriodLabel,
            DefaultIsoYear = defaultIsoYear,
            DefaultIsoWeek = defaultIsoWeek,
            ReportYear = reportIsoYear,
            ReportMonth = reportIsoWeek,
            MonthName = period.PeriodLabel,
            MonthStart = periodStart,
            MonthEnd = periodEnd,
            FilterBusinessAreaId = businessAreaId,
            FilterDirectorateId = directorateId,
            BusinessAreas = businessAreas,
            Directorates = directorates,
            TotalActiveProjects = allProjects.Count,
            NewProjectsCount = newProjectsThisPeriod.Count,
            MilestonesAchievedCount = milestonesAchieved.Count,
            NewProjectsThisMonth = newProjectsThisPeriod,
            MilestonesAchieved = milestonesAchieved,
            UpcomingMilestonesNext30Days = upcomingMilestones30,
            LateMilestones = lateMilestones,
            MonthlyUpdateStats = weeklyUpdateStats,
            BusinessAreaSubmissionProgress = businessAreaSubmissionProgress,
            RagDistribution = ragDistribution,
            PriorityDistribution = priorityDistribution,
            PrevMonthRagDistribution = prevPeriodRagDistribution,
            PrevMonthPriorityDistribution = prevPeriodPriorityDistribution,
            PrevMonthName = prevPeriodName,
            BusinessAreaRows = businessAreaRows,
            ProjectsWithPathToGreen = projectsWithPathToGreen,
            RagPriorityMatrix = matrix,
            ProjectsWithRagChangeInPeriod = projectsWithRagChange,
            ProjectsWithPriorityChangeInPeriod = projectsWithPriorityChange,
            RagTrend = ragTrend,
            PriorityTrend = priorityTrend,
            RagChanges = ragChanges,
            PriorityChanges = priorityChanges,
            BusinessAreaSummaryNarrative = businessAreaNarrative,
            MinReportYear = reportIsoYear,
            MaxReportYear = defaultIsoYear,
            HasPreviousWeekNav = hasPreviousWeekNav,
            HasNextWeekNav = hasNextWeekNav,
            PreviousNavIsoYear = previousNavIsoYear,
            PreviousNavIsoWeek = previousNavIsoWeek,
            NextNavIsoYear = nextNavIsoYear,
            NextNavIsoWeek = nextNavIsoWeek,
            HasPreviousMonthNav = hasPreviousWeekNav,
            HasNextMonthNav = hasNextWeekNav,
            PreviousNavYear = previousNavIsoYear,
            PreviousNavMonth = previousNavIsoWeek,
            NextNavYear = nextNavIsoYear,
            NextNavMonth = nextNavIsoWeek,
            AccessibilitySummary = accessibilitySummary,
            AccessibilitySummaryError = accessibilityError,
            AccessibilityAreaRows = accessibilityAreaRows,
            AccessibilityIssueCriteria = accessibilityIssueCriteria,
            RaidSummary = raidSummary,
            Intelligence = intelligence,
            ScopeProjectItems = allProjects
                .Select(p => ToWeeklyBusinessAreaProjectItem(
                    p, reportIsoYear, reportIsoWeek, periodStart, periodEnd, upcomingWindowEnd, todayUtc))
                .OrderBy(x => x.Title)
                .ToList(),
            RagSixMonthTrendRows = ragSixWeekTrendRows,
            RecentPeriods = recentPeriodOptions
        };
    }

    public async Task<ModernWeeklySubmissionProgressViewModel> BuildWeeklySubmissionProgressAsync(
        int? isoYear,
        int? isoWeek,
        int? businessAreaId,
        int? directorateId,
        CancellationToken cancellationToken = default)
    {
        var utcNow = DateTime.UtcNow;
        var (defaultIsoYear, defaultIsoWeek) = _weeklyUpdateService.ResolveDashboardReportingPeriod(utcNow);
        var (reportIsoYear, reportIsoWeek, period) = ResolveWeeklyReportingPeriod(isoYear, isoWeek, defaultIsoYear, defaultIsoWeek);
        if (period == null)
            throw new InvalidOperationException("Weekly reporting period is not configured or not available.");

        var periodStart = period.PeriodStart;
        var periodEnd = period.PeriodEnd;

        var dueDate = _weeklyUpdateService.GetWeeklyUpdateDueDate(reportIsoYear, reportIsoWeek).Date;
        var submissionWindowStart = _weeklyUpdateService.GetSubmissionWindowOpens(reportIsoYear, reportIsoWeek);
        var submissionWindowEnd = _weeklyUpdateService.GetSubmissionWindowCloses(reportIsoYear, reportIsoWeek);
        if (submissionWindowEnd < submissionWindowStart)
            submissionWindowEnd = submissionWindowStart;

        var submissionWindowDescription = $"Submission due {dueDate:d MMMM yyyy}";

        var allProjects = await LoadWeeklyScopedProjectsAsync(
            businessAreaId,
            directorateId,
            query => query
                .Include(p => p.BusinessAreaLookup)
                .Include(p => p.WeeklyWorkUpdates)
                .Include(p => p.Directorates)
                    .ThenInclude(d => d.Division),
            cancellationToken);

        var businessAreas = await LoadActiveBusinessAreasAsync(cancellationToken);
        var directorates = await LoadActiveDirectoratesAsync(cancellationToken);

        var weeklyUpdateStats = CalculateWeeklyUpdateStats(allProjects, reportIsoYear, reportIsoWeek, dueDate);
        var expectedProgressToday = ComputeExpectedProgressPercent(submissionWindowStart, submissionWindowEnd, DateTime.UtcNow.Date);

        var submittedDates = allProjects
            .Select(p => p.WeeklyWorkUpdates?.FirstOrDefault(u => u.IsoYear == reportIsoYear && u.IsoWeek == reportIsoWeek))
            .Where(u => u?.SubmittedAt != null)
            .Select(u => u!.SubmittedAt!.Value.Date)
            .OrderBy(d => d)
            .ToList();

        var dailyProgress = BuildDailySubmissionProgress(
            allProjects.Count,
            submissionWindowStart,
            submissionWindowEnd,
            submittedDates);

        var businessAreaLeague = BuildWeeklySubmissionLeagueByBusinessArea(
            allProjects, reportIsoYear, reportIsoWeek, expectedProgressToday, dueDate);
        var directorateLeague = BuildWeeklySubmissionLeagueByDirectorate(
            allProjects, directorates, reportIsoYear, reportIsoWeek, expectedProgressToday, dueDate);

        var (trendColumns, businessAreaTrendRows) = BuildBusinessAreaSixWeekTrends(
            allProjects, reportIsoYear, reportIsoWeek);

        var (hasPreviousWeekNav, previousNavIsoYear, previousNavIsoWeek) =
            GetWeeklyNavPrevious(reportIsoYear, reportIsoWeek);
        var (hasNextWeekNav, nextNavIsoYear, nextNavIsoWeek) =
            GetWeeklyNavNext(reportIsoYear, reportIsoWeek, defaultIsoYear, defaultIsoWeek);

        var recentPeriodOptions = _weeklyUpdateService.EnumerateRecentPeriods(utcNow, 26).ToList();

        return new ModernWeeklySubmissionProgressViewModel
        {
            IsoYear = reportIsoYear,
            IsoWeek = reportIsoWeek,
            PeriodLabel = period.PeriodLabel,
            DefaultIsoYear = defaultIsoYear,
            DefaultIsoWeek = defaultIsoWeek,
            ReportYear = reportIsoYear,
            ReportMonth = reportIsoWeek,
            MonthName = period.PeriodLabel,
            MonthStart = periodStart,
            MonthEnd = periodEnd,
            FilterBusinessAreaId = businessAreaId,
            FilterDirectorateId = directorateId,
            BusinessAreas = businessAreas,
            Directorates = directorates,
            MonthlyUpdateStats = weeklyUpdateStats,
            SubmissionWindowStart = submissionWindowStart,
            SubmissionWindowEnd = submissionWindowEnd,
            UsesExplicitReportingPeriod = false,
            SubmissionWindowDescription = submissionWindowDescription,
            ExpectedProgressPercentToday = expectedProgressToday,
            DailyProgress = dailyProgress,
            BusinessAreaLeague = businessAreaLeague,
            DirectorateLeague = directorateLeague,
            TrendMonthColumns = trendColumns,
            BusinessAreaTrendRows = businessAreaTrendRows,
            HasPreviousWeekNav = hasPreviousWeekNav,
            HasNextWeekNav = hasNextWeekNav,
            PreviousNavIsoYear = previousNavIsoYear,
            PreviousNavIsoWeek = previousNavIsoWeek,
            NextNavIsoYear = nextNavIsoYear,
            NextNavIsoWeek = nextNavIsoWeek,
            HasPreviousMonthNav = hasPreviousWeekNav,
            HasNextMonthNav = hasNextWeekNav,
            PreviousNavYear = previousNavIsoYear,
            PreviousNavMonth = previousNavIsoWeek,
            NextNavYear = nextNavIsoYear,
            NextNavMonth = nextNavIsoWeek,
            RecentPeriods = recentPeriodOptions
        };
    }

    private (int IsoYear, int IsoWeek, WeeklyReportingPeriodInfo? Period) ResolveWeeklyReportingPeriod(
        int? isoYear,
        int? isoWeek,
        int defaultIsoYear,
        int defaultIsoWeek)
    {
        var reportIsoYear = isoYear ?? defaultIsoYear;
        var reportIsoWeek = isoWeek ?? defaultIsoWeek;
        if (reportIsoWeek is < 1 or > 53)
        {
            reportIsoYear = defaultIsoYear;
            reportIsoWeek = defaultIsoWeek;
        }

        var period = _weeklyUpdateService.TryGetReportingPeriod(reportIsoYear, reportIsoWeek);
        if (period != null)
            return (reportIsoYear, reportIsoWeek, period);

        period = _weeklyUpdateService.TryGetReportingPeriod(defaultIsoYear, defaultIsoWeek);
        return (defaultIsoYear, defaultIsoWeek, period);
    }

    private async Task<List<Project>> LoadWeeklyScopedProjectsAsync(
        int? businessAreaId,
        int? directorateId,
        Func<IQueryable<Project>, IQueryable<Project>> configureIncludes,
        CancellationToken cancellationToken)
    {
        var scopeProjectIds = await _db.WeeklyWorkReportingScopeProjects
            .AsNoTracking()
            .Select(x => x.ProjectId)
            .ToListAsync(cancellationToken);

        var query = configureIncludes(_db.Projects.AsNoTracking())
            .Where(p => !p.IsDeleted && (p.Status == "Active" || p.Status == "Paused"))
            .Where(p => scopeProjectIds.Contains(p.Id));

        if (businessAreaId.HasValue)
            query = query.Where(p => p.BusinessAreaId == businessAreaId.Value);
        if (directorateId.HasValue)
            query = query.Where(p => p.Directorates.Any(d => d.DivisionId == directorateId.Value));

        return await query.ToListAsync(cancellationToken);
    }

    private async Task<List<BusinessAreaLookup>> LoadActiveBusinessAreasAsync(CancellationToken cancellationToken) =>
        await _db.BusinessAreaLookups
            .AsNoTracking()
            .Where(ba => ba.IsActive)
            .OrderBy(ba => ba.SortOrder)
            .ThenBy(ba => ba.Name)
            .ToListAsync(cancellationToken);

    private async Task<List<Division>> LoadActiveDirectoratesAsync(CancellationToken cancellationToken) =>
        await _db.Divisions
            .AsNoTracking()
            .Where(d => d.IsActive)
            .OrderBy(d => d.SortOrder)
            .ThenBy(d => d.Name)
            .ToListAsync(cancellationToken);

    private static MonthlyUpdateStats CalculateWeeklyUpdateStats(
        List<Project> projects,
        int isoYear,
        int isoWeek,
        DateTime dueDate)
    {
        var totalProjects = projects.Count;
        var currentDate = DateTime.UtcNow;
        var submitted = 0;
        var notStarted = 0;
        var inProgress = 0;
        var late = 0;

        foreach (var project in projects)
        {
            var update = project.WeeklyWorkUpdates?.FirstOrDefault(u => u.IsoYear == isoYear && u.IsoWeek == isoWeek);

            if (update != null && update.SubmittedAt.HasValue)
                submitted++;
            else if (currentDate > dueDate)
                late++;
            else if (update != null && !update.SubmittedAt.HasValue)
                inProgress++;
            else
                notStarted++;
        }

        return new MonthlyUpdateStats
        {
            Year = isoYear,
            Month = isoWeek,
            TotalProjects = totalProjects,
            Submitted = submitted,
            NotStarted = notStarted,
            InProgress = inProgress,
            Late = late,
            DueDate = dueDate
        };
    }

    private List<ModernBusinessAreaDashboardRow> BuildWeeklyBusinessAreaDashboardRows(
        List<Project> allProjects,
        int reportIsoYear,
        int reportIsoWeek,
        DateTime periodStart,
        DateTime periodEnd,
        DateTime upcomingWindowEnd,
        DateTime todayUtc,
        DateTime nowUtcForSubmission,
        DateTime dueDateForSubmission) =>
        allProjects
            .GroupBy(p => p.BusinessAreaId)
            .Select(g => BuildWeeklyDashboardRowFromProjects(
                g.ToList(),
                g.First().BusinessAreaLookup?.Name ?? "Not set",
                g.Key,
                reportIsoYear,
                reportIsoWeek,
                periodStart,
                periodEnd,
                upcomingWindowEnd,
                todayUtc,
                nowUtcForSubmission,
                dueDateForSubmission))
            .OrderByDescending(r => r.CompletionRatePercent)
            .ThenBy(r => r.BusinessArea == "Not set" ? "zzzzzz" : r.BusinessArea)
            .ToList();

    private ModernBusinessAreaDashboardRow BuildWeeklyDashboardRowFromProjects(
        List<Project> projects,
        string groupName,
        int? groupId,
        int reportIsoYear,
        int reportIsoWeek,
        DateTime periodStart,
        DateTime periodEnd,
        DateTime upcomingWindowEnd,
        DateTime todayUtc,
        DateTime nowUtcForSubmission,
        DateTime dueDateForSubmission)
    {
        var submitted = 0;
        var inProgress = 0;
        var late = 0;
        var notStarted = 0;
        foreach (var p in projects)
        {
            var update = p.WeeklyWorkUpdates?.FirstOrDefault(u => u.IsoYear == reportIsoYear && u.IsoWeek == reportIsoWeek);
            if (update != null && update.SubmittedAt.HasValue)
                submitted++;
            else if (nowUtcForSubmission > dueDateForSubmission)
                late++;
            else if (update != null && !update.SubmittedAt.HasValue)
                inProgress++;
            else
                notStarted++;
        }

        var total = projects.Count;
        var completion = total == 0 ? 0 : Math.Round(100m * submitted / total, 1, MidpointRounding.AwayFromZero);

        return new ModernBusinessAreaDashboardRow
        {
            BusinessArea = groupName,
            BusinessAreaId = groupId,
            TotalProjects = total,
            SubmittedCount = submitted,
            InProgressCount = inProgress,
            LateCount = late,
            NotStartedCount = notStarted,
            CompletionRatePercent = completion,
            NewThisMonth = projects.Count(p => p.CreatedAt >= periodStart && p.CreatedAt <= periodEnd),
            MilestonesCompleted = projects.SelectMany(p => p.Milestones
                .Where(m => !m.IsDeleted &&
                            m.Status == "complete" &&
                            m.ActualDate.HasValue &&
                            m.ActualDate.Value >= periodStart &&
                            m.ActualDate.Value <= periodEnd)).Count(),
            MilestonesUpcoming30Days = projects.SelectMany(p => p.Milestones
                .Where(m => !m.IsDeleted &&
                            m.Status != "complete" &&
                            m.Status != "cancelled" &&
                            m.DueDate >= periodStart &&
                            m.DueDate < upcomingWindowEnd)).Count(),
            MilestonesLate = projects.SelectMany(p => p.Milestones
                .Where(m => !m.IsDeleted &&
                            m.Status != "complete" &&
                            m.Status != "cancelled" &&
                            m.DueDate.Date < todayUtc)).Count(),
            RagRed = projects.Count(p => RagBucket(p) == "Red"),
            RagAmberRed = projects.Count(p => RagBucket(p) == "Amber-Red"),
            RagAmberGreen = projects.Count(p => RagBucket(p) == "Amber-Green"),
            RagGreen = projects.Count(p => RagBucket(p) == "Green"),
            RagNotSet = projects.Count(p => RagBucket(p) == "Not Set"),
            PriCritical = projects.Count(p => PriorityBucket(p) == "Critical"),
            PriHigh = projects.Count(p => PriorityBucket(p) == "High"),
            PriMedium = projects.Count(p => PriorityBucket(p) == "Medium"),
            PriLow = projects.Count(p => PriorityBucket(p) == "Low"),
            PriNotSet = projects.Count(p => PriorityBucket(p) == "Not Set"),
            Projects = projects
                .Select(p => ToWeeklyBusinessAreaProjectItem(
                    p, reportIsoYear, reportIsoWeek, periodStart, periodEnd, upcomingWindowEnd, todayUtc))
                .OrderBy(x => x.Title)
                .ToList()
        };
    }

    private static BusinessAreaProjectItem ToWeeklyBusinessAreaProjectItem(
        Project p,
        int reportIsoYear,
        int reportIsoWeek,
        DateTime periodStart,
        DateTime periodEnd,
        DateTime upcomingWindowEnd,
        DateTime todayUtc)
    {
        var periodUpdate = p.WeeklyWorkUpdates?.FirstOrDefault(u => u.IsoYear == reportIsoYear && u.IsoWeek == reportIsoWeek);
        var latestSubmitted = p.WeeklyWorkUpdates?
            .Where(u => u.SubmittedAt.HasValue)
            .OrderByDescending(u => u.IsoYear)
            .ThenByDescending(u => u.IsoWeek)
            .FirstOrDefault();

        return new BusinessAreaProjectItem
        {
            Id = p.Id,
            Title = p.Title,
            Status = p.Status,
            BusinessArea = p.BusinessAreaLookup?.Name ?? "Not set",
            Summary = string.IsNullOrWhiteSpace(p.Aim) ? null : p.Aim.Trim(),
            PathToGreen = string.IsNullOrWhiteSpace(p.PathToGreen) ? null : p.PathToGreen.Trim(),
            RagJustification = string.IsNullOrWhiteSpace(p.RagJustification) ? null : p.RagJustification.Trim(),
            LatestMonthlyUpdateNarrative = string.IsNullOrWhiteSpace(latestSubmitted?.Narrative) ? null : latestSubmitted!.Narrative.Trim(),
            Rag = RagBucket(p),
            Priority = PriorityBucket(p),
            PermFte = periodUpdate?.WeeklyPermFte ?? latestSubmitted?.WeeklyPermFte,
            MspFte = periodUpdate?.WeeklyMspFte ?? latestSubmitted?.WeeklyMspFte,
            MilestonesSummary = BuildMilestonesSummary(p, periodStart, periodEnd, upcomingWindowEnd, todayUtc),
            SubmittedUpdate = periodUpdate?.SubmittedAt.HasValue == true,
            IsNew = p.CreatedAt >= periodStart && p.CreatedAt <= periodEnd,
            HasMilestoneCompletedInPeriod = p.Milestones.Any(m =>
                !m.IsDeleted &&
                m.Status == "complete" &&
                m.ActualDate.HasValue &&
                m.ActualDate.Value >= periodStart &&
                m.ActualDate.Value <= periodEnd),
            HasMilestoneUpcomingInWindow = p.Milestones.Any(m =>
                !m.IsDeleted &&
                m.Status != "complete" &&
                m.Status != "cancelled" &&
                m.DueDate >= periodStart &&
                m.DueDate < upcomingWindowEnd),
            HasLateMilestone = p.Milestones.Any(m =>
                !m.IsDeleted &&
                m.Status != "complete" &&
                m.Status != "cancelled" &&
                m.DueDate.Date < todayUtc)
        };
    }

    private static List<RagTrendMonthPoint> BuildWeeklyRagTrend(
        List<Project> projects,
        Dictionary<int, List<ProjectRagHistory>> historyByProject,
        IReadOnlyList<WeeklyReportingPeriodInfo> recentPeriods,
        int endIsoYear,
        int endIsoWeek)
    {
        var periods = recentPeriods
            .Reverse()
            .TakeWhile(p => p.IsoYear < endIsoYear || (p.IsoYear == endIsoYear && p.IsoWeek <= endIsoWeek))
            .Reverse()
            .TakeLast(6)
            .ToList();

        var list = new List<RagTrendMonthPoint>();
        foreach (var period in periods)
        {
            var cutoff = period.PeriodEnd.Date.AddDays(1);
            var dist = new Dictionary<string, int>
            {
                ["Red"] = 0,
                ["Amber-Red"] = 0,
                ["Amber-Green"] = 0,
                ["Green"] = 0,
                ["Not Set"] = 0
            };

            foreach (var p in projects)
            {
                var rag = ResolveRagAtCutoff(p, cutoff, historyByProject);
                if (string.IsNullOrWhiteSpace(rag) || string.Equals(rag, "Amber", StringComparison.OrdinalIgnoreCase))
                    dist["Not Set"]++;
                else if (dist.ContainsKey(rag))
                    dist[rag]++;
                else
                    dist["Not Set"]++;
            }

            list.Add(new RagTrendMonthPoint
            {
                Label = $"W{period.IsoWeek:D2}",
                Year = period.IsoYear,
                Month = period.IsoWeek,
                Red = dist["Red"],
                AmberRed = dist["Amber-Red"],
                AmberGreen = dist["Amber-Green"],
                Green = dist["Green"],
                NotSet = dist["Not Set"]
            });
        }

        return list;
    }

    private static List<PriorityTrendMonthPoint> BuildWeeklyPriorityTrend(
        List<Project> projects,
        IReadOnlyList<WeeklyReportingPeriodInfo> recentPeriods,
        int endIsoYear,
        int endIsoWeek)
    {
        var periods = recentPeriods
            .Reverse()
            .TakeWhile(p => p.IsoYear < endIsoYear || (p.IsoYear == endIsoYear && p.IsoWeek <= endIsoWeek))
            .Reverse()
            .TakeLast(6)
            .ToList();

        var list = new List<PriorityTrendMonthPoint>();
        foreach (var period in periods)
        {
            var dist = new Dictionary<string, int>
            {
                ["Critical"] = 0,
                ["High"] = 0,
                ["Medium"] = 0,
                ["Low"] = 0,
                ["Not Set"] = 0
            };
            foreach (var p in projects)
                dist[PriorityBucket(p)]++;

            list.Add(new PriorityTrendMonthPoint
            {
                Label = $"W{period.IsoWeek:D2}",
                Year = period.IsoYear,
                Month = period.IsoWeek,
                Critical = dist["Critical"],
                High = dist["High"],
                Medium = dist["Medium"],
                Low = dist["Low"],
                NotSet = dist["Not Set"]
            });
        }

        return list;
    }

    private List<WorkItemRagSixMonthTrendRow> BuildWeeklyRagSixWeekTrendRows(
        IReadOnlyList<Project> projects,
        Dictionary<int, List<ProjectRagHistory>> historyByProject,
        int endIsoYear,
        int endIsoWeek)
    {
        var periods = new List<WeeklyReportingPeriodInfo>();
        var year = endIsoYear;
        var week = endIsoWeek;
        for (var i = 0; i < 6; i++)
        {
            var period = _weeklyUpdateService.TryGetReportingPeriod(year, week);
            if (period == null)
                break;
            periods.Insert(0, period);
            var anchor = ISOWeek.ToDateTime(year, week, DayOfWeek.Monday).AddDays(-7);
            year = ISOWeek.GetYear(anchor);
            week = ISOWeek.GetWeekOfYear(anchor);
        }

        if (periods.Count == 0)
            return new List<WorkItemRagSixMonthTrendRow>();

        var cutoffs = periods.Select(p => p.PeriodEnd.Date.AddDays(1)).ToList();
        var labels = periods.Select(p => $"W{p.IsoWeek:D2}").ToList();
        var rows = new List<WorkItemRagSixMonthTrendRow>();

        foreach (var project in projects)
        {
            var buckets = cutoffs
                .Select(c => MonthlyReportRagTrendAnalyzer.BucketRag(ResolveRagAtCutoff(project, c, historyByProject)))
                .ToList();

            var snapshots = buckets
                .Select((rag, idx) => new RagSixMonthSnapshot { Label = labels[idx], Rag = rag })
                .ToList();

            rows.Add(new WorkItemRagSixMonthTrendRow
            {
                ProjectId = project.Id,
                Title = project.Title,
                BusinessArea = project.BusinessAreaLookup?.Name,
                TrendCategory = MonthlyReportRagTrendAnalyzer.ClassifyTrend(buckets),
                Months = snapshots
            });
        }

        return rows.OrderBy(r => r.Title, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private (List<SubmissionTrendMonthColumn> Columns, List<BusinessAreaMonthlySubmissionTrendRow> Rows)
        BuildBusinessAreaSixWeekTrends(List<Project> projects, int endIsoYear, int endIsoWeek)
    {
        const int weekCount = 6;
        var columns = new List<SubmissionTrendMonthColumn>();
        var periods = new List<WeeklyReportingPeriodInfo>();

        var year = endIsoYear;
        var week = endIsoWeek;
        for (var i = 0; i < weekCount; i++)
        {
            var period = _weeklyUpdateService.TryGetReportingPeriod(year, week);
            if (period == null)
                break;
            periods.Insert(0, period);
            var anchor = ISOWeek.ToDateTime(year, week, DayOfWeek.Monday).AddDays(-7);
            year = ISOWeek.GetYear(anchor);
            week = ISOWeek.GetWeekOfYear(anchor);
        }

        foreach (var period in periods)
        {
            columns.Add(new SubmissionTrendMonthColumn
            {
                Year = period.IsoYear,
                Month = period.IsoWeek,
                Label = $"W{period.IsoWeek:D2} {period.PeriodStart:MMM yy}"
            });
        }

        var rows = projects
            .GroupBy(p => p.BusinessAreaId)
            .Select(g =>
            {
                var name = g.First().BusinessAreaLookup?.Name ?? "Not set";
                var months = new List<BusinessAreaMonthlySubmissionCell>();
                foreach (var col in columns)
                {
                    var (submitted, total) = CountWeeklySubmissionForPeriod(g, col.Year, col.Month);
                    var pct = total == 0 ? 0m : Math.Round(100m * submitted / total, 1, MidpointRounding.AwayFromZero);
                    var cell = new BusinessAreaMonthlySubmissionCell
                    {
                        TotalInScope = total,
                        Submitted = submitted,
                        CompletionPercent = pct
                    };
                    if (months.Count > 0)
                        cell.MonthOverMonth = ClassifyMonthOverMonth(months[^1], cell);
                    months.Add(cell);
                }

                var first = months.Count > 0 ? months[0] : new BusinessAreaMonthlySubmissionCell();
                var last = months.Count > 0 ? months[^1] : new BusinessAreaMonthlySubmissionCell();
                var scopeDelta = last.TotalInScope - first.TotalInScope;
                var completionDelta = last.CompletionPercent - first.CompletionPercent;
                var trend = ClassifySubmissionTrend(months);
                var summary = DescribeSubmissionTrend(trend, scopeDelta, completionDelta);

                return new BusinessAreaMonthlySubmissionTrendRow
                {
                    BusinessAreaName = name,
                    BusinessAreaId = g.Key,
                    Months = months,
                    Trend = trend,
                    TrendSummary = summary
                };
            })
            .Where(r => r.Months.Any(m => m.TotalInScope > 0))
            .OrderBy(r => r.BusinessAreaName == "Not set" ? "zzzzzz" : r.BusinessAreaName)
            .ToList();

        return (columns, rows);
    }

    private static (int Submitted, int TotalInScope) CountWeeklySubmissionForPeriod(
        IEnumerable<Project> projects,
        int isoYear,
        int isoWeek)
    {
        var inScope = projects.ToList();
        var total = inScope.Count;
        var submitted = inScope.Count(p =>
        {
            var update = p.WeeklyWorkUpdates?.FirstOrDefault(u => u.IsoYear == isoYear && u.IsoWeek == isoWeek);
            return update?.SubmittedAt != null;
        });
        return (submitted, total);
    }

    private List<MonthlySubmissionLeagueRow> BuildWeeklySubmissionLeagueByBusinessArea(
        List<Project> projects,
        int reportIsoYear,
        int reportIsoWeek,
        decimal expectedProgressPercent,
        DateTime dueDate) =>
        projects
            .GroupBy(p => p.BusinessAreaId)
            .Select(g => BuildWeeklySubmissionLeagueRow(
                g.First().BusinessAreaLookup?.Name ?? "Not set",
                g.Key,
                g.ToList(),
                reportIsoYear,
                reportIsoWeek,
                expectedProgressPercent,
                dueDate))
            .OrderBy(r => r.Name == "Not set" ? "zzzzzz" : r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private List<MonthlySubmissionLeagueRow> BuildWeeklySubmissionLeagueByDirectorate(
        List<Project> projects,
        List<Division> directorateLookups,
        int reportIsoYear,
        int reportIsoWeek,
        decimal expectedProgressPercent,
        DateTime dueDate)
    {
        var dirNameById = directorateLookups.ToDictionary(d => d.Id, d => d.Name);

        return projects
            .GroupBy(GetPrimaryDirectorateId)
            .Select(g =>
            {
                var name = g.Key.HasValue && dirNameById.TryGetValue(g.Key.Value, out var n)
                    ? n
                    : "Not set";
                return BuildWeeklySubmissionLeagueRow(name, g.Key, g.ToList(), reportIsoYear, reportIsoWeek, expectedProgressPercent, dueDate);
            })
            .OrderBy(r => r.Name == "Not set" ? "zzzzzz" : r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static MonthlySubmissionLeagueRow BuildWeeklySubmissionLeagueRow(
        string name,
        int? entityId,
        List<Project> projects,
        int reportIsoYear,
        int reportIsoWeek,
        decimal expectedProgressPercent,
        DateTime dueDate)
    {
        var nowUtc = DateTime.UtcNow;
        var submitted = 0;
        var inProgress = 0;
        var late = 0;
        var notStarted = 0;

        foreach (var project in projects)
        {
            var update = project.WeeklyWorkUpdates?.FirstOrDefault(u => u.IsoYear == reportIsoYear && u.IsoWeek == reportIsoWeek);
            if (update != null && update.SubmittedAt.HasValue)
                submitted++;
            else if (nowUtc > dueDate)
                late++;
            else if (update != null && !update.SubmittedAt.HasValue)
                inProgress++;
            else
                notStarted++;
        }

        var total = projects.Count;
        var actual = total == 0 ? 0 : Math.Round(100m * submitted / total, 1, MidpointRounding.AwayFromZero);

        return new MonthlySubmissionLeagueRow
        {
            Name = name,
            EntityId = entityId,
            TotalToReport = total,
            Submitted = submitted,
            InProgress = inProgress,
            Late = late,
            NotStarted = notStarted,
            ActualProgressPercent = actual,
            ExpectedProgressPercent = expectedProgressPercent,
            WorkItems = projects
                .OrderBy(p => p.Title, StringComparer.OrdinalIgnoreCase)
                .Select(p => BuildWeeklySubmissionProgressWorkItemRow(p, reportIsoYear, reportIsoWeek, dueDate))
                .ToList()
        };
    }

    private static SubmissionProgressWorkItemRow BuildWeeklySubmissionProgressWorkItemRow(
        Project p,
        int reportIsoYear,
        int reportIsoWeek,
        DateTime dueDate)
    {
        var (status, submittedAt) = ResolveWeeklyDetailedSubmissionStatus(p, reportIsoYear, reportIsoWeek, dueDate);
        return new SubmissionProgressWorkItemRow
        {
            ProjectId = p.Id,
            Title = p.Title,
            SubmissionStatus = status,
            SubmittedAt = submittedAt
        };
    }

    private static (string Status, DateTime? SubmittedAt) ResolveWeeklyDetailedSubmissionStatus(
        Project project,
        int reportIsoYear,
        int reportIsoWeek,
        DateTime dueDate)
    {
        var nowUtc = DateTime.UtcNow;
        var update = project.WeeklyWorkUpdates?.FirstOrDefault(u => u.IsoYear == reportIsoYear && u.IsoWeek == reportIsoWeek);

        if (update != null && update.SubmittedAt.HasValue)
            return ("Submitted", update.SubmittedAt);

        if (nowUtc > dueDate)
            return ("Late", null);

        if (update != null && !update.SubmittedAt.HasValue)
            return ("In progress", null);

        return ("Not started", null);
    }

    private static (int IsoYear, int IsoWeek, WeeklyReportingPeriodInfo? Period) GetAdjacentWeeklyPeriod(
        int isoYear,
        int isoWeek,
        int directionWeeks)
    {
        var anchor = ISOWeek.ToDateTime(isoYear, isoWeek, DayOfWeek.Monday).AddDays(directionWeeks * 7);
        var adjYear = ISOWeek.GetYear(anchor);
        var adjWeek = ISOWeek.GetWeekOfYear(anchor);
        return (adjYear, adjWeek, null);
    }

    private (bool HasPrevious, int? PreviousYear, int? PreviousWeek) GetWeeklyNavPrevious(int isoYear, int isoWeek)
    {
        var (prevYear, prevWeek, _) = GetAdjacentWeeklyPeriod(isoYear, isoWeek, -1);
        var prevPeriod = _weeklyUpdateService.TryGetReportingPeriod(prevYear, prevWeek);
        return prevPeriod != null ? (true, prevYear, prevWeek) : (false, null, null);
    }

    private (bool HasNext, int? NextYear, int? NextWeek) GetWeeklyNavNext(
        int isoYear,
        int isoWeek,
        int defaultIsoYear,
        int defaultIsoWeek)
    {
        var (nextYear, nextWeek, _) = GetAdjacentWeeklyPeriod(isoYear, isoWeek, 1);
        if (nextYear > defaultIsoYear || (nextYear == defaultIsoYear && nextWeek > defaultIsoWeek))
            return (false, null, null);

        var nextPeriod = _weeklyUpdateService.TryGetReportingPeriod(nextYear, nextWeek);
        return nextPeriod != null ? (true, nextYear, nextWeek) : (false, null, null);
    }
}
