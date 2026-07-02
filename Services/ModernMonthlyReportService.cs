using Compass.Controllers;
using Compass.Data;
using Compass.Models;
using Compass.Models.Fips;
using Compass.Services.Aiss;
using Compass.Services.Modern;
using Compass.ViewModels;
using Compass.ViewModels.Modern;
using Microsoft.EntityFrameworkCore;

namespace Compass.Services;

/// <summary>Builds data for the modern monthly reporting dashboard (aligned with Central Ops Monthly Summary V2 logic).</summary>
public partial class ModernMonthlyReportService
{
    private readonly CompassDbContext _db;
    private readonly IMonthlyUpdateService _monthlyUpdateService;
    private readonly IWeeklyUpdateService _weeklyUpdateService;
    private readonly IAissSummaryService _aissSummary;

    public ModernMonthlyReportService(
        CompassDbContext db,
        IMonthlyUpdateService monthlyUpdateService,
        IWeeklyUpdateService weeklyUpdateService,
        IAissSummaryService aissSummary)
    {
        _db = db;
        _monthlyUpdateService = monthlyUpdateService;
        _weeklyUpdateService = weeklyUpdateService;
        _aissSummary = aissSummary;
    }

    public async Task<ModernPrioritiesReportViewModel> BuildPrioritiesReportAsync(
        string? dimension,
        int? year,
        int? month,
        int? groupId,
        CancellationToken cancellationToken = default)
    {
        var options = new PrioritiesReportOptions
        {
            Dimension = NormalizePrioritiesDimension(dimension),
            GroupId = groupId,
            IncludeAllDimensions = true
        };
        var report = await BuildDashboardAsync(year, month, null, null, options, cancellationToken);
        var ctx = report.PrioritiesReport!;
        return new ModernPrioritiesReportViewModel
        {
            Report = report,
            DimensionSections = ctx.DimensionSections
        };
    }

    public async Task<ModernResourcingReportViewModel> BuildResourcingReportAsync(
        int? year,
        int? month,
        int? businessAreaId,
        int? directorateId,
        string? dimension,
        int? groupId,
        CancellationToken cancellationToken = default)
    {
        var currentDate = DateTime.UtcNow;
        var calendarYearUtc = currentDate.Year;
        var currentMonth = currentDate.Month;

        const int minReportYear = 2026;
        var maxSelectableYear = calendarYearUtc >= minReportYear ? calendarYearUtc : minReportYear;

        var currentPeriodDueDate = _monthlyUpdateService.GetMonthlyUpdateDueDate(calendarYearUtc, currentMonth);
        var daysUntilCurrentPeriodDueDate = (currentPeriodDueDate - currentDate).Days;

        var defaultReportYear = daysUntilCurrentPeriodDueDate <= 10 ? calendarYearUtc : (currentMonth == 1 ? calendarYearUtc - 1 : calendarYearUtc);
        var defaultReportMonth = daysUntilCurrentPeriodDueDate <= 10 ? currentMonth : (currentMonth == 1 ? 12 : currentMonth - 1);

        defaultReportYear = Math.Max(minReportYear, defaultReportYear);

        var reportYear = year ?? defaultReportYear;
        var reportMonth = month ?? defaultReportMonth;
        if (reportMonth < 1 || reportMonth > 12)
            reportMonth = defaultReportMonth;

        reportYear = Math.Clamp(reportYear, minReportYear, maxSelectableYear);

        var dimensionKey = NormalizeResourcingDimension(dimension);

        var query = _db.Projects
            .AsNoTracking()
            .Include(p => p.BusinessAreaLookup)
            .Include(p => p.DeliveryPriority)
            .Include(p => p.RagStatusLookup)
            .Include(p => p.MonthlyUpdates)
            .Include(p => p.Directorates)
                .ThenInclude(d => d.Division)
            .Include(p => p.ProjectMissions)
                .ThenInclude(pm => pm.Mission)
            .Include(p => p.ProjectObjectives)
                .ThenInclude(po => po.Objective)
            .Where(p => !p.IsDeleted && p.Status != "Cancelled" && p.Status != "Completed");

        if (businessAreaId.HasValue)
            query = query.Where(p => p.BusinessAreaId == businessAreaId.Value);
        if (directorateId.HasValue)
            query = query.Where(p => p.Directorates.Any(d => d.DivisionId == directorateId.Value));

        var scopedProjects = await query.ToListAsync(cancellationToken);

        var groupOptions = new List<PrioritiesReportGroupOption>();
        string? groupName = null;
        if (dimensionKey != "all")
        {
            groupOptions = BuildPrioritiesGroupOptions(scopedProjects, dimensionKey);
            if (groupId.HasValue)
            {
                scopedProjects = FilterProjectsByPrioritiesGroup(scopedProjects, dimensionKey, groupId.Value);
                groupName = groupOptions.FirstOrDefault(o => o.GroupId == groupId.Value)?.Name;
            }
        }

        var bands = await _db.ResourceBandLookups.AsNoTracking()
            .Where(rb => rb.IsActive)
            .OrderBy(rb => rb.SortOrder)
            .ThenBy(rb => rb.MinFte)
            .Select(rb => new ResourcingBandViewModel
            {
                Id = rb.Id,
                Name = rb.Name,
                Description = rb.Description,
                MinFte = rb.MinFte,
                MaxFte = rb.MaxFte,
                CssClass = rb.CssClass,
                SortOrder = rb.SortOrder
            })
            .ToListAsync(cancellationToken);

        var itemRows = new List<ResourcingWorkItemRow>();
        foreach (var project in scopedProjects)
        {
            var periodUpdate = project.MonthlyUpdates.FirstOrDefault(u =>
                u.Year == reportYear &&
                u.Month == reportMonth &&
                u.SubmittedAt.HasValue);
            if (periodUpdate == null)
                continue;

            var perm = periodUpdate.MonthlyPermFte ?? 0m;
            var msp = periodUpdate.MonthlyMspFte ?? 0m;
            var total = perm + msp;
            var band = ResolveResourceBand(total, bands);

            var directorateNames = project.Directorates
                .Select(d => d.Division?.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n)
                .ToList();

            itemRows.Add(new ResourcingWorkItemRow
            {
                WorkItemId = project.Id,
                Title = project.Title,
                BusinessArea = project.BusinessAreaLookup?.Name ?? "Not set",
                Directorates = directorateNames.Count == 0 ? "Not set" : string.Join(", ", directorateNames),
                Rag = RagBucket(project),
                Priority = project.DeliveryPriority?.Name,
                PermFte = perm,
                MspFte = msp,
                ResourcingFte = total,
                BandName = band.Name,
                BandCssClass = band.CssClass
            });
        }

        var workItemRows = itemRows
            .OrderBy(r => PrioritySortKey(r.Priority ?? "Not set"))
            .ThenByDescending(r => r.ResourcingFte)
            .ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var businessAreaRows = workItemRows
            .GroupBy(r => r.BusinessArea)
            .Select(g => BuildResourcingAggregateRow(
                g.Key,
                scopedProjects.FirstOrDefault(p => string.Equals(p.BusinessAreaLookup?.Name ?? "Not set", g.Key, StringComparison.OrdinalIgnoreCase))?.BusinessAreaId,
                g.ToList(),
                bands))
            .OrderByDescending(r => r.ResourcingFteTotal)
            .ThenBy(r => r.Name == "Not set" ? "zzzzzz" : r.Name)
            .ToList();

        var directorateLinks = new List<(int? DirectorateId, string DirectorateName, ResourcingWorkItemRow Item)>();
        foreach (var project in scopedProjects)
        {
            var item = workItemRows.FirstOrDefault(r => r.WorkItemId == project.Id);
            if (item == null)
                continue;

            var directors = project.Directorates
                .Where(d => d.Division != null && !string.IsNullOrWhiteSpace(d.Division!.Name))
                .Select(d => (DirectorateId: (int?)d.DivisionId, DirectorateName: d.Division!.Name.Trim()))
                .Distinct()
                .ToList();

            if (directors.Count == 0)
            {
                directorateLinks.Add((null, "Not set", item));
                continue;
            }

            foreach (var d in directors)
                directorateLinks.Add((d.DirectorateId, d.DirectorateName, item));
        }

        var directorateRows = directorateLinks
            .GroupBy(x => (x.DirectorateId, x.DirectorateName))
            .Select(g => BuildResourcingAggregateRow(
                g.Key.DirectorateName,
                g.Key.DirectorateId,
                g.Select(x => x.Item).DistinctBy(i => i.WorkItemId).ToList(),
                bands))
            .OrderByDescending(r => r.ResourcingFteTotal)
            .ThenBy(r => r.Name == "Not set" ? "zzzzzz" : r.Name)
            .ToList();

        var trendPoints = BuildResourcingTrendPoints(scopedProjects, reportYear, reportMonth, minReportYear);
        var directorateTrendSeries = BuildResourcingGroupTrendSeries(scopedProjects, reportYear, reportMonth, minReportYear, "directorate");
        var businessAreaTrendSeries = BuildResourcingGroupTrendSeries(scopedProjects, reportYear, reportMonth, minReportYear, "businessArea");

        var nextMonthDate = new DateTime(reportYear, reportMonth, 1).AddMonths(1);
        var nextMonthAllowed =
            (nextMonthDate.Year < defaultReportYear ||
             (nextMonthDate.Year == defaultReportYear && nextMonthDate.Month <= defaultReportMonth)) &&
            nextMonthDate.Year <= calendarYearUtc;
        var prevMonthDate = new DateTime(reportYear, reportMonth, 1).AddMonths(-1);
        var earliestReportPeriod = new DateTime(minReportYear, 1, 1);
        var hasPreviousMonthNav = prevMonthDate >= earliestReportPeriod;

        return new ModernResourcingReportViewModel
        {
            ReportYear = reportYear,
            ReportMonth = reportMonth,
            MonthName = new DateTime(reportYear, reportMonth, 1).ToString("MMMM yyyy"),
            MinReportYear = minReportYear,
            MaxReportYear = maxSelectableYear,
            FilterBusinessAreaId = businessAreaId,
            FilterDirectorateId = directorateId,
            Dimension = dimensionKey,
            GroupId = groupId,
            GroupName = groupName,
            GroupOptions = groupOptions,
            BusinessAreas = await _db.BusinessAreaLookups.AsNoTracking()
                .Where(ba => ba.IsActive)
                .OrderBy(ba => ba.SortOrder)
                .ThenBy(ba => ba.Name)
                .ToListAsync(cancellationToken),
            Directorates = await _db.Divisions.AsNoTracking()
                .Where(d => d.IsActive)
                .OrderBy(d => d.SortOrder)
                .ThenBy(d => d.Name)
                .ToListAsync(cancellationToken),
            HasPreviousMonthNav = hasPreviousMonthNav,
            HasNextMonthNav = nextMonthAllowed,
            PreviousNavYear = hasPreviousMonthNav ? prevMonthDate.Year : null,
            PreviousNavMonth = hasPreviousMonthNav ? prevMonthDate.Month : null,
            NextNavYear = nextMonthAllowed ? nextMonthDate.Year : null,
            NextNavMonth = nextMonthAllowed ? nextMonthDate.Month : null,
            TotalPermFte = workItemRows.Sum(r => r.PermFte),
            TotalMspFte = workItemRows.Sum(r => r.MspFte),
            TotalResourcingFte = workItemRows.Sum(r => r.ResourcingFte),
            SubmittedWorkItemCount = workItemRows.Count,
            Bands = bands,
            DirectorateRows = directorateRows,
            BusinessAreaRows = businessAreaRows,
            WorkItemRows = workItemRows,
            TrendPoints = trendPoints,
            DirectorateTrendSeries = directorateTrendSeries,
            BusinessAreaTrendSeries = businessAreaTrendSeries
        };
    }

    public async Task<ModernServiceRegisterReportViewModel> BuildServiceRegisterReportAsync(
        CancellationToken cancellationToken = default)
    {
        var products = await _db.CMDBProducts
            .AsNoTracking()
            .Include(p => p.Phase)
            .Include(p => p.BusinessAreas)
                .ThenInclude(ba => ba.FipsBusinessArea)
                    .ThenInclude(fba => fba.BusinessAreaLookup)
                        .ThenInclude(bal => bal.DivisionBusinessAreas)
                            .ThenInclude(dba => dba.Division)
            .Include(p => p.Channels)
                .ThenInclude(c => c.FipsChannel)
            .Include(p => p.UserGroups)
            .Include(p => p.Types)
                .ThenInclude(t => t.FipsType)
            .Include(p => p.Contacts)
                .ThenInclude(c => c.FipsContactRole)
            .OrderBy(p => p.Title)
            .ThenBy(p => p.UniqueID)
            .ToListAsync(cancellationToken);

        var completionRows = products
            .Select(BuildServiceRegisterProductCompletionRow)
            .OrderBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.UniqueId)
            .ToList();

        var directorateRows = BuildServiceRegisterAreaSummaryRows(
            completionRows,
            r => r.DirectorateNames);
        var businessAreaRows = BuildServiceRegisterAreaSummaryRows(
            completionRows,
            r => r.BusinessAreaNames);

        var activeRows = completionRows.Where(IsServiceRegisterActive).ToList();
        var activeTotal = activeRows.Count;
        const decimal criteriaMaxPerProduct = 6m;
        var activeCriteriaMet = activeRows.Sum(CompletionCriteriaMetCount);
        var activeOverallCompletionPercent = activeTotal == 0
            ? 0m
            : Math.Round((activeCriteriaMet / (activeTotal * criteriaMaxPerProduct)) * 100m, 1, MidpointRounding.AwayFromZero);

