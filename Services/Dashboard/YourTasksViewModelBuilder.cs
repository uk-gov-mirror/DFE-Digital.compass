using System.Globalization;
using Compass.Data;
using Compass.Models;
using Compass.Services;
using Compass.ViewModels.Dashboard;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Compass.Services.Dashboard;

public sealed class YourTasksViewModelBuilder : IYourTasksViewModelBuilder
{
    private const int VisibleTaskLimit = 8;

    private static bool IsOpenForSubmissionWindow(Commission commission, DateTime now) =>
        commission.IsActive && now >= commission.OpenDate && now <= commission.DueDate.AddDays(1);

    private readonly CompassDbContext _context;
    private readonly IProductsApiService _productsApi;
    private readonly IMonthlyUpdateService _monthlyUpdateService;
    private readonly IPerformanceReportingEligibilityService _eligibilityService;

    public YourTasksViewModelBuilder(
        CompassDbContext context,
        IProductsApiService productsApi,
        IMonthlyUpdateService monthlyUpdateService,
        IPerformanceReportingEligibilityService eligibilityService)
    {
        _context = context;
        _productsApi = productsApi;
        _monthlyUpdateService = monthlyUpdateService;
        _eligibilityService = eligibilityService;
    }

    public YourTasksViewModel Build(YourTasksBuildInput input) =>
        BuildCore(
            input.MyProjects,
            input.OverdueMilestones,
            input.MilestonesDueThisWeek,
            input.HighPriorityIssues,
            input.AssignedActions,
            input.ShowRaidIssues,
            input.MonthlyReportingWindowOpen,
            input.MonthlyReportingRemainingCount,
            input.ReportingPeriodLabel,
            input.PerformanceReturnsDueCount,
            input.Links,
            input.IdPrefix,
            input.Url);

    public async Task<YourTasksViewModel> BuildAsync(
        User currentUser,
        string userEmail,
        IUrlHelper url,
        bool showRaidIssues,
        YourTasksLinkOptions links,
        string idPrefix = "dashboard-task",
        CancellationToken cancellationToken = default)
    {
        var emailLower = userEmail.ToLowerInvariant();
        var myProjects = await _context.Projects
            .AsNoTracking()
            .Where(p => !p.IsDeleted && p.Status == "Active" && (
                p.ProjectContacts.Any(pc => pc.Email.ToLower() == emailLower) ||
                (p.PrimaryContactUser != null && p.PrimaryContactUser.Email.ToLower() == emailLower) ||
                p.SeniorResponsibleOfficers.Any(sro => sro.User != null && sro.User.Email.ToLower() == emailLower) ||
                p.ServiceOwners.Any(so => so.User != null && so.User.Email.ToLower() == emailLower) ||
                p.PmoContacts.Any(pmo => pmo.User != null && pmo.User.Email.ToLower() == emailLower)))
            .Include(p => p.Milestones)
            .Include(p => p.Issues)
            .OrderBy(p => p.Title)
            .ToListAsync(cancellationToken);

        var allActiveMilestones = myProjects.SelectMany(p => p.Milestones.Where(m => !m.IsDeleted)).ToList();
        var milestonesDueThisWeek = allActiveMilestones
            .Where(m => m.DueDate >= DateTime.Today && m.DueDate <= DateTime.Today.AddDays(7))
            .ToList();
        var overdueMilestones = allActiveMilestones
            .Where(m => m.DueDate < DateTime.Today && m.Status != "complete")
            .ToList();

        var allActiveIssues = myProjects.SelectMany(p => p.Issues.Where(i => !i.IsDeleted)).ToList();
        var highPriorityIssues = allActiveIssues
            .Where(i => i.Severity == "high" || i.Severity == "critical")
            .ToList();

        var assignedActions = await _context.Actions
            .AsNoTracking()
            .Include(a => a.Project)
            .Where(a => !a.IsDeleted && (
                (!string.IsNullOrEmpty(a.AssignedToEmail) && a.AssignedToEmail.ToLower() == emailLower) ||
                a.AssignedToUserId == currentUser.Id))
            .OrderBy(a => a.DueDate ?? DateTime.MaxValue)
            .ThenBy(a => a.Status)
            .Take(15)
            .ToListAsync(cancellationToken);

        var now = DateTime.UtcNow;
        var (applicableYear, applicableMonth) = _monthlyUpdateService.ResolveDashboardReportingPeriod(now);
        var explicitMonthlyPeriod = _monthlyUpdateService.TryGetActiveExplicitReportingPeriod(applicableYear, applicableMonth);
        var reportingPeriodLabel = !string.IsNullOrWhiteSpace(explicitMonthlyPeriod?.PeriodLabel)
            ? explicitMonthlyPeriod!.PeriodLabel.Trim()
            : new DateTime(applicableYear, applicableMonth, 1).ToString("MMMM yyyy", CultureInfo.GetCultureInfo("en-GB"));

        var monthlyUpdateStatusByProjectId = new Dictionary<int, UpdateSubmissionStatus>();
        foreach (var project in myProjects)
        {
            var update = await _context.ProjectMonthlyUpdates.AsNoTracking()
                .FirstOrDefaultAsync(u => u.ProjectId == project.Id && u.Year == applicableYear && u.Month == applicableMonth, cancellationToken);
            monthlyUpdateStatusByProjectId[project.Id] =
                _monthlyUpdateService.CalculateUpdateStatus(applicableYear, applicableMonth, update?.SubmittedAt);
        }

        var projectsNeedingMonthlyUpdates = myProjects.Count(p =>
            monthlyUpdateStatusByProjectId.TryGetValue(p.Id, out var status) &&
            (status == UpdateSubmissionStatus.Due || status == UpdateSubmissionStatus.Late));

        var hasReportingEligibleWork = myProjects.Any(p =>
            string.Equals(p.Status, "Active", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(p.Status, "Paused", StringComparison.OrdinalIgnoreCase));
        var monthlyReportingWindowOpen = hasReportingEligibleWork &&
            _monthlyUpdateService.IsMonthlyReportEditingAllowed(applicableYear, applicableMonth);

        var myProducts = await LoadMyProductsAsync(userEmail, cancellationToken);
        var productsNeedingCommissionReporting =
            await LoadProductsNeedingCommissionReportingAsync(myProducts, cancellationToken);

        return BuildCore(
            myProjects,
            overdueMilestones,
            milestonesDueThisWeek,
            highPriorityIssues,
            assignedActions,
            showRaidIssues,
            monthlyReportingWindowOpen,
            projectsNeedingMonthlyUpdates,
            reportingPeriodLabel,
            productsNeedingCommissionReporting.Count,
            links,
            idPrefix,
            url);
    }

    public async Task<List<(ProductDto Product, Commission Commission, CommissionSubmissionStatus Status, DateTime DueDate)>>
        LoadProductsNeedingCommissionReportingAsync(
            IReadOnlyList<ProductDto> myProducts,
            CancellationToken cancellationToken = default)
    {
        var results = new List<(ProductDto, Commission, CommissionSubmissionStatus, DateTime)>();
        var now = DateTime.UtcNow;
        var activeCommissions = await _context.Commissions.AsNoTracking()
            .Where(c => c.IsActive && c.OpenDate <= now)
            .OrderByDescending(c => c.DueDate)
            .ToListAsync(cancellationToken);
        var eligibilityCache = await _eligibilityService.LoadEligibilityCacheAsync();

        foreach (var commission in activeCommissions)
        {
            if (!IsOpenForSubmissionWindow(commission, now))
                continue;

            var userProductDocumentIds = myProducts
                .Where(p => !string.IsNullOrEmpty(p.DocumentId) &&
                            p.State != null &&
                            !p.State.Equals("Decommissioned", StringComparison.OrdinalIgnoreCase) &&
                            !p.State.Equals("Decommissioning", StringComparison.OrdinalIgnoreCase) &&
                            p.PublishedAt.HasValue)
                .Select(p => p.DocumentId!)
                .ToList();

            if (userProductDocumentIds.Count == 0)
                continue;

            var existingSubmissions = await _context.CommissionSubmissions.AsNoTracking()
                .Where(cs => cs.CommissionId == commission.Id &&
                             userProductDocumentIds.Contains(cs.ProductDocumentId))
                .ToDictionaryAsync(cs => cs.ProductDocumentId, cs => cs, cancellationToken);

            foreach (var product in myProducts.Where(p => userProductDocumentIds.Contains(p.DocumentId ?? "")))
            {
                var documentId = product.DocumentId ?? "";
                if (string.IsNullOrEmpty(documentId))
                    continue;

                if (!CommissionReportingProductScope.ProductMatchesCommissionInScopeRules(commission, product))
                    continue;

                if (_eligibilityService.IsProductExcludedForCommission(product, commission, eligibilityCache))
                    continue;

                var submission = existingSubmissions.GetValueOrDefault(documentId);
                var status = submission?.Status ?? CommissionSubmissionStatus.NotStarted;
                if (status == CommissionSubmissionStatus.Submitted)
                    continue;

                var finalStatus = now > commission.DueDate
                    ? CommissionSubmissionStatus.Late
                    : status;

                results.Add((product, commission, finalStatus, commission.DueDate));
            }
        }

        return results;
    }

    private async Task<List<ProductDto>> LoadMyProductsAsync(string userEmail, CancellationToken cancellationToken)
    {
        var productsByServiceOwner = await _productsApi.GetProductsByServiceOwnerAsync(userEmail);
        var productsByProductManager = await _productsApi.GetProductsByProductManagerAsync(userEmail);
        var productsByDeliveryManager = await _productsApi.GetProductsByDeliveryManagerAsync(userEmail);
        var productsByReportingUser = await _productsApi.GetProductsByReportingUserAsync(userEmail);

        return productsByServiceOwner
            .Concat(productsByProductManager)
            .Concat(productsByDeliveryManager)
            .Concat(productsByReportingUser)
            .GroupBy(p => p.FipsId)
            .Where(g => !string.IsNullOrEmpty(g.Key))
            .Select(g => g.First())
            .OrderBy(p => p.Title)
            .ToList();
    }

    private static YourTasksViewModel BuildCore(
        IReadOnlyList<Project> myProjects,
        IReadOnlyList<Milestone> overdueMilestones,
        IReadOnlyList<Milestone> milestonesDueThisWeek,
        IReadOnlyList<Issue> highPriorityIssues,
        IReadOnlyList<Models.Action> assignedActions,
        bool showRaidIssues,
        bool monthlyReportingWindowOpen,
        int monthlyReportingRemainingCount,
        string reportingPeriodLabel,
        int performanceReturnsDueCount,
        YourTasksLinkOptions links,
        string idPrefix,
        IUrlHelper? url)
    {
        var today = DateTime.Today;
        var ci = CultureInfo.GetCultureInfo("en-GB");
        var milestoneTasks = new List<YourTaskRow>();

        string WorkMilestonesHref(int projectId) =>
            (url?.Action("Detail", "ModernWork", new { id = projectId, tab = "milestones" }) ?? $"/modern/work/{projectId}")
            + "#wd-milestones";

        string IssueHref(int issueId) =>
            url?.Action("Details", "Issue", new { id = issueId }) ?? $"/Issue/Details/{issueId}";

        string ActionHref(int actionId) =>
            url?.Action("Details", "Action", new { id = actionId }) ?? $"/Action/Details/{actionId}";

        foreach (var m in overdueMilestones)
        {
            if (!m.ProjectId.HasValue)
                continue;
            var projectId = m.ProjectId.Value;
            var proj = myProjects.FirstOrDefault(p => p.Id == projectId);
            var milestoneHint = proj != null ? $"Milestone · {proj.Title}" : "Milestone";
            milestoneHint = $"Due {m.DueDate.ToString("d MMM yyyy", ci)} · {milestoneHint}";
            milestoneTasks.Add(new YourTaskRow
            {
                Title = m.Name,
                Hint = milestoneHint,
                Href = WorkMilestonesHref(projectId),
                StatusTag = "Late"
            });
        }

        foreach (var m in milestonesDueThisWeek)
        {
            if (!m.ProjectId.HasValue)
                continue;
            var projectId = m.ProjectId.Value;
            var proj = myProjects.FirstOrDefault(p => p.Id == projectId);
            var milestoneHint = proj != null ? $"Milestone · {proj.Title}" : "Milestone";
            milestoneHint = $"Due {m.DueDate.ToString("d MMM yyyy", ci)} · {milestoneHint}";
            milestoneTasks.Add(new YourTaskRow
            {
                Title = m.Name,
                Hint = milestoneHint,
                Href = WorkMilestonesHref(projectId),
                StatusTag = "Due"
            });
        }

        var otherTasks = new List<YourTaskRow>();
        if (showRaidIssues)
        {
            foreach (var i in highPriorityIssues
                         .Where(i => !IsClosedIssue(i))
                         .Take(8))
            {
                var proj = myProjects.FirstOrDefault(p => p.Id == i.ProjectId);
                var issueLate = i.TargetResolutionDate.HasValue && i.TargetResolutionDate.Value.Date < today;
                otherTasks.Add(new YourTaskRow
                {
                    Title = i.Title,
                    Hint = proj != null ? $"Issue · {proj.Title}" : "Issue",
                    Href = IssueHref(i.Id),
                    StatusTag = issueLate ? "Late" : "Due"
                });
            }
        }

        foreach (var a in assignedActions
                     .Where(a => !IsCompletedAction(a))
                     .Take(15))
        {
            var actionLate = a.DueDate.HasValue && a.DueDate.Value.Date < today;
            otherTasks.Add(new YourTaskRow
            {
                Title = a.Title,
                Hint = a.Project?.Title ?? "Action",
                Href = ActionHref(a.Id),
                StatusTag = actionLate ? "Late" : "Due"
            });
        }

        var orderedTasks = milestoneTasks.Concat(otherTasks).ToList();
        var showMonthlyReportingTask = monthlyReportingWindowOpen && monthlyReportingRemainingCount > 0;
        var showPerformanceReturnsTask = performanceReturnsDueCount > 0;
        var reservedSlots = (showMonthlyReportingTask ? 1 : 0) + (showPerformanceReturnsTask ? 1 : 0);
        var visibleSlots = Math.Max(0, VisibleTaskLimit - reservedSlots);

        return new YourTasksViewModel
        {
            MonthlyReportingWindowOpen = monthlyReportingWindowOpen,
            MonthlyReportingRemainingCount = monthlyReportingRemainingCount,
            ReportingPeriodLabel = reportingPeriodLabel,
            PerformanceReturnsDueCount = performanceReturnsDueCount,
            MonthlyReportingHref = links.MonthlyReportingHref,
            PerformanceCommissionsHref = links.PerformanceCommissionsHref,
            AllWorkHref = links.AllWorkHref,
            ShowMonthlyReportingTask = showMonthlyReportingTask,
            ShowPerformanceReturnsTask = showPerformanceReturnsTask,
            VisibleTasks = orderedTasks.Take(visibleSlots).ToList(),
            HiddenTasks = orderedTasks.Skip(visibleSlots).ToList(),
            HasAnyTasks = showMonthlyReportingTask || showPerformanceReturnsTask || orderedTasks.Count > 0,
            IdPrefix = idPrefix
        };
    }

    private static bool IsClosedIssue(Issue issue)
    {
        var status = issue.Status?.Trim() ?? "";
        return status.Equals("resolved", StringComparison.OrdinalIgnoreCase)
            || status.Equals("closed", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCompletedAction(Models.Action action)
    {
        var status = action.Status?.Trim() ?? "";
        return status.Equals("complete", StringComparison.OrdinalIgnoreCase)
            || status.Equals("completed", StringComparison.OrdinalIgnoreCase)
            || status.Equals("done", StringComparison.OrdinalIgnoreCase)
            || status.Equals("cancelled", StringComparison.OrdinalIgnoreCase)
            || status.Equals("closed", StringComparison.OrdinalIgnoreCase);
    }
}