        static List<ServiceRegisterProductCompletionRow> OrderCompletionRows(IEnumerable<ServiceRegisterProductCompletionRow> rows) =>
            rows.OrderBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.UniqueId)
                .ToList();

        return new ModernServiceRegisterReportViewModel
        {
            ActiveTotalProducts = activeTotal,
            ActiveOverallCompletionPercent = activeOverallCompletionPercent,
            ActiveFullyCompleteCount = activeRows.Count(r => r.CompletionPercent >= 100),
            ActiveProductsWithoutUrlCount = activeRows.Count(r => !r.HasProductUrl),
            ActiveProductsWithoutServiceOwnerOrSroCount = activeRows.Count(r =>
                !r.HasServiceOwner || !r.HasSeniorResponsibleOfficer),
            ActiveCount = completionRows.Count(IsServiceRegisterActive),
            RejectedCount = completionRows.Count(IsServiceRegisterRejected),
            RetiredCount = completionRows.Count(IsServiceRegisterRetired),
            NewCount = completionRows.Count(IsServiceRegisterNew),
            EnterpriseCount = completionRows.Count(r => r.IsEnterprise),
            DirectorateRows = directorateRows,
            BusinessAreaRows = businessAreaRows,
            ActiveCompletionRows = OrderCompletionRows(activeRows),
            EnterpriseCompletionRows = OrderCompletionRows(completionRows.Where(r => r.IsEnterprise)),
            RetiredCompletionRows = OrderCompletionRows(completionRows.Where(IsServiceRegisterRetired)),
            NewCompletionRows = OrderCompletionRows(completionRows.Where(IsServiceRegisterNew)),
            RejectedCompletionRows = OrderCompletionRows(completionRows.Where(IsServiceRegisterRejected))
        };
    }

    public async Task<ModernMonthlyReportDashboardViewModel> BuildDashboardAsync(
        int? year,
        int? month,
        int? businessAreaId,
        int? directorateId,
        PrioritiesReportOptions? prioritiesReport = null,
        CancellationToken cancellationToken = default)
    {
        var currentDate = DateTime.UtcNow;
        var calendarYearUtc = currentDate.Year;
        var currentMonth = currentDate.Month;

        const int minReportYear = 2026;
        var maxSelectableYear = calendarYearUtc >= minReportYear ? calendarYearUtc : minReportYear;

        var currentPeriodDueDate = _monthlyUpdateService.GetMonthlyUpdateDueDate(calendarYearUtc, currentMonth);
        var daysUntilCurrentPeriodDueDate = (currentPeriodDueDate - currentDate).Days;

        var defaultReportYear = daysUntilCurrentPeriodDueDate <= 10 ? calendarYearUtc : (currentMonth == 1 ? calendarYearUtc - 1 : calendarYearUtc);
        var defaultReportMonth = daysUntilCurrentPeriodDueDate <= 10 ? currentMonth : (currentMonth == 1 ? 12 : currentMonth - 1);

        defaultReportYear = Math.Max(minReportYear, defaultReportYear);

        var reportYear = year ?? defaultReportYear;
        var reportMonth = month ?? defaultReportMonth;
        if (reportMonth < 1 || reportMonth > 12)
            reportMonth = defaultReportMonth;

        reportYear = Math.Clamp(reportYear, minReportYear, maxSelectableYear);

        var monthStart = new DateTime(reportYear, reportMonth, 1);
        var monthEnd = monthStart.AddMonths(1).AddDays(-1).AddHours(23).AddMinutes(59).AddSeconds(59);
        var todayUtc = DateTime.UtcNow.Date;

        var upcomingWindowEnd = monthStart.AddDays(30);

        var query = _db.Projects
            .AsNoTracking()
            .Include(p => p.PrimaryContactUser)
            .Include(p => p.DeliveryPriority)
            .Include(p => p.BusinessAreaLookup)
            .Include(p => p.Milestones)
            .Include(p => p.MonthlyUpdates)
                .ThenInclude(mu => mu.MonthlyUpdateNarratives)
            .Include(p => p.RagStatusLookup)
            .Include(p => p.RagHistory)
            .Include(p => p.Directorates)
            .Where(p => !p.IsDeleted && p.Status != "Cancelled" && p.Status != "Completed");

        if (prioritiesReport != null)
        {
            query = query
                .Include(p => p.ProjectMissions)
                    .ThenInclude(pm => pm.Mission)
                .Include(p => p.ProjectObjectives)
                    .ThenInclude(po => po.Objective);
        }

        if (businessAreaId.HasValue)
            query = query.Where(p => p.BusinessAreaId == businessAreaId.Value);
        if (directorateId.HasValue)
            query = query.Where(p => p.Directorates.Any(d => d.DivisionId == directorateId.Value));

        var allProjects = await query.ToListAsync(cancellationToken);

        PrioritiesReportContext? prioritiesContext = null;
        if (prioritiesReport != null)
        {
            var dim = prioritiesReport.Dimension;
            prioritiesContext = new PrioritiesReportContext
            {
                Dimension = dim,
                GroupColumnLabel = GetPrioritiesDimensionLabel(dim),
                GroupOptions = BuildPrioritiesGroupOptions(allProjects, dim)
            };

            if (prioritiesReport.GroupId is int gid && !prioritiesReport.IncludeAllDimensions)
            {
                allProjects = FilterProjectsByPrioritiesGroup(allProjects, dim, gid);
                prioritiesContext.FilterGroupId = gid;
                prioritiesContext.FilterGroupName = prioritiesContext.GroupOptions
                    .FirstOrDefault(o => o.GroupId == gid)?.Name;
            }
        }

        var businessAreas = await _db.BusinessAreaLookups
            .AsNoTracking()
            .Where(ba => ba.IsActive)
            .OrderBy(ba => ba.SortOrder)
            .ThenBy(ba => ba.Name)
            .ToListAsync(cancellationToken);

        var directorates = await _db.Divisions
            .AsNoTracking()
            .Where(d => d.IsActive)
            .OrderBy(d => d.SortOrder)
            .ThenBy(d => d.Name)
            .ToListAsync(cancellationToken);

        var totalActiveProjects = allProjects.Count;
        var newProjectsThisMonth = allProjects
            .Where(p => p.CreatedAt >= monthStart && p.CreatedAt <= monthEnd)
            .OrderByDescending(p => p.CreatedAt)
            .ToList();

        var milestonesAchieved = allProjects
            .SelectMany(p => p.Milestones
                .Where(m => !m.IsDeleted &&
                            m.Status == "complete" &&
                            m.ActualDate.HasValue &&
                            m.ActualDate.Value >= monthStart &&
                            m.ActualDate.Value <= monthEnd)
                .Select(m => new MilestoneWithProject { Project = p, Milestone = m }))
            .OrderBy(x => x.Milestone.ActualDate)
            .ToList();

        var upcomingMilestones30 = allProjects
            .SelectMany(p => p.Milestones
                .Where(m => !m.IsDeleted &&
                            m.Status != "complete" &&
                            m.Status != "cancelled" &&
                            m.DueDate >= monthStart &&
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

        var monthlyUpdateStats = CalculateMonthlyUpdateStats(allProjects, reportYear, reportMonth, _monthlyUpdateService);
        var dueDateForSubmission = _monthlyUpdateService.GetMonthlyUpdateDueDate(reportYear, reportMonth);
        var nowUtcForSubmission = DateTime.UtcNow;

        var ragDistribution = BuildRagDistribution(allProjects);
        var priorityDistribution = BuildPriorityDistribution(allProjects);

        if (prioritiesReport?.IncludeAllDimensions == true && prioritiesContext != null)
        {
            prioritiesContext.DimensionSections = BuildAllPrioritiesDimensionSections(
                allProjects,
                reportYear,
                reportMonth,
                monthStart,
                monthEnd,
                upcomingWindowEnd,
                todayUtc,
                nowUtcForSubmission,
                dueDateForSubmission);
        }

        var businessAreaRows = prioritiesReport?.IncludeAllDimensions == true
            ? prioritiesContext!.DimensionSections.FirstOrDefault()?.Rows ?? new List<ModernBusinessAreaDashboardRow>()
            : prioritiesReport != null
            ? BuildPrioritiesGroupRows(
                allProjects,
                prioritiesReport.Dimension,
                reportYear,
                reportMonth,
                monthStart,
                monthEnd,
                upcomingWindowEnd,
                todayUtc,
                nowUtcForSubmission,
                dueDateForSubmission)
            : BuildBusinessAreaDashboardRows(
                allProjects,
                reportYear,
                reportMonth,
                monthStart,
                monthEnd,
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

        var prevMonth = reportMonth == 1 ? 12 : reportMonth - 1;
        var prevYear = reportMonth == 1 ? reportYear - 1 : reportYear;
        var prevMonthStart = new DateTime(prevYear, prevMonth, 1);
        var prevMonthName = prevMonthStart.ToString("MMMM yyyy");

        var prevQuery = _db.Projects
            .AsNoTracking()
            .Include(p => p.MonthlyUpdates)
                .ThenInclude(mu => mu.MonthlyUpdateNarratives)
            .Include(p => p.RagStatusLookup)
            .Include(p => p.DeliveryPriority)
            .Include(p => p.Directorates)
            .Where(p => !p.IsDeleted && p.Status != "Cancelled" && p.Status != "Completed");
        if (businessAreaId.HasValue)
            prevQuery = prevQuery.Where(p => p.BusinessAreaId == businessAreaId.Value);
        if (directorateId.HasValue)
            prevQuery = prevQuery.Where(p => p.Directorates.Any(d => d.DivisionId == directorateId.Value));

        if (prioritiesReport != null)
        {
            prevQuery = prevQuery
                .Include(p => p.ProjectMissions)
                .Include(p => p.ProjectObjectives);
        }

        var prevMonthProjects = await prevQuery.ToListAsync(cancellationToken);
        if (prioritiesReport?.GroupId is int prevGroupId)
            prevMonthProjects = FilterProjectsByPrioritiesGroup(prevMonthProjects, prioritiesReport.Dimension, prevGroupId);

        var prevMonthRagDistribution = await BuildPrevMonthRagDistributionAsync(prevMonthProjects, monthStart, cancellationToken);
        var prevMonthPriorityDistribution = BuildPriorityDistribution(prevMonthProjects);

        var allowedProjectIds = allProjects.Select(p => p.Id).ToHashSet();

        var ragHistoryDuringMonth = await _db.ProjectRagHistories
            .AsNoTracking()
            .Include(rh => rh.RagStatusLookup)
            .Where(rh => rh.ChangedAt >= monthStart && rh.ChangedAt <= monthEnd && allowedProjectIds.Contains(rh.ProjectId))
            .ToListAsync(cancellationToken);

        var projectsWithRagChange = ragHistoryDuringMonth.Select(rh => rh.ProjectId).Distinct().Count();

        var projectsWithPriorityChange = allProjects
            .Where(p => p.UpdatedAt >= monthStart &&
                        p.UpdatedAt <= monthEnd &&
                        prevMonthProjects.Any(pp => pp.Id == p.Id &&
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

        var ragTrend = BuildRagTrend(allProjects, historyByProject, reportYear, reportMonth);
        var priorityTrend = BuildPriorityTrend(allProjects, reportYear, reportMonth);

        var ragChanges = BuildRagChangeDetails(allProjects, ragHistoryDuringMonth, historyByProject, monthStart, reportYear, reportMonth);
        var priorityChanges = BuildPriorityChangeDetails(allProjects, prevMonthProjects, monthStart, monthEnd, reportYear, reportMonth);

        var ragSixMonthTrendRows = MonthlyReportRagTrendAnalyzer.Build(
            allProjects,
            historyByProject,
            reportYear,
            reportMonth,
            ResolveRagAtCutoff);

        var nextMonthDate = monthStart.AddMonths(1);
        var nextMonthAllowed =
            (nextMonthDate.Year < defaultReportYear ||
             (nextMonthDate.Year == defaultReportYear && nextMonthDate.Month <= defaultReportMonth)) &&
            nextMonthDate.Year <= calendarYearUtc;
        var prevMonthDate = monthStart.AddMonths(-1);
        var earliestReportPeriod = new DateTime(minReportYear, 1, 1);
        var hasPreviousMonthNav = prevMonthDate >= earliestReportPeriod;

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
            // Optional reporting enhancement — do not fail the monthly report page
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
            monthStart.ToString("MMMM yyyy"),
            prevMonthName,
            filterBaName,
            allProjects,
            prevMonthProjects,
            monthlyUpdateStats,
            ragChanges,
            priorityChanges,
            raidSummary,
            accessibilityAreaRows,
            accessibilitySummary,
            monthStart,
            monthEnd,
            cancellationToken);

        string? businessAreaNarrative = null;
        if (businessAreaId.HasValue)
        {
            var baName = businessAreas.FirstOrDefault(x => x.Id == businessAreaId.Value)?.Name ?? "This business area";
            businessAreaNarrative = BuildBusinessAreaSummaryNarrative(
                baName,
                monthStart.ToString("MMMM yyyy"),
                monthStart,
                reportYear,
                reportMonth,
                allProjects,
                newProjectsThisMonth.Count,
                milestonesAchieved.Count,
                upcomingMilestones30,
                lateMilestones,
                monthlyUpdateStats,
                ragDistribution,
                priorityDistribution,
                prevMonthRagDistribution,
                prevMonthName,
                ragTrend,
                projectsWithPathToGreen.Count,
                projectsWithRagChange,
                projectsWithPriorityChange,
                ragChanges,
                priorityChanges);
        }

        return new ModernMonthlyReportDashboardViewModel
        {
            ReportYear = reportYear,
            ReportMonth = reportMonth,
            MonthName = monthStart.ToString("MMMM yyyy"),
            MonthStart = monthStart,
            MonthEnd = monthEnd,
            DefaultReportYear = defaultReportYear,
            DefaultReportMonth = defaultReportMonth,
            FilterBusinessAreaId = businessAreaId,
            FilterDirectorateId = directorateId,
            BusinessAreas = businessAreas,
            Directorates = directorates,
            TotalActiveProjects = totalActiveProjects,
            NewProjectsCount = newProjectsThisMonth.Count,
            MilestonesAchievedCount = milestonesAchieved.Count,
            NewProjectsThisMonth = newProjectsThisMonth,
            MilestonesAchieved = milestonesAchieved,
            UpcomingMilestonesNext30Days = upcomingMilestones30,
            LateMilestones = lateMilestones,
            MonthlyUpdateStats = monthlyUpdateStats,
            BusinessAreaSubmissionProgress = businessAreaSubmissionProgress,
            RagDistribution = ragDistribution,
            PriorityDistribution = priorityDistribution,
            PrevMonthRagDistribution = prevMonthRagDistribution,
            PrevMonthPriorityDistribution = prevMonthPriorityDistribution,
            PrevMonthName = prevMonthName,
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
            MinReportYear = minReportYear,
            MaxReportYear = maxSelectableYear,
            HasPreviousMonthNav = hasPreviousMonthNav,
            HasNextMonthNav = nextMonthAllowed,
            PreviousNavYear = hasPreviousMonthNav ? prevMonthDate.Year : null,
            PreviousNavMonth = hasPreviousMonthNav ? prevMonthDate.Month : null,
            NextNavYear = nextMonthAllowed ? nextMonthDate.Year : null,
            NextNavMonth = nextMonthAllowed ? nextMonthDate.Month : null,
            AccessibilitySummary = accessibilitySummary,
            AccessibilitySummaryError = accessibilityError,
            AccessibilityAreaRows = accessibilityAreaRows,
            AccessibilityIssueCriteria = accessibilityIssueCriteria,
            RaidSummary = raidSummary,
            Intelligence = intelligence,
            PrioritiesReport = prioritiesContext,
            ScopeProjectItems = allProjects
                .Select(p => ToBusinessAreaProjectItem(p, reportYear, reportMonth, monthStart, monthEnd, upcomingWindowEnd, todayUtc))
                .OrderBy(x => x.Title)
                .ToList(),
            RagSixMonthTrendRows = ragSixMonthTrendRows
        };
    }

    private static string NormalizePrioritiesDimension(string? dimension)
    {
        var d = (dimension ?? "mission").Trim().ToLowerInvariant();
        return d is "outcomes" or "priority" ? d : "mission";
    }

    private static string NormalizeResourcingDimension(string? dimension)
    {
        var d = (dimension ?? "all").Trim().ToLowerInvariant();
        return d is "mission" or "outcomes" or "priority" ? d : "all";
    }

    private static ServiceRegisterProductCompletionRow BuildServiceRegisterProductCompletionRow(CMDBProduct product)
    {
        var hasPhase = product.PhaseId.HasValue;
        var hasBusinessArea = product.BusinessAreas.Any();
        var hasChannel = product.Channels.Any();
        var hasUserGroup = product.UserGroups.Any();
        var hasType = product.Types.Any();
        var contactCount = product.Contacts.Count;
        var hasAnyContact = contactCount > 0;
        var hasServiceOwner = product.Contacts.Any(c =>
            string.Equals(c.FipsContactRole?.Name, "Service Owner", StringComparison.OrdinalIgnoreCase));
        var hasSro = product.Contacts.Any(c =>
            string.Equals(c.FipsContactRole?.Name, "Senior Responsible Officer", StringComparison.OrdinalIgnoreCase));
        var hasProductUrl = !string.IsNullOrWhiteSpace(product.ProductURL);

        var businessAreaNames = product.BusinessAreas
            .Select(ba => ba.FipsBusinessArea?.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var directorateNames = product.BusinessAreas
            .SelectMany(ba => ba.FipsBusinessArea?.BusinessAreaLookup?.DivisionBusinessAreas ?? Array.Empty<DivisionBusinessArea>())
            .Select(dba => dba.Division?.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (businessAreaNames.Count == 0)
            businessAreaNames.Add("Not set");
        if (directorateNames.Count == 0)
            directorateNames.Add("Not set");

        var missingFields = new List<string>();
        if (!hasAnyContact) missingFields.Add("At least 1 contact");
        if (!hasPhase) missingFields.Add("Phase");
        if (!hasBusinessArea) missingFields.Add("Business area");
        if (!hasChannel) missingFields.Add("At least 1 channel");
        if (!hasUserGroup) missingFields.Add("At least 1 user group");
        if (!hasType) missingFields.Add("At least 1 type");

        var criteriaMet = 0;
        if (hasAnyContact) criteriaMet++;
        if (hasPhase) criteriaMet++;
        if (hasBusinessArea) criteriaMet++;
        if (hasChannel) criteriaMet++;
        if (hasUserGroup) criteriaMet++;
        if (hasType) criteriaMet++;
        var completionPercent = (int)Math.Round((criteriaMet / 6m) * 100m, 0, MidpointRounding.AwayFromZero);

        var missingOwnerOrSro = new List<string>();
        if (!hasServiceOwner) missingOwnerOrSro.Add("Service owner");
        if (!hasSro) missingOwnerOrSro.Add("Senior Responsible Officer");

        var channelNames = product.Channels
            .Select(c => c.FipsChannel?.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var typeNames = product.Types
            .Select(t => t.FipsType?.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var phaseDisplay = string.IsNullOrWhiteSpace(product.Phase?.Name) ? "Not set" : product.Phase.Name.Trim();

        return new ServiceRegisterProductCompletionRow
        {
            ProductId = product.Id,
            UniqueId = product.UniqueID,
            Title = product.Title,
            StatusLabel = ServiceRegisterStatusLabel(product.Status),
            IsEnterprise = product.IsEnterpriseService && product.Status == CMDBProductStatus.Active,
            CompletionPercent = completionPercent,
            MissingFields = missingFields.Count == 0 ? "None" : string.Join(", ", missingFields),
            HasProductUrl = hasProductUrl,
            ProductUrl = product.ProductURL,
            ContactCount = contactCount,
            HasPhase = hasPhase,
            HasBusinessArea = hasBusinessArea,
            HasChannel = hasChannel,
            HasUserGroup = hasUserGroup,
            HasType = hasType,
            HasServiceOwner = hasServiceOwner,
            HasSeniorResponsibleOfficer = hasSro,
            MissingOwnerOrSro = missingOwnerOrSro.Count == 0 ? "None" : string.Join(", ", missingOwnerOrSro),
            PhaseDisplay = phaseDisplay,
            ChannelsDisplay = channelNames.Count == 0 ? "Not set" : string.Join(", ", channelNames),
            TypesDisplay = typeNames.Count == 0 ? "Not set" : string.Join(", ", typeNames),
            BusinessAreasDisplay = string.Join(", ", businessAreaNames),
            DirectoratesDisplay = string.Join(", ", directorateNames),
            BusinessAreaNames = businessAreaNames,
            DirectorateNames = directorateNames
        };
    }

    private static List<ServiceRegisterAreaSummaryRow> BuildServiceRegisterAreaSummaryRows(
        List<ServiceRegisterProductCompletionRow> rows,
        Func<ServiceRegisterProductCompletionRow, IEnumerable<string>> namesSelector)
    {
        var linked = new List<(string Name, ServiceRegisterProductCompletionRow Row)>();
        foreach (var row in rows)
        {
            var names = namesSelector(row)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (names.Count == 0)
                names.Add("Not set");
            foreach (var name in names)
                linked.Add((name.Trim(), row));
        }

        return linked
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var products = g.Select(x => x.Row).DistinctBy(r => r.ProductId).ToList();
                return new ServiceRegisterAreaSummaryRow
                {
                    Name = g.Key,
                    ProductCount = products.Count,
                    ActiveCount = products.Count(IsServiceRegisterActive),
                    RejectedCount = products.Count(IsServiceRegisterRejected),
                    RetiredCount = products.Count(IsServiceRegisterRetired),
                    NewCount = products.Count(IsServiceRegisterNew),
                    EnterpriseCount = products.Count(r => r.IsEnterprise),
                    AverageCompletionPercent = products.Count == 0
                        ? 0m
                        : Math.Round(products.Average(r => (decimal)r.CompletionPercent), 1, MidpointRounding.AwayFromZero)
                };
            })
            .OrderByDescending(r => r.ProductCount)
            .ThenBy(r => string.Equals(r.Name, "Not set", StringComparison.OrdinalIgnoreCase) ? "zzzzzz" : r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int CompletionCriteriaMetCount(ServiceRegisterProductCompletionRow row)
    {
        var met = 0;
        if (row.ContactCount > 0) met++;
        if (row.HasPhase) met++;
        if (row.HasBusinessArea) met++;
        if (row.HasChannel) met++;
        if (row.HasUserGroup) met++;
        if (row.HasType) met++;
        return met;
    }

    private static string ServiceRegisterStatusLabel(CMDBProductStatus status) => status switch
    {
        CMDBProductStatus.Active => "Active",
        CMDBProductStatus.Rejected => "Rejected",
        CMDBProductStatus.Inactive => "Retired",
        CMDBProductStatus.New => "New",
        _ => "New"
    };

    private static bool IsServiceRegisterActive(ServiceRegisterProductCompletionRow row) =>
        string.Equals(row.StatusLabel, "Active", StringComparison.OrdinalIgnoreCase);

    private static bool IsServiceRegisterRejected(ServiceRegisterProductCompletionRow row) =>
        string.Equals(row.StatusLabel, "Rejected", StringComparison.OrdinalIgnoreCase);

    private static bool IsServiceRegisterRetired(ServiceRegisterProductCompletionRow row) =>
        string.Equals(row.StatusLabel, "Retired", StringComparison.OrdinalIgnoreCase);

    private static bool IsServiceRegisterNew(ServiceRegisterProductCompletionRow row) =>
        string.Equals(row.StatusLabel, "New", StringComparison.OrdinalIgnoreCase);

    private static (string Name, string? CssClass) ResolveResourceBand(decimal value, List<ResourcingBandViewModel> bands)
    {
        if (value <= 0m)
            return ("—", null);
        var match = bands.FirstOrDefault(b => value >= b.MinFte && (!b.MaxFte.HasValue || value <= b.MaxFte.Value));
        if (match == null)
            return ("Unmapped", null);
        return (match.Name, match.CssClass);
    }

    private static ResourcingAggregateRow BuildResourcingAggregateRow(
        string name,
        int? groupId,
        List<ResourcingWorkItemRow> rows,
        List<ResourcingBandViewModel> bands)
    {
        var permTotal = rows.Sum(r => r.PermFte);
        var mspTotal = rows.Sum(r => r.MspFte);
        var total = permTotal + mspTotal;
        var band = ResolveResourceBand(total, bands);

        return new ResourcingAggregateRow
        {
            Name = name,
            GroupId = groupId,
            WorkItemCount = rows.Select(r => r.WorkItemId).Distinct().Count(),
            PermFteTotal = permTotal,
            MspFteTotal = mspTotal,
            ResourcingFteTotal = total,
            BandName = band.Name,
            BandCssClass = band.CssClass,
            ProjectIds = rows.Select(r => r.WorkItemId).Distinct().ToList()
        };
    }

    private static List<ResourcingTrendMonthPoint> BuildResourcingTrendPoints(List<Project> scopedProjects, int reportYear, int reportMonth, int startYear)
    {
        var monthCursor = new DateTime(startYear, 1, 1);
        var endMonth = new DateTime(reportYear, reportMonth, 1);
        var points = new List<ResourcingTrendMonthPoint>();
        while (monthCursor <= endMonth)
        {
            var y = monthCursor.Year;
            var m = monthCursor.Month;
            decimal perm = 0m;
            decimal msp = 0m;
            var submittedCount = 0;
            var workItemIds = new List<int>();
            foreach (var p in scopedProjects)
            {
                var update = p.MonthlyUpdates.FirstOrDefault(u =>
                    u.Year == y &&
                    u.Month == m &&
                    u.SubmittedAt.HasValue);
                if (update == null)
                    continue;

                submittedCount++;
                workItemIds.Add(p.Id);
                perm += update.MonthlyPermFte ?? 0m;
                msp += update.MonthlyMspFte ?? 0m;
            }

            points.Add(new ResourcingTrendMonthPoint
            {
                Year = y,
                Month = m,
                Label = monthCursor.ToString("MMM yyyy"),
                PermFteTotal = perm,
                MspFteTotal = msp,
                ResourcingFteTotal = perm + msp,
                SubmittedWorkItemCount = submittedCount,
                WorkItemIds = workItemIds
            });

            monthCursor = monthCursor.AddMonths(1);
        }

        return points;
    }

    private static List<ResourcingGroupTrendSeries> BuildResourcingGroupTrendSeries(
        List<Project> scopedProjects,
        int reportYear,
        int reportMonth,
        int startYear,
        string groupBy)
    {
        var grouped = groupBy == "directorate"
            ? scopedProjects
                .SelectMany(p =>
                {
                    var dirs = p.Directorates
                        .Where(d => d.Division != null && !string.IsNullOrWhiteSpace(d.Division!.Name))
                        .Select(d => d.Division!.Name.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    if (dirs.Count == 0)
                        dirs = new List<string> { "Not set" };
                    return dirs.Select(name => (GroupName: name, Project: p));
                })
                .GroupBy(x => x.GroupName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Select(x => x.Project).ToList(), StringComparer.OrdinalIgnoreCase)
            : scopedProjects
                .GroupBy(p => p.BusinessAreaLookup?.Name ?? "Not set", StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        return grouped
            .Select(kvp => new ResourcingGroupTrendSeries
            {
                GroupName = kvp.Key,
                Points = BuildResourcingTrendPoints(kvp.Value, reportYear, reportMonth, startYear)
            })
            .OrderByDescending(s => s.Points.LastOrDefault()?.ResourcingFteTotal ?? 0m)
            .ThenBy(s => s.GroupName == "Not set" ? "zzzzzz" : s.GroupName)
            .ToList();
    }

    private static string GetPrioritiesDimensionLabel(string dimension) => dimension switch
    {
        "outcomes" => "Priority outcome",
        "priority" => "Delivery priority",
        _ => "Mission pillar"
    };

    private List<PrioritiesReportDimensionSection> BuildAllPrioritiesDimensionSections(
        List<Project> allProjects,
        int reportYear,
        int reportMonth,
        DateTime monthStart,
        DateTime monthEnd,
        DateTime upcomingWindowEnd,
        DateTime todayUtc,
        DateTime nowUtcForSubmission,
        DateTime dueDateForSubmission) =>
        new[] { "mission", "outcomes", "priority" }
            .Select(dim => new PrioritiesReportDimensionSection
            {
                Dimension = dim,
                GroupColumnLabel = GetPrioritiesDimensionLabel(dim),
                Rows = BuildPrioritiesGroupRows(
                    allProjects,
                    dim,
                    reportYear,
                    reportMonth,
                    monthStart,
                    monthEnd,
                    upcomingWindowEnd,
                    todayUtc,
                    nowUtcForSubmission,
                    dueDateForSubmission)
            })
            .ToList();

    private static List<Project> FilterProjectsByPrioritiesGroup(List<Project> projects, string dimension, int groupId)
    {
        if (groupId == 0)
        {
            return dimension switch
            {
                "mission" => projects.Where(p => p.ProjectMissions == null || !p.ProjectMissions.Any()).ToList(),
                "outcomes" => projects.Where(p => p.ProjectObjectives == null || !p.ProjectObjectives.Any()).ToList(),
                "priority" => projects.Where(p => !p.DeliveryPriorityId.HasValue).ToList(),
                _ => projects
            };
        }

        return dimension switch
        {
            "mission" => projects.Where(p => p.ProjectMissions.Any(pm => pm.MissionId == groupId)).ToList(),
            "outcomes" => projects.Where(p => p.ProjectObjectives.Any(po => po.ObjectiveId == groupId)).ToList(),
            "priority" => projects.Where(p => p.DeliveryPriorityId == groupId).ToList(),
            _ => projects
        };
    }

    private static List<PrioritiesReportGroupOption> BuildPrioritiesGroupOptions(List<Project> projects, string dimension)
    {
        return ExpandProjectsForPrioritiesDimension(projects, dimension)
            .GroupBy(x => (x.GroupId, x.GroupName))
            .Select(g => new PrioritiesReportGroupOption
            {
                GroupId = g.Key.GroupId ?? 0,
                Name = g.Key.GroupName,
                WorkItemCount = g.Select(x => x.Project.Id).Distinct().Count()
            })
            .OrderBy(o => o.Name == "Not set" ? "zzzzzz" : o.Name)
            .ToList();
    }

    private static IEnumerable<(int? GroupId, string GroupName, Project Project)> ExpandProjectsForPrioritiesDimension(
        IEnumerable<Project> projects,
        string dimension)
    {
        foreach (var p in projects)
        {
            switch (dimension)
            {
                case "mission":
                    if (p.ProjectMissions is { Count: > 0 })
                    {
                        foreach (var pm in p.ProjectMissions.Where(pm => pm.Mission is { IsDeleted: false }))
                            yield return (pm.MissionId, pm.Mission!.Title, p);
                    }
                    else
                        yield return (null, "Not set", p);
                    break;
                case "outcomes":
                    if (p.ProjectObjectives is { Count: > 0 })
                    {
                        foreach (var po in p.ProjectObjectives.Where(po =>
                                     po.Objective is { IsDeleted: false } &&
                                     string.Equals(po.Objective.Status, "active", StringComparison.OrdinalIgnoreCase)))
                            yield return (po.ObjectiveId, po.Objective!.Title, p);
                    }
                    else
                        yield return (null, "Not set", p);
                    break;
                default:
                    yield return (p.DeliveryPriorityId, p.DeliveryPriority?.Name ?? "Not set", p);
                    break;
            }
        }
    }

    private List<ModernBusinessAreaDashboardRow> BuildPrioritiesGroupRows(
        List<Project> allProjects,
        string dimension,
        int reportYear,
        int reportMonth,
        DateTime monthStart,
        DateTime monthEnd,
        DateTime upcomingWindowEnd,
        DateTime todayUtc,
        DateTime nowUtcForSubmission,
        DateTime dueDateForSubmission) =>
        ExpandProjectsForPrioritiesDimension(allProjects, dimension)
            .GroupBy(x => (x.GroupId, x.GroupName))
            .Select(g =>
            {
                var distinctProjects = g.Select(x => x.Project).GroupBy(p => p.Id).Select(pg => pg.First()).ToList();
                return BuildDashboardRowFromProjects(
                    distinctProjects,
                    g.Key.GroupName,
                    g.Key.GroupId,
                    reportYear,
                    reportMonth,
                    monthStart,
                    monthEnd,
                    upcomingWindowEnd,
                    todayUtc,
                    nowUtcForSubmission,
                    dueDateForSubmission);
            })
            .OrderByDescending(r => r.TotalProjects)
            .ThenBy(r => r.BusinessArea == "Not set" ? "zzzzzz" : r.BusinessArea)
            .ToList();

    private List<ModernBusinessAreaDashboardRow> BuildBusinessAreaDashboardRows(
        List<Project> allProjects,
        int reportYear,
        int reportMonth,
        DateTime monthStart,
        DateTime monthEnd,
        DateTime upcomingWindowEnd,
        DateTime todayUtc,
        DateTime nowUtcForSubmission,
        DateTime dueDateForSubmission) =>
        allProjects
            .GroupBy(p => p.BusinessAreaId)
            .Select(g => BuildDashboardRowFromProjects(
                g.ToList(),
                g.First().BusinessAreaLookup?.Name ?? "Not set",
                g.Key,
                reportYear,
                reportMonth,
                monthStart,
                monthEnd,
                upcomingWindowEnd,
                todayUtc,
                nowUtcForSubmission,
                dueDateForSubmission))
            .OrderByDescending(r => r.CompletionRatePercent)
            .ThenBy(r => r.BusinessArea == "Not set" ? "zzzzzz" : r.BusinessArea)
            .ToList();

    private ModernBusinessAreaDashboardRow BuildDashboardRowFromProjects(
        List<Project> projects,
        string groupName,
        int? groupId,
        int reportYear,
        int reportMonth,
        DateTime monthStart,
        DateTime monthEnd,
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
            var update = p.MonthlyUpdates?.FirstOrDefault(u => u.Year == reportYear && u.Month == reportMonth);
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
            NewThisMonth = projects.Count(p => p.CreatedAt >= monthStart && p.CreatedAt <= monthEnd),
            MilestonesCompleted = projects.SelectMany(p => p.Milestones
                .Where(m => !m.IsDeleted &&
                            m.Status == "complete" &&
                            m.ActualDate.HasValue &&
                            m.ActualDate.Value >= monthStart &&
                            m.ActualDate.Value <= monthEnd)).Count(),
            MilestonesUpcoming30Days = projects.SelectMany(p => p.Milestones
                .Where(m => !m.IsDeleted &&
                            m.Status != "complete" &&
                            m.Status != "cancelled" &&
                            m.DueDate >= monthStart &&
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
                .Select(p => ToBusinessAreaProjectItem(p, reportYear, reportMonth, monthStart, monthEnd, upcomingWindowEnd, todayUtc))
                .OrderBy(x => x.Title)
                .ToList()
        };
    }

    private static BusinessAreaProjectItem ToBusinessAreaProjectItem(
        Project p,
        int reportYear,
        int reportMonth,
        DateTime monthStart,
        DateTime monthEnd,
        DateTime upcomingWindowEnd,
        DateTime todayUtc)
    {
        var periodUpdate = p.MonthlyUpdates?.FirstOrDefault(u => u.Year == reportYear && u.Month == reportMonth);
        var latestSubmitted = p.MonthlyUpdates?
            .Where(u => u.SubmittedAt.HasValue)
            .OrderByDescending(u => u.Year)
            .ThenByDescending(u => u.Month)
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
            LatestMonthlyUpdateNarrative = ProjectMonthlyUpdateNarrative.LatestSubmittedText(p),
            Rag = RagBucket(p),
            Priority = PriorityBucket(p),
            PermFte = periodUpdate?.MonthlyPermFte ?? latestSubmitted?.MonthlyPermFte,
            MspFte = periodUpdate?.MonthlyMspFte ?? latestSubmitted?.MonthlyMspFte,
            MilestonesSummary = BuildMilestonesSummary(p, monthStart, monthEnd, upcomingWindowEnd, todayUtc),
            SubmittedUpdate = periodUpdate?.SubmittedAt.HasValue == true,
            IsNew = p.CreatedAt >= monthStart && p.CreatedAt <= monthEnd,
            HasMilestoneCompletedInPeriod = p.Milestones.Any(m =>
                !m.IsDeleted &&
                m.Status == "complete" &&
                m.ActualDate.HasValue &&
                m.ActualDate.Value >= monthStart &&
                m.ActualDate.Value <= monthEnd),
            HasMilestoneUpcomingInWindow = p.Milestones.Any(m =>
                !m.IsDeleted &&
                m.Status != "complete" &&
                m.Status != "cancelled" &&
                m.DueDate >= monthStart &&
                m.DueDate < upcomingWindowEnd),
            HasLateMilestone = p.Milestones.Any(m =>
                !m.IsDeleted &&
                m.Status != "complete" &&
                m.Status != "cancelled" &&
                m.DueDate.Date < todayUtc)
        };
    }

    internal static List<BusinessAreaProjectItem> FilterDrilldownItems(
        IEnumerable<BusinessAreaProjectItem> items,
        string filter)
    {
        var list = items.ToList();
        if (filter.StartsWith("mx-", StringComparison.Ordinal))
        {
            var parts = filter.AsSpan(3).ToString().Split('|');
            if (parts.Length == 2)
                return list.Where(p => p.Rag == parts[0] && p.Priority == parts[1]).ToList();
        }

        return filter switch
        {
            "total" => list,
            "submitted" => list.Where(p => p.SubmittedUpdate).ToList(),
            "new" => list.Where(p => p.IsNew).ToList(),
            "ms-done" => list.Where(p => p.HasMilestoneCompletedInPeriod).ToList(),
            "ms-soon" => list.Where(p => p.HasMilestoneUpcomingInWindow).ToList(),
            "ms-late" => list.Where(p => p.HasLateMilestone).ToList(),
            _ when filter.StartsWith("rag-", StringComparison.Ordinal) =>
                list.Where(p => p.Rag == filter[4..]).ToList(),
            _ when filter.StartsWith("pri-", StringComparison.Ordinal) =>
                list.Where(p => p.Priority == filter[4..]).ToList(),
            _ => list
        };
    }

    internal static IReadOnlyList<BusinessAreaProjectItem> SortDrilldownItems(IEnumerable<BusinessAreaProjectItem> items) =>
        items
            .OrderBy(p => PrioritySortKey(p.Priority))
            .ThenBy(p => RagSortKey(p.Rag))
            .ThenBy(p => p.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static int PrioritySortKey(string priority) => priority switch
    {
        "Critical" => 0,
        "High" => 1,
        "Medium" => 2,
        "Low" => 3,
        _ => 4
    };

    private static int RagSortKey(string rag) => rag switch
    {
        "Red" => 0,
        "Amber-Red" => 1,
        "Amber-Green" => 2,
        "Green" => 3,
        _ => 4
    };

    private static string? BuildMilestonesSummary(
        Project p,
        DateTime monthStart,
        DateTime monthEnd,
        DateTime upcomingWindowEnd,
        DateTime todayUtc)
    {
        var lines = new List<string>();
        foreach (var m in p.Milestones.Where(x => !x.IsDeleted).OrderBy(x => x.DueDate))
        {
            if (m.Status == "complete" &&
                m.ActualDate.HasValue &&
                m.ActualDate.Value >= monthStart &&
                m.ActualDate.Value <= monthEnd)
            {
                lines.Add($"Completed in period: {m.Name} ({m.ActualDate:dd MMM yyyy})");
                continue;
            }

            if (m.Status is "complete" or "cancelled")
                continue;

            if (m.DueDate.Date < todayUtc)
                lines.Add($"Late: {m.Name} (due {m.DueDate:dd MMM yyyy})");
            else if (m.DueDate >= monthStart && m.DueDate < upcomingWindowEnd)
                lines.Add($"Due soon: {m.Name} ({m.DueDate:dd MMM yyyy})");
        }

        return lines.Count == 0 ? null : string.Join(Environment.NewLine, lines);
    }

    private async Task<MonthlyReportRaidSummary> BuildRaidSummaryAsync(
        int? businessAreaId,
        int? directorateId,
        CancellationToken cancellationToken)
    {
        var riskQuery = _db.Risks.AsNoTracking().Where(r => !r.IsDeleted);
        var issueQuery = _db.Issues.AsNoTracking().Where(i => !i.IsDeleted);

        if (businessAreaId is { } baid)
        {
            riskQuery = riskQuery.Where(r =>
                r.RiskBusinessAreas.Any(b => b.BusinessAreaLookupId == baid)
                || (r.Project != null && r.Project.BusinessAreaId == baid));
            issueQuery = issueQuery.Where(i =>
                i.IssueBusinessAreas.Any(b => b.BusinessAreaLookupId == baid)
                || (i.Project != null && i.Project.BusinessAreaId == baid));
        }

        if (directorateId is { } did)
        {
            riskQuery = riskQuery.Where(r => r.RiskDivisions.Any(d => d.DivisionId == did));
            issueQuery = issueQuery.Where(i => i.IssueDivisions.Any(d => d.DivisionId == did));
        }

        var riskOpen = riskQuery.Where(r => r.ClosedDate == null);
        var issueOpen = issueQuery.Where(i => i.ClosedDate == null);
        var today = DateTime.UtcNow.Date;

        var criticalSeverityIds = await _db.IssueSeverities.AsNoTracking()
            .Where(s => s.IsActive && s.Label == "Critical")
            .Select(s => s.Id)
            .ToListAsync(cancellationToken);

        var nearMissQuery = _db.NearMisses.AsNoTracking().Where(n => !n.IsDeleted);
        if (businessAreaId is { } nmBaId)
            nearMissQuery = nearMissQuery.Where(n => n.BusinessAreaLookupId == nmBaId);
        if (directorateId is { } nmDirId)
            nearMissQuery = nearMissQuery.Where(n => n.DirectorateLookupId == nmDirId);

        var closedNearMissStatusIds = await _db.NearMissStatuses.AsNoTracking()
            .Where(s => s.IsActive && s.Code == "CLOSED")
            .Select(s => s.Id)
            .ToListAsync(cancellationToken);
        if (closedNearMissStatusIds.Count > 0)
        {
            nearMissQuery = nearMissQuery.Where(n =>
                !n.NearMissStatusId.HasValue || !closedNearMissStatusIds.Contains(n.NearMissStatusId.Value));
        }

        return new MonthlyReportRaidSummary
        {
            OpenRisks = await riskOpen.CountAsync(cancellationToken),
            OpenIssues = await issueOpen.CountAsync(cancellationToken),
            OpenNearMisses = await nearMissQuery.CountAsync(cancellationToken),
            HighRisks = await riskOpen.CountAsync(r => r.RiskScore >= 15, cancellationToken),
            RisksReviewOverdue = await riskOpen.CountAsync(
                r => r.NextReviewDate.HasValue && r.NextReviewDate.Value < today,
                cancellationToken),
            OpenCriticalIssues = criticalSeverityIds.Count == 0
                ? 0
                : await issueOpen.CountAsync(i => i.SeverityId.HasValue && criticalSeverityIds.Contains(i.SeverityId.Value), cancellationToken)
        };
    }

    private static List<RagTrendMonthPoint> BuildRagTrend(
        List<Project> projects,
        Dictionary<int, List<ProjectRagHistory>> historyByProject,
        int reportYear,
        int reportMonth)
    {
        var list = new List<RagTrendMonthPoint>();
        var startMonth = new DateTime(2026, 1, 1);
        var reportMonthStart = new DateTime(reportYear, reportMonth, 1);
        for (var period = startMonth; period <= reportMonthStart; period = period.AddMonths(1))
        {
            var y = period.Year;
            var m = period.Month;
            var cutoff = new DateTime(y, m, 1).AddMonths(1);
            var label = new DateTime(y, m, 1).ToString("MMM yyyy");

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
                Label = label,
                Year = y,
                Month = m,
                Red = dist["Red"],
                AmberRed = dist["Amber-Red"],
                AmberGreen = dist["Amber-Green"],
                Green = dist["Green"],
                NotSet = dist["Not Set"]
            });
        }

        for (var i = 1; i <= 3; i++)
        {
            var future = reportMonthStart.AddMonths(i);
            list.Add(new RagTrendMonthPoint
            {
                Label = future.ToString("MMM yyyy"),
                Year = future.Year,
                Month = future.Month,
                Red = 0,
                AmberRed = 0,
                AmberGreen = 0,
                Green = 0,
                NotSet = 0
            });
        }

        return list;
    }

    private static string ResolveRagAtCutoff(
        Project project,
        DateTime cutoff,
        Dictionary<int, List<ProjectRagHistory>> historyByProject)
    {
        if (!historyByProject.TryGetValue(project.Id, out var list))
            return NormalizeRagStatus(project.RagStatusLookup?.Name ?? project.RagStatus);

        var last = list.FirstOrDefault(h => h.ChangedAt < cutoff);
        if (last != null)
            return NormalizeRagStatus(last.RagStatusLookup?.Name ?? last.RagStatus);

        return NormalizeRagStatus(project.RagStatusLookup?.Name ?? project.RagStatus);
    }

    private async Task<Dictionary<string, int>> BuildPrevMonthRagDistributionAsync(
        List<Project> prevMonthProjects,
        DateTime currentMonthStart,
        CancellationToken cancellationToken)
    {
        var prevMonthProjectIds = prevMonthProjects.Select(p => p.Id).ToList();
        var ragHistoryUpToPrevMonth = await _db.ProjectRagHistories
            .AsNoTracking()
            .Include(rh => rh.RagStatusLookup)
            .Where(rh => rh.ChangedAt < currentMonthStart && prevMonthProjectIds.Contains(rh.ProjectId))
            .OrderByDescending(rh => rh.ChangedAt)
            .ToListAsync(cancellationToken);

        var projectRagStatusAtPrevMonthEnd = ragHistoryUpToPrevMonth
            .GroupBy(rh => rh.ProjectId)
            .ToDictionary(g => g.Key, g => NormalizeRagStatus(g.First().RagStatusLookup?.Name ?? g.First().RagStatus));

        var prevMonthRagDistribution = new Dictionary<string, int>
        {
            { "Red", 0 },
            { "Amber-Red", 0 },
            { "Amber-Green", 0 },
            { "Green", 0 },
            { "Not Set", 0 }
        };

        foreach (var project in prevMonthProjects)
        {
            string ragStatus;
            if (projectRagStatusAtPrevMonthEnd.TryGetValue(project.Id, out var fromHist))
                ragStatus = fromHist;
            else
                ragStatus = NormalizeRagStatus(project.RagStatusLookup?.Name ?? project.RagStatus);

            if (string.IsNullOrWhiteSpace(ragStatus))
                prevMonthRagDistribution["Not Set"]++;
            else if (string.Equals(ragStatus, "Amber", StringComparison.OrdinalIgnoreCase))
                prevMonthRagDistribution["Not Set"]++;
            else if (prevMonthRagDistribution.ContainsKey(ragStatus))
                prevMonthRagDistribution[ragStatus]++;
            else
                prevMonthRagDistribution["Not Set"]++;
        }

        return prevMonthRagDistribution;
    }

    private static Dictionary<string, int> BuildRagDistribution(List<Project> projects)
    {
        var ragDistribution = new Dictionary<string, int>
        {
            { "Red", 0 },
            { "Amber-Red", 0 },
            { "Amber-Green", 0 },
            { "Green", 0 },
            { "Not Set", 0 }
        };

        foreach (var project in projects)
        {
            var ragStatus = NormalizeRagStatus(project.RagStatusLookup?.Name ?? project.RagStatus);
            if (string.IsNullOrWhiteSpace(ragStatus))
                ragDistribution["Not Set"]++;
            else if (string.Equals(ragStatus, "Amber", StringComparison.OrdinalIgnoreCase))
                ragDistribution["Not Set"]++;
            else if (ragDistribution.ContainsKey(ragStatus))
                ragDistribution[ragStatus]++;
            else
                ragDistribution["Not Set"]++;
        }

        return ragDistribution;
    }

    private static Dictionary<string, int> BuildPriorityDistribution(List<Project> projects)
    {
        var priorityDistribution = new Dictionary<string, int>
        {
            { "Critical", 0 },
            { "High", 0 },
            { "Medium", 0 },
            { "Low", 0 },
            { "Not Set", 0 }
        };

        foreach (var project in projects)
        {
            var bucket = PriorityBucket(project);
            priorityDistribution[bucket]++;
        }

        return priorityDistribution;
    }

    private static string PriorityBucket(Project project)
    {
        if (project.DeliveryPriority == null)
            return "Not Set";
        var priorityName = project.DeliveryPriority.Name.ToLowerInvariant();
        if (priorityName.Contains("critical"))
            return "Critical";
        if (priorityName.Contains("high"))
            return "High";
        if (priorityName.Contains("medium"))
            return "Medium";
        if (priorityName.Contains("low"))
            return "Low";
        return "Not Set";
    }

    private static string RagBucket(Project project)
    {
        var ragStatus = NormalizeRagStatus(project.RagStatusLookup?.Name ?? project.RagStatus);
        if (string.IsNullOrWhiteSpace(ragStatus))
            return "Not Set";
        if (string.Equals(ragStatus, "Amber", StringComparison.OrdinalIgnoreCase))
            return "Not Set";
        return ragStatus is "Red" or "Amber-Red" or "Amber-Green" or "Green" ? ragStatus : "Not Set";
    }

    private static string NormalizeRagStatus(string? ragStatus)
    {
        if (string.IsNullOrWhiteSpace(ragStatus))
            return string.Empty;

        var normalized = ragStatus.Trim()
            .Replace(" / ", "-")
            .Replace("/", "-")
            .Replace(" /", "-")
            .Replace("/ ", "-");

        var parts = normalized.Split('-');
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length > 0)
            {
                parts[i] = char.ToUpperInvariant(parts[i][0]) +
                           (parts[i].Length > 1 ? parts[i][1..].ToLowerInvariant() : "");
            }
        }

        return string.Join("-", parts);
    }

    private static List<PriorityTrendMonthPoint> BuildPriorityTrend(
        List<Project> projects,
        int reportYear,
        int reportMonth)
    {
        var list = new List<PriorityTrendMonthPoint>();
        var startMonth = new DateTime(2026, 1, 1);
        var reportMonthStart = new DateTime(reportYear, reportMonth, 1);

        for (var period = startMonth; period <= reportMonthStart; period = period.AddMonths(1))
        {
            var label = period.ToString("MMM yyyy");
            var dist = new Dictionary<string, int>
            {
                ["Critical"] = 0,
                ["High"] = 0,
                ["Medium"] = 0,
                ["Low"] = 0,
                ["Not Set"] = 0
            };
            foreach (var p in projects)
            {
                var bucket = PriorityBucket(p);
                dist[bucket]++;
            }
            list.Add(new PriorityTrendMonthPoint
            {
                Label = label,
                Year = period.Year,
                Month = period.Month,
                Critical = dist["Critical"],
                High = dist["High"],
                Medium = dist["Medium"],
                Low = dist["Low"],
                NotSet = dist["Not Set"]
            });
        }

        for (var i = 1; i <= 3; i++)
        {
            var future = reportMonthStart.AddMonths(i);
            list.Add(new PriorityTrendMonthPoint
            {
                Label = future.ToString("MMM yyyy"),
                Year = future.Year,
                Month = future.Month,
                Critical = 0,
                High = 0,
                Medium = 0,
                Low = 0,
                NotSet = 0
            });
        }

        return list;
    }

    private static List<ProjectChangeRow> BuildRagChangeDetails(
        List<Project> projects,
        List<ProjectRagHistory> ragHistoryDuringMonth,
        Dictionary<int, List<ProjectRagHistory>> historyByProject,
        DateTime monthStart,
        int reportYear, int reportMonth)
    {
        var projectMap = projects.ToDictionary(p => p.Id);
        var changedProjectIds = ragHistoryDuringMonth
            .Where(rh => projectMap.ContainsKey(rh.ProjectId))
            .Select(rh => rh.ProjectId)
            .Distinct();

        var rows = new List<ProjectChangeRow>();
        foreach (var pid in changedProjectIds)
        {
            if (!projectMap.TryGetValue(pid, out var project)) continue;

            var fromRag = ResolveRagAtCutoff(project, monthStart, historyByProject);
            if (string.IsNullOrWhiteSpace(fromRag)) fromRag = "Not Set";
            else if (string.Equals(fromRag, "Amber", StringComparison.OrdinalIgnoreCase)) fromRag = "Not Set";

            var toRag = NormalizeRagStatus(project.RagStatusLookup?.Name ?? project.RagStatus);
            if (string.IsNullOrWhiteSpace(toRag)) toRag = "Not Set";
            else if (string.Equals(toRag, "Amber", StringComparison.OrdinalIgnoreCase)) toRag = "Not Set";

            if (fromRag == toRag) continue;

            var lastChange = ragHistoryDuringMonth
                .Where(rh => rh.ProjectId == pid)
                .OrderByDescending(rh => rh.ChangedAt)
                .First();

            var latestUpdate = project.MonthlyUpdates?
                .Where(u => u.Year == reportYear && u.Month == reportMonth)
                .OrderByDescending(u => u.SubmittedAt ?? u.CreatedAt)
                .FirstOrDefault()
                ?? project.MonthlyUpdates?
                    .OrderByDescending(u => u.Year * 100 + u.Month)
                    .FirstOrDefault();

            rows.Add(new ProjectChangeRow
            {
                ProjectId = project.Id,
                Title = project.Title,
                BusinessArea = project.BusinessAreaLookup?.Name,
                From = fromRag,
                To = toRag,
                ChangedAt = lastChange.ChangedAt,
                Justification = !string.IsNullOrWhiteSpace(lastChange.Justification)
                    ? lastChange.Justification
                    : project.RagJustification,
                RagJustification = project.RagJustification,
                LatestNarrative = latestUpdate != null ? ProjectMonthlyUpdateNarrative.Compose(latestUpdate) : null
            });
        }
        return rows.OrderBy(r => RagSortOrder(r.To)).ThenBy(r => r.Title).ToList();
    }

    private static List<ProjectChangeRow> BuildPriorityChangeDetails(
        List<Project> currentProjects,
        List<Project> prevMonthProjects,
        DateTime monthStart, DateTime monthEnd,
        int reportYear, int reportMonth)
    {
        var prevMap = prevMonthProjects.ToDictionary(p => p.Id);
        var rows = new List<ProjectChangeRow>();
        foreach (var p in currentProjects)
        {
            if (p.UpdatedAt < monthStart || p.UpdatedAt > monthEnd) continue;
            if (!prevMap.TryGetValue(p.Id, out var prev)) continue;

            var fromPri = PriorityBucket(prev);
            var toPri = PriorityBucket(p);
            if (fromPri == toPri) continue;

            var latestUpdate = p.MonthlyUpdates?
                .Where(u => u.Year == reportYear && u.Month == reportMonth)
                .OrderByDescending(u => u.SubmittedAt ?? u.CreatedAt)
                .FirstOrDefault()
                ?? p.MonthlyUpdates?
                    .OrderByDescending(u => u.Year * 100 + u.Month)
                    .FirstOrDefault();

            rows.Add(new ProjectChangeRow
            {
                ProjectId = p.Id,
                Title = p.Title,
                BusinessArea = p.BusinessAreaLookup?.Name,
                From = fromPri,
                To = toPri,
                ChangedAt = p.UpdatedAt,
                Justification = p.DeliveryPriorityChangeReason,
                RagJustification = p.RagJustification,
                LatestNarrative = latestUpdate != null ? ProjectMonthlyUpdateNarrative.Compose(latestUpdate) : null
            });
        }
        return rows.OrderBy(r => r.Title).ToList();
    }

    private static int RagSortOrder(string rag) => rag switch
    {
        "Red" => 1,
        "Amber-Red" => 2,
        "Amber-Green" => 3,
        "Green" => 4,
        _ => 5
    };

    private static MonthlyUpdateStats CalculateMonthlyUpdateStats(
        List<Project> projects,
        int year,
        int month,
        IMonthlyUpdateService monthlyUpdateService) =>
        CalculateMonthlyUpdateStats(projects, year, month, monthlyUpdateService.GetMonthlyUpdateDueDate(year, month));

    private static MonthlyUpdateStats CalculateMonthlyUpdateStats(
        List<Project> projects,
        int year,
        int month,
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
            var update = project.MonthlyUpdates?.FirstOrDefault(u => u.Year == year && u.Month == month);

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
            Year = year,
            Month = month,
            TotalProjects = totalProjects,
            Submitted = submitted,
            NotStarted = notStarted,
            InProgress = inProgress,
            Late = late,
            DueDate = dueDate
        };
    }

    /// <summary>
    /// Plain-language narrative for the single–business-area monthly report. Tone is suggestive, not prescriptive; data-driven prompts only.
    /// </summary>
    private static string BuildBusinessAreaSummaryNarrative(
        string businessAreaName,
        string monthDisplay,
        DateTime monthStart,
        int reportYear,
        int reportMonth,
        IReadOnlyList<Project> projects,
        int newThisMonthCount,
        int milestonesAchievedCount,
        IReadOnlyList<MilestoneWithProject> upcomingMilestones,
        IReadOnlyList<MilestoneWithProject> lateMilestones,
        MonthlyUpdateStats? submissionStats,
        Dictionary<string, int> ragDistribution,
        Dictionary<string, int> priorityDistribution,
        Dictionary<string, int> prevMonthRagDistribution,
        string prevMonthName,
        IReadOnlyList<RagTrendMonthPoint> ragTrend,
        int pathToGreenCount,
        int ragChangeProjects,
        int priorityChangeProjects,
        List<ProjectChangeRow> ragChanges,
        List<ProjectChangeRow> priorityChanges)
    {
        static int RagRiskOrder(string rag) => rag switch
        {
            "Green" => 5,
            "Amber-Green" => 4,
            "Amber-Red" => 2,
            "Red" => 1,
            _ => 3
        };

        static bool RagWorsened(string from, string to) =>
            RagRiskOrder(to) < RagRiskOrder(from);

        static bool RagImproved(string from, string to) =>
            RagRiskOrder(to) > RagRiskOrder(from);

        var totalActive = projects.Count;
        var upcomingMilestonesCount = upcomingMilestones.Count;
        var lateMilestonesCount = lateMilestones.Count;
        var paragraphs = new List<string>();

        if (totalActive == 0)
        {
            paragraphs.Add(
                $"There are no active work items attributed to {businessAreaName} in Compass for {monthDisplay} with the current filters.");
            return string.Join("\n\n", paragraphs);
        }

        paragraphs.Add(
            $"For {monthDisplay}, {businessAreaName} has {totalActive} active work item{(totalActive == 1 ? "" : "s")}. " +
            $"{(newThisMonthCount > 0 ? $"{newThisMonthCount} new item{(newThisMonthCount == 1 ? "" : "s")} started in this month. " : "")}" +
            "The figures on this page summarise returns, RAG, priority, and milestones for this view.");

        if (submissionStats != null && submissionStats.TotalProjects > 0)
        {
            var due = submissionStats.DueDate.ToString("d MMMM yyyy");
            var pct = Math.Round(100m * submissionStats.Submitted / submissionStats.TotalProjects, 1, MidpointRounding.AwayFromZero);
            var submissionSentence =
                $"Monthly returns for {monthDisplay} were due {due}. " +
                $"Of {submissionStats.TotalProjects} work item{(submissionStats.TotalProjects == 1 ? "" : "s")} in scope, " +
                $"{submissionStats.Submitted} ({pct}%) {(submissionStats.Submitted == 1 ? "was" : "were")} submitted.";
            if (submissionStats.InProgress > 0)
                submissionSentence += $" {submissionStats.InProgress} {(submissionStats.InProgress == 1 ? "has" : "have")} a return in progress.";
            if (submissionStats.NotStarted > 0)
                submissionSentence +=
                    $" {submissionStats.NotStarted} {(submissionStats.NotStarted == 1 ? "has" : "have")} not yet started a return for this period (within the window reflected here).";
            if (submissionStats.Late > 0)
                submissionSentence +=
                    $" {submissionStats.Late} {(submissionStats.Late == 1 ? "was" : "were")} still unreturned after the due date—if that still applies, a light touch with the relevant delivery lead may help clear the line.";
            else if (submissionStats.Submitted == submissionStats.TotalProjects)
                submissionSentence += " Returns are in for all items in scope for this period.";
            paragraphs.Add(submissionSentence);
        }
        else if (submissionStats != null && submissionStats.TotalProjects == 0)
            paragraphs.Add("No work items in this view were in scope for monthly returns for the selected period.");

        var stableRag = TryDescribeRagMixStability(ragTrend, reportYear, reportMonth);
        if (!string.IsNullOrEmpty(stableRag))
            paragraphs.Add(stableRag);

        var milestoneParts = new List<string>();
        if (milestonesAchievedCount > 0)
            milestoneParts.Add(
                $"{milestonesAchievedCount} milestone{(milestonesAchievedCount == 1 ? "" : "s")} were recorded complete in {monthDisplay}");
        if (upcomingMilestonesCount > 0)
        {
            var uPlural = upcomingMilestonesCount == 1 ? "" : "s";
            var uVerb = upcomingMilestonesCount == 1 ? "is" : "are";
            milestoneParts.Add(
                $"{upcomingMilestonesCount} open milestone{uPlural} {uVerb} due in the 30 days from the start of the month. {DescribeUpcomingMilestoneHint(upcomingMilestones, monthStart)}");
        }
        if (lateMilestonesCount > 0)
        {
            var lPlural = lateMilestonesCount == 1 ? "" : "s";
            var lVerb = lateMilestonesCount == 1 ? "is" : "are";
            milestoneParts.Add(
                $"{lateMilestonesCount} open milestone{lPlural} {lVerb} past due. {DescribeLateMilestonesHint(lateMilestones, monthStart)}");
        }
        if (milestoneParts.Count > 0)
            paragraphs.Add("Milestones: " + string.Join("; ", milestoneParts) + ".");

        var staleMilestoneCount = CountStaleOpenMilestones(projects, monthStart, staleBefore: monthStart.AddDays(-56));
        if (staleMilestoneCount > 0)
        {
            paragraphs.Add(
                $"{staleMilestoneCount} open milestone{(staleMilestoneCount == 1 ? "" : "s")} {(staleMilestoneCount == 1 ? "has" : "have")} not been updated in Compass for at least eight weeks before the start of {monthDisplay}. A quick refresh of dates or status in the plan may be worthwhile.");
        }

        var ragRed = ragDistribution.GetValueOrDefault("Red");
        var ragAmbr = ragDistribution.GetValueOrDefault("Amber-Red");
        var ragAmg = ragDistribution.GetValueOrDefault("Amber-Green");
        var ragGreen = ragDistribution.GetValueOrDefault("Green");
        var prevRed = prevMonthRagDistribution.GetValueOrDefault("Red");
        if (ragRed + ragAmbr + ragAmg + ragGreen + ragDistribution.GetValueOrDefault("Not Set") > 0)
        {
            var mixParts = new List<string>();
            if (ragRed + ragAmbr > 0)
                mixParts.Add(
                    $"{ragRed + ragAmbr} at Red or Amber–Red ({ragRed} Red, {ragAmbr} Amber–Red)");
            mixParts.Add($"{ragAmg} Amber–Green, {ragGreen} Green");
            var ragSentence = "RAG: " + string.Join("; ", mixParts) + ".";
            if (ragRed != prevRed)
            {
                ragSentence +=
                    $" Compared with {prevMonthName}, the count in Red moved from {prevRed} to {ragRed}.";
            }
            paragraphs.Add(ragSentence);

            var lowPriElevatedRag = CountElevatedRagWithLowerPriority(projects);
            if (lowPriElevatedRag > 0)
            {
                paragraphs.Add(
                    $"{lowPriElevatedRag} work item{(lowPriElevatedRag == 1 ? "" : "s")} {(lowPriElevatedRag == 1 ? "shows" : "show")} Red or Amber–Red while delivery priority is Medium, Low, not set, or marked optional. That pairing can be fine, but you may want to confirm that priority still reflects the assurance signal—or adjust one or the other for clarity.");
            }
        }

        var crit = priorityDistribution.GetValueOrDefault("Critical");
        var high = priorityDistribution.GetValueOrDefault("High");
        if (crit + high > 0)
            paragraphs.Add(
                $"Delivery priority: {crit} Critical, {high} High (among others in the table). These counts are a prompt for where attention may already be formalised.");

        var worsening = ragChanges.Count(r => RagWorsened(r.From, r.To));
        var improving = ragChanges.Count(r => RagImproved(r.From, r.To));
        var priChanged = priorityChanges.Count;
        var movementParts = new List<string>();
        if (ragChangeProjects > 0)
            movementParts.Add(
                $"{ragChangeProjects} work item{(ragChangeProjects == 1 ? "" : "s")} had a RAG change recorded in the month" +
                (worsening > 0 || improving > 0
                    ? $" ({worsening} where the signal moved toward red, {improving} toward green, where the history allows a clean read)"
                    : ""));
        if (priChanged > 0)
            movementParts.Add($"{priChanged} delivery priority change{(priChanged == 1 ? "" : "s")} in the period");
        if (pathToGreenCount > 0)
            movementParts.Add(
                $"{pathToGreenCount} non-green work item{(pathToGreenCount == 1 ? "" : "s")} {(pathToGreenCount == 1 ? "has" : "have")} path-to-green text—useful as a thread for follow-up, not a test of pass or fail");
        if (movementParts.Count > 0)
            paragraphs.Add("Movement: " + string.Join("; ", movementParts) + ".");

        return string.Join("\n\n", paragraphs);
    }

    /// <summary>
    /// Red or Amber–Red with medium/low/unset/optional-style priority (not Critical/High).
    /// </summary>
    private static int CountElevatedRagWithLowerPriority(IEnumerable<Project> projects) =>
        projects.Count(p =>
        {
            var r = RagBucket(p);
            if (r is not ("Red" or "Amber-Red"))
                return false;
            return IsLowerOrUnsetDeliveryPriority(p);
        });

    private static bool IsLowerOrUnsetDeliveryPriority(Project project)
    {
        if (IsOptionalPriorityName(project))
            return true;
        return PriorityBucket(project) is "Low" or "Medium" or "Not Set";
    }

    private static bool IsOptionalPriorityName(Project project)
    {
        var name = project.DeliveryPriority?.Name;
        if (string.IsNullOrWhiteSpace(name))
            return false;
        return name.Contains("optional", StringComparison.OrdinalIgnoreCase);
    }

    private static int CountStaleOpenMilestones(
        IReadOnlyList<Project> projects,
        DateTime monthStart,
        DateTime staleBefore)
    {
        var n = 0;
        foreach (var p in projects)
        {
            if (p.Milestones == null) continue;
            foreach (var m in p.Milestones)
            {
                if (m.IsDeleted) continue;
                if (m.Status is "complete" or "cancelled")
                    continue;
                if (m.UpdatedAt >= staleBefore)
                    continue;
                n++;
            }
        }
        return n;
    }

    private static string? TryDescribeRagMixStability(
        IReadOnlyList<RagTrendMonthPoint> trend,
        int reportYear,
        int reportMonth)
    {
        var historical = trend
            .Where(p => p.Year < reportYear || (p.Year == reportYear && p.Month <= reportMonth))
            .OrderBy(p => p.Year).ThenBy(p => p.Month)
            .ToList();
        if (historical.Count < 2)
            return null;

        const int wantMonths = 3;
        var take = Math.Min(wantMonths, historical.Count);
        var slice = historical.TakeLast(take).ToList();
        if (slice.Count < 2)
            return null;

        var first = slice[0];
        bool flat = slice.All(p =>
            p.Red == first.Red && p.AmberRed == first.AmberRed && p.AmberGreen == first.AmberGreen
            && p.Green == first.Green && p.NotSet == first.NotSet);
        if (!flat)
            return null;

        var n = slice.Count;
        return
            $"The RAG mix in this business area has been the same for the last {n} month{(n == 1 ? "" : "s")} in Compass (from {first.Label} to {slice[^1].Label}). " +
            "If that still matches how teams feel on the ground, no change is implied—it's simply a prompt to sense-check if needed.";
    }

    private static string DescribeUpcomingMilestoneHint(
        IReadOnlyList<MilestoneWithProject> upcoming,
        DateTime monthStart)
    {
        if (upcoming.Count == 0)
            return "You may want to line these up with team plans when convenient.";
        var soonest = upcoming.Min(x => x.Milestone.DueDate);
        if (soonest <= monthStart.AddDays(7))
            return "At least one is very soon, so a short check-in with owners may help.";
        return "Worth a quick glance at upcoming commitments when you next speak with teams.";
    }

    private static string DescribeLateMilestonesHint(
        IReadOnlyList<MilestoneWithProject> late,
        DateTime monthStart)
    {
        if (late.Count == 0)
            return "Confirming next steps with owners can help, if still relevant.";
        var oldest = late.Min(x => x.Milestone.DueDate);
        if (oldest < monthStart.AddMonths(-2))
            return "Some have been open a long time; a light refresh of dates or ownership in the plan may be useful.";
        return "A quick look at whether dates or owners still feel right may be enough.";
    }

    public async Task<ModernMonthlySubmissionProgressViewModel> BuildSubmissionProgressAsync(
        int? year,
        int? month,
        int? businessAreaId,
        int? directorateId,
        CancellationToken cancellationToken = default)
    {
        var currentDate = DateTime.UtcNow;
        var calendarYearUtc = currentDate.Year;
        var currentMonth = currentDate.Month;

        const int minReportYear = 2026;
        var maxSelectableYear = calendarYearUtc >= minReportYear ? calendarYearUtc : minReportYear;

        var currentPeriodDueDate = _monthlyUpdateService.GetMonthlyUpdateDueDate(calendarYearUtc, currentMonth);
        var daysUntilCurrentPeriodDueDate = (currentPeriodDueDate - currentDate).Days;

        var defaultReportYear = daysUntilCurrentPeriodDueDate <= 10 ? calendarYearUtc : (currentMonth == 1 ? calendarYearUtc - 1 : calendarYearUtc);
        var defaultReportMonth = daysUntilCurrentPeriodDueDate <= 10 ? currentMonth : (currentMonth == 1 ? 12 : currentMonth - 1);
        defaultReportYear = Math.Max(minReportYear, defaultReportYear);

        var reportYear = year ?? defaultReportYear;
        var reportMonth = month ?? defaultReportMonth;
        if (reportMonth < 1 || reportMonth > 12)
            reportMonth = defaultReportMonth;
        reportYear = Math.Clamp(reportYear, minReportYear, maxSelectableYear);

        var monthStart = new DateTime(reportYear, reportMonth, 1);
        var monthEnd = monthStart.AddMonths(1).AddDays(-1);

        var explicitPeriod = _monthlyUpdateService.TryGetActiveExplicitReportingPeriod(reportYear, reportMonth);
        var dueDate = _monthlyUpdateService.GetMonthlyUpdateDueDate(reportYear, reportMonth).Date;
        var submissionWindowStart = _monthlyUpdateService.GetSubmissionWindowOpens(reportYear, reportMonth);
        var submissionWindowEnd = _monthlyUpdateService.GetSubmissionWindowCloses(reportYear, reportMonth);
        if (submissionWindowEnd < submissionWindowStart)
            submissionWindowEnd = submissionWindowStart;

        var submissionWindowDescription = explicitPeriod != null
            ? _monthlyUpdateService.GetMonthlyUpdateDueRuleDescription(reportYear, reportMonth)
            : $"Submission due {dueDate:d MMMM yyyy}";

        var query = _db.Projects
            .AsNoTracking()
            .Include(p => p.BusinessAreaLookup)
            .Include(p => p.MonthlyUpdates)
            .Include(p => p.Directorates)
                .ThenInclude(d => d.Division)
            .Where(p => !p.IsDeleted && p.Status != "Cancelled" && p.Status != "Completed");

        if (businessAreaId.HasValue)
            query = query.Where(p => p.BusinessAreaId == businessAreaId.Value);
        if (directorateId.HasValue)
            query = query.Where(p => p.Directorates.Any(d => d.DivisionId == directorateId.Value));

        var allProjects = await query.ToListAsync(cancellationToken);

        var businessAreas = await _db.BusinessAreaLookups
            .AsNoTracking()
            .Where(ba => ba.IsActive)
            .OrderBy(ba => ba.SortOrder)
            .ThenBy(ba => ba.Name)
            .ToListAsync(cancellationToken);

        var directorates = await _db.Divisions
            .AsNoTracking()
            .Where(d => d.IsActive)
            .OrderBy(d => d.SortOrder)
            .ThenBy(d => d.Name)
            .ToListAsync(cancellationToken);

        var monthlyUpdateStats = CalculateMonthlyUpdateStats(allProjects, reportYear, reportMonth, dueDate);
        var expectedProgressToday = ComputeExpectedProgressPercent(submissionWindowStart, submissionWindowEnd, DateTime.UtcNow.Date);

        var submittedDates = allProjects
            .Select(p => p.MonthlyUpdates?.FirstOrDefault(u => u.Year == reportYear && u.Month == reportMonth))
            .Where(u => u?.SubmittedAt != null)
            .Select(u => u!.SubmittedAt!.Value.Date)
            .OrderBy(d => d)
            .ToList();

        var dailyProgress = BuildDailySubmissionProgress(
            allProjects.Count,
            submissionWindowStart,
            submissionWindowEnd,
            submittedDates);

        var businessAreaLeague = BuildSubmissionLeagueByBusinessArea(
            allProjects, reportYear, reportMonth, expectedProgressToday, dueDate);
        var directorateLeague = BuildSubmissionLeagueByDirectorate(
            allProjects, directorates, reportYear, reportMonth, expectedProgressToday, dueDate);

        var (trendColumns, businessAreaTrendRows) = BuildBusinessAreaSixMonthTrends(
            allProjects, reportYear, reportMonth);

        var exportPeriodColumns = await LoadExportPeriodColumnsAsync(
            reportYear, reportMonth, minReportYear, cancellationToken);
        var exportRows = BuildSubmissionProgressExportRows(
            allProjects, directorates, reportYear, reportMonth, dueDate, exportPeriodColumns);

        var nextMonthDate = monthStart.AddMonths(1);
        var nextMonthAllowed =
            (nextMonthDate.Year < defaultReportYear ||
             (nextMonthDate.Year == defaultReportYear && nextMonthDate.Month <= defaultReportMonth)) &&
            nextMonthDate.Year <= calendarYearUtc;
        var prevMonthDate = monthStart.AddMonths(-1);
        var earliestReportPeriod = new DateTime(minReportYear, 1, 1);
        var hasPreviousMonthNav = prevMonthDate >= earliestReportPeriod;

        return new ModernMonthlySubmissionProgressViewModel
        {
            ReportYear = reportYear,
            ReportMonth = reportMonth,
            MonthName = monthStart.ToString("MMMM yyyy"),
            MonthStart = monthStart,
            MonthEnd = monthEnd,
            DefaultReportYear = defaultReportYear,
            DefaultReportMonth = defaultReportMonth,
            MinReportYear = minReportYear,
            MaxReportYear = maxSelectableYear,
            FilterBusinessAreaId = businessAreaId,
            FilterDirectorateId = directorateId,
            BusinessAreas = businessAreas,
            Directorates = directorates,
            MonthlyUpdateStats = monthlyUpdateStats,
            SubmissionWindowStart = submissionWindowStart,
            SubmissionWindowEnd = submissionWindowEnd,
            UsesExplicitReportingPeriod = explicitPeriod != null,
            SubmissionWindowDescription = submissionWindowDescription,
            ExpectedProgressPercentToday = expectedProgressToday,
            DailyProgress = dailyProgress,
            BusinessAreaLeague = businessAreaLeague,
            DirectorateLeague = directorateLeague,
            TrendMonthColumns = trendColumns,
            BusinessAreaTrendRows = businessAreaTrendRows,
            ExportPeriodColumns = exportPeriodColumns,
            ExportRows = exportRows,
            HasPreviousMonthNav = hasPreviousMonthNav,
            HasNextMonthNav = nextMonthAllowed,
            PreviousNavYear = hasPreviousMonthNav ? prevMonthDate.Year : null,
            PreviousNavMonth = hasPreviousMonthNav ? prevMonthDate.Month : null,
            NextNavYear = nextMonthAllowed ? nextMonthDate.Year : null,
            NextNavMonth = nextMonthAllowed ? nextMonthDate.Month : null
        };
    }

    private static decimal ComputeExpectedProgressPercent(DateTime windowStart, DateTime windowEnd, DateTime asOfDate)
    {
        if (windowEnd < windowStart)
            return 100m;

        var totalDays = (windowEnd - windowStart).Days + 1;
        if (totalDays <= 0)
            return 100m;

        if (asOfDate < windowStart)
            return 0m;
        if (asOfDate >= windowEnd)
            return 100m;

        var elapsedDays = (asOfDate - windowStart).Days + 1;
        return Math.Round(100m * elapsedDays / totalDays, 1, MidpointRounding.AwayFromZero);
    }

    private static List<SubmissionProgressDayPoint> BuildDailySubmissionProgress(
        int totalInScope,
        DateTime windowStart,
        DateTime windowEnd,
        IReadOnlyList<DateTime> submissionDates)
    {
        var points = new List<SubmissionProgressDayPoint>();
        if (windowEnd < windowStart)
            return points;

        var totalDays = (windowEnd - windowStart).Days + 1;
        var submittedIndex = 0;
        var cumulative = 0;

        for (var day = windowStart; day <= windowEnd; day = day.AddDays(1))
        {
            while (submittedIndex < submissionDates.Count && submissionDates[submittedIndex] <= day)
            {
                cumulative++;
                submittedIndex++;
            }

            var dayNumber = (day - windowStart).Days + 1;
            var expected = totalInScope == 0 || totalDays == 0
                ? 0m
                : Math.Round((decimal)totalInScope * dayNumber / totalDays, 1, MidpointRounding.AwayFromZero);
            var expectedPercent = totalDays == 0
                ? 0m
                : Math.Round(100m * dayNumber / totalDays, 1, MidpointRounding.AwayFromZero);
            var actualPercent = totalInScope == 0
                ? 0m
                : Math.Round(100m * cumulative / totalInScope, 1, MidpointRounding.AwayFromZero);

            points.Add(new SubmissionProgressDayPoint
            {
                Label = day.ToString("d MMM"),
                Date = day,
                ActualCumulative = cumulative,
                ExpectedCumulative = expected,
                ActualCompletionPercent = actualPercent,
                ExpectedCompletionPercent = expectedPercent,
                TotalInScope = totalInScope
            });
        }

        return points;
    }

    private static (List<SubmissionTrendMonthColumn> Columns, List<BusinessAreaMonthlySubmissionTrendRow> Rows)
        BuildBusinessAreaSixMonthTrends(List<Project> projects, int endYear, int endMonth)
    {
        const int monthCount = 6;
        var endDate = new DateTime(endYear, endMonth, 1);
        var columns = new List<SubmissionTrendMonthColumn>();
        for (var i = monthCount - 1; i >= 0; i--)
        {
            var d = endDate.AddMonths(-i);
            columns.Add(new SubmissionTrendMonthColumn
            {
                Year = d.Year,
                Month = d.Month,
                Label = d.ToString("MMM yyyy", System.Globalization.CultureInfo.GetCultureInfo("en-GB"))
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
                    var (submitted, total) = CountSubmissionForMonth(g, col.Year, col.Month);
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

                var first = months[0];
                var last = months[^1];
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

    private static bool IsProjectInReportingScopeForMonth(Project project, int year, int month) =>
        WorkRegisterMonthlySubmissionExportHelper.IsProjectInReportingScopeForMonth(project, year, month);

    private static (int Submitted, int TotalInScope) CountSubmissionForMonth(
        IEnumerable<Project> projects,
        int year,
        int month)
    {
        var inScope = projects.Where(p => IsProjectInReportingScopeForMonth(p, year, month)).ToList();
        var total = inScope.Count;
        var submitted = inScope.Count(p =>
        {
            var update = p.MonthlyUpdates?.FirstOrDefault(u => u.Year == year && u.Month == month);
            return update?.SubmittedAt != null;
        });
        return (submitted, total);
    }

    private static SubmissionReportingTrend ClassifyMonthOverMonth(
        BusinessAreaMonthlySubmissionCell previous,
        BusinessAreaMonthlySubmissionCell current)
    {
        if (previous.TotalInScope == 0 || current.TotalInScope == 0)
            return SubmissionReportingTrend.InsufficientData;

        var completionDelta = current.CompletionPercent - previous.CompletionPercent;
        if (completionDelta >= 3)
            return SubmissionReportingTrend.Improving;
        if (completionDelta <= -3)
            return SubmissionReportingTrend.Worsening;
        return SubmissionReportingTrend.Stable;
    }

    private static SubmissionReportingTrend ClassifySubmissionTrend(IReadOnlyList<BusinessAreaMonthlySubmissionCell> months)
    {
        if (months.Count < 2)
            return SubmissionReportingTrend.InsufficientData;

        var first = months[0];
        var last = months[^1];
        var scopeDelta = last.TotalInScope - first.TotalInScope;
        var completionDelta = last.CompletionPercent - first.CompletionPercent;
        var rates = months.Where(m => m.TotalInScope > 0).Select(m => m.CompletionPercent).ToList();
        if (rates.Count < 2)
            return SubmissionReportingTrend.InsufficientData;

        var rateRange = rates.Max() - rates.Min();

        if (scopeDelta <= -3 || (completionDelta < -8 && scopeDelta <= 1))
            return SubmissionReportingTrend.Worsening;
        if (scopeDelta >= 3 || completionDelta >= 8 || (scopeDelta >= 1 && completionDelta >= 3))
            return SubmissionReportingTrend.Improving;
        if (rateRange <= 10 && Math.Abs(scopeDelta) <= 2)
            return SubmissionReportingTrend.Stable;
        if (completionDelta >= 4)
            return SubmissionReportingTrend.Improving;
        if (completionDelta <= -5)
            return SubmissionReportingTrend.Worsening;
        return SubmissionReportingTrend.Stable;
    }

    private static string DescribeSubmissionTrend(
        SubmissionReportingTrend trend,
        int scopeDelta,
        decimal completionDelta)
    {
        return trend switch
        {
            SubmissionReportingTrend.Improving when scopeDelta > 0 =>
                $"Reporting on more work items (+{scopeDelta}) with {(completionDelta >= 0 ? "improving" : "mixed")} completion.",
            SubmissionReportingTrend.Improving =>
                $"Completion rate improving ({FormatSignedPercent(completionDelta)} percentage points).",
            SubmissionReportingTrend.Worsening when scopeDelta < 0 =>
                $"Fewer work items in scope ({scopeDelta}).",
            SubmissionReportingTrend.Worsening =>
                $"Completion rate declining ({FormatSignedPercent(completionDelta)} percentage points).",
            SubmissionReportingTrend.Stable =>
                "Similar reporting scope and completion over the period.",
            _ => "Not enough history to assess trend."
        };
    }

    private static string FormatSignedPercent(decimal delta) =>
        delta > 0 ? $"+{delta:0.#}" : $"{delta:0.#}";

    private static List<MonthlySubmissionLeagueRow> BuildSubmissionLeagueByBusinessArea(
        List<Project> projects,
        int reportYear,
        int reportMonth,
        decimal expectedProgressPercent,
        DateTime dueDate)
    {
        return projects
            .GroupBy(p => p.BusinessAreaId)
            .Select(g => BuildSubmissionLeagueRow(
                g.First().BusinessAreaLookup?.Name ?? "Not set",
                g.Key,
                g.ToList(),
                reportYear,
                reportMonth,
                expectedProgressPercent,
                dueDate))
            .OrderBy(r => r.Name == "Not set" ? "zzzzzz" : r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<MonthlySubmissionLeagueRow> BuildSubmissionLeagueByDirectorate(
        List<Project> projects,
        List<Division> directorateLookups,
        int reportYear,
        int reportMonth,
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
                return BuildSubmissionLeagueRow(name, g.Key, g.ToList(), reportYear, reportMonth, expectedProgressPercent, dueDate);
            })
            .OrderBy(r => r.Name == "Not set" ? "zzzzzz" : r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int? GetPrimaryDirectorateId(Project project)
    {
        var primary = project.Directorates?
            .OrderBy(d => d.Division?.SortOrder ?? int.MaxValue)
            .ThenBy(d => d.Division?.Name ?? "")
            .FirstOrDefault();
        return primary?.DivisionId;
    }

    private static MonthlySubmissionLeagueRow BuildSubmissionLeagueRow(
        string name,
        int? entityId,
        List<Project> projects,
        int reportYear,
        int reportMonth,
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
            var update = project.MonthlyUpdates?.FirstOrDefault(u => u.Year == reportYear && u.Month == reportMonth);
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
            WorkItems = BuildSubmissionProgressWorkItems(projects, reportYear, reportMonth, dueDate)
        };
    }

    private static List<SubmissionProgressWorkItemRow> BuildSubmissionProgressWorkItems(
        List<Project> projects,
        int reportYear,
        int reportMonth,
        DateTime dueDate)
    {
        return projects
            .OrderBy(p => p.Title, StringComparer.OrdinalIgnoreCase)
            .Select(p => BuildSubmissionProgressWorkItemRow(p, reportYear, reportMonth, dueDate))
            .ToList();
    }

    private static SubmissionProgressWorkItemRow BuildSubmissionProgressWorkItemRow(
        Project p,
        int reportYear,
        int reportMonth,
        DateTime dueDate)
    {
        var (status, submittedAt) = ResolveDetailedSubmissionStatus(p, reportYear, reportMonth, dueDate);
        return new SubmissionProgressWorkItemRow
        {
            ProjectId = p.Id,
            Title = p.Title,
            SubmissionStatus = status,
            SubmittedAt = submittedAt
        };
    }

    private static (string Status, DateTime? SubmittedAt) ResolveDetailedSubmissionStatus(
        Project project,
        int reportYear,
        int reportMonth,
        DateTime dueDate)
    {
        var nowUtc = DateTime.UtcNow;
        var update = project.MonthlyUpdates?.FirstOrDefault(u => u.Year == reportYear && u.Month == reportMonth);

        if (update != null && update.SubmittedAt.HasValue)
            return ("Submitted", update.SubmittedAt);

        if (nowUtc > dueDate)
            return ("Late", null);

        if (update != null && !update.SubmittedAt.HasValue)
            return ("In progress", null);

        return ("Not started", null);
    }

    private static string ResolveExportPeriodSubmissionStatus(
        Project project,
        int reportYear,
        int reportMonth) =>
        WorkRegisterMonthlySubmissionExportHelper.ResolvePeriodSubmissionStatus(project, reportYear, reportMonth);

    private Task<List<SubmissionTrendMonthColumn>> LoadExportPeriodColumnsAsync(
        int reportYear,
        int reportMonth,
        int minReportYear,
        CancellationToken cancellationToken) =>
        WorkRegisterMonthlySubmissionExportHelper.LoadPeriodColumnsAsync(
            _db, reportYear, reportMonth, minReportYear, cancellationToken);

    private static List<SubmissionProgressExportRow> BuildSubmissionProgressExportRows(
        List<Project> projects,
        List<Division> directorateLookups,
        int reportYear,
        int reportMonth,
        DateTime dueDate,
        IReadOnlyList<SubmissionTrendMonthColumn> periodColumns)
    {
        var dirNameById = directorateLookups.ToDictionary(d => d.Id, d => d.Name);

        return projects
            .OrderBy(p => p.Title, StringComparer.OrdinalIgnoreCase)
            .Select(p =>
            {
                var dirId = GetPrimaryDirectorateId(p);
                var dirName = dirId.HasValue && dirNameById.TryGetValue(dirId.Value, out var n) ? n : "Not set";
                var (currentStatus, submittedAt) = ResolveDetailedSubmissionStatus(
                    p, reportYear, reportMonth, dueDate);

                return new SubmissionProgressExportRow
                {
                    ProjectId = p.Id,
                    Title = p.Title,
                    BusinessAreaName = p.BusinessAreaLookup?.Name ?? "Not set",
                    DirectorateName = dirName,
                    CurrentPeriodStatus = currentStatus,
                    CurrentPeriodSubmittedAt = submittedAt,
                    PeriodStatuses = periodColumns
                        .Select(col => ResolveExportPeriodSubmissionStatus(p, col.Year, col.Month))
                        .ToList()
                };
            })
            .ToList();
    }

    /// <summary>Dashboard rows per thematic tag for the thematic report (current reporting period).</summary>
    public async Task<ModernThematicReportDashboard> BuildThematicReportDashboardAsync(
        CancellationToken cancellationToken = default)
    {
        var nowUtc = DateTime.UtcNow;
        var calendarYearUtc = nowUtc.Year;
        var currentMonth = nowUtc.Month;
        var minReportYear = 2026;
        var currentPeriodDueDate = _monthlyUpdateService.GetMonthlyUpdateDueDate(calendarYearUtc, currentMonth);
        var daysUntilCurrentPeriodDueDate = (currentPeriodDueDate.Date - nowUtc.Date).Days;
        var defaultReportYear = daysUntilCurrentPeriodDueDate <= 10 ? calendarYearUtc : (currentMonth == 1 ? calendarYearUtc - 1 : calendarYearUtc);
        var defaultReportMonth = daysUntilCurrentPeriodDueDate <= 10 ? currentMonth : (currentMonth == 1 ? 12 : currentMonth - 1);
        defaultReportYear = Math.Max(minReportYear, defaultReportYear);

        var reportYear = defaultReportYear;
        var reportMonth = defaultReportMonth;
        var monthStart = new DateTime(reportYear, reportMonth, 1);
        var monthEnd = monthStart.AddMonths(1).AddDays(-1).AddHours(23).AddMinutes(59).AddSeconds(59);
        var todayUtc = nowUtc.Date;
        var upcomingWindowEnd = monthStart.AddDays(30);
        var dueDateForSubmission = _monthlyUpdateService.GetMonthlyUpdateDueDate(reportYear, reportMonth);
        var nowUtcForSubmission = nowUtc;

        var tags = await _db.WorkItemTagLookups.AsNoTracking()
            .Where(t => t.IsActive)
            .OrderBy(t => t.SortOrder)
            .ThenBy(t => t.Name)
            .ToListAsync(cancellationToken);

        var projects = await _db.Projects
            .AsNoTracking()
            .Include(p => p.BusinessAreaLookup)
            .Include(p => p.DeliveryPriority)
            .Include(p => p.Milestones)
            .Include(p => p.MonthlyUpdates)
                .ThenInclude(mu => mu.MonthlyUpdateNarratives)
            .Include(p => p.RagStatusLookup)
            .Include(p => p.ProjectWorkItemTags)
            .Where(p => !p.IsDeleted && p.Status != "Cancelled")
            .ToListAsync(cancellationToken);

        var rows = new List<ModernBusinessAreaDashboardRow>();
        foreach (var tag in tags)
        {
            var tagged = projects
                .Where(p => p.ProjectWorkItemTags.Any(l => l.WorkItemTagLookupId == tag.Id))
                .Where(p => p.Status is "Active" or "Paused")
                .ToList();
            if (tagged.Count == 0)
            {
                rows.Add(new ModernBusinessAreaDashboardRow
                {
                    BusinessArea = tag.Name,
                    BusinessAreaId = tag.Id,
                    Projects = new List<BusinessAreaProjectItem>()
                });
                continue;
            }

            rows.Add(BuildDashboardRowFromProjects(
                tagged,
                tag.Name,
                tag.Id,
                reportYear,
                reportMonth,
                monthStart,
                monthEnd,
                upcomingWindowEnd,
                todayUtc,
                nowUtcForSubmission,
                dueDateForSubmission));
        }

        var activeTagIds = tags.Select(t => t.Id).ToHashSet();
        var scope = projects
            .Where(p => p.Status is "Active" or "Paused" or "Completed")
            .Where(p => p.ProjectWorkItemTags.Any(l => activeTagIds.Contains(l.WorkItemTagLookupId)))
            .Select(p => ToBusinessAreaProjectItem(p, reportYear, reportMonth, monthStart, monthEnd, upcomingWindowEnd, todayUtc))
            .OrderBy(x => x.Title)
            .ToList();

        return new ModernThematicReportDashboard
        {
            ReportYear = reportYear,
            ReportMonth = reportMonth,
            MonthName = monthStart.ToString("MMMM yyyy"),
            Rows = rows,
            ScopeProjectItems = scope
        };
    }

}
