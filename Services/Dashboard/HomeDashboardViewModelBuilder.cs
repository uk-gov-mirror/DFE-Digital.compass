using System;
using System.Collections.Generic;
using System.Linq;
using Compass.Data;
using Compass.Helpers;
using Compass.Models;
using Compass.Services;
using Compass.ViewModels.Dashboard;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Compass.Services.Dashboard;

public class HomeDashboardViewModelBuilder : IHomeDashboardViewModelBuilder
{
    private readonly ILogger<HomeDashboardViewModelBuilder> _logger;
    private readonly IProductsApiService _productsApiService;
    private readonly IReturnStatusService _returnStatusService;
    private readonly IMonthlyUpdateService _monthlyUpdateService;
    private readonly CompassDbContext _context;
    private readonly IWebHostEnvironment _environment;
    private readonly IConfiguration _configuration;
    private readonly IYourTasksViewModelBuilder _yourTasksBuilder;

    public HomeDashboardViewModelBuilder(
        ILogger<HomeDashboardViewModelBuilder> logger,
        IProductsApiService productsApiService,
        IReturnStatusService returnStatusService,
        IMonthlyUpdateService monthlyUpdateService,
        CompassDbContext context,
        IWebHostEnvironment environment,
        IConfiguration configuration,
        IYourTasksViewModelBuilder yourTasksBuilder)
    {
        _logger = logger;
        _productsApiService = productsApiService;
        _returnStatusService = returnStatusService;
        _monthlyUpdateService = monthlyUpdateService;
        _context = context;
        _environment = environment;
        _configuration = configuration;
        _yourTasksBuilder = yourTasksBuilder;
    }

    public async Task<UserPreference> GetOrCreateDashboardPreferenceAsync(User user)
    {
        var preference = await _context.UserPreferences
            .FirstOrDefaultAsync(up => up.UserId == user.Id);

        if (preference != null)
        {
            return preference;
        }

        preference = new UserPreference
        {
            UserId = user.Id,
            PreferredTaskGrouping = "priority",
            ShowTasksPanel = true,
            ShowProductPanel = true,
            ShowRiskPanel = true,
            ShowMilestonePanel = true,
            ShowRemindersPanel = true,
            ShowSuccessPanel = true
        };

        _context.UserPreferences.Add(preference);
        await _context.SaveChangesAsync();

        return preference;
    }

    public async Task<HomeDashboardViewModel> BuildDashboardViewModelAsync(User currentUser, string userEmail, UserPreference preference, IUrlHelper url, HttpContext httpContext, bool showRaidIssues = true)
    {
        var myProjects = await _context.Projects
            .Where(p => !p.IsDeleted && p.Status == "Active" && (
                p.ProjectContacts.Any(pc => pc.Email.ToLower() == userEmail.ToLower()) ||
                (p.PrimaryContactUser != null && p.PrimaryContactUser.Email.ToLower() == userEmail.ToLower()) ||
                p.SeniorResponsibleOfficers.Any(sro => sro.User != null && sro.User.Email.ToLower() == userEmail.ToLower()) ||
                p.ServiceOwners.Any(so => so.User != null && so.User.Email.ToLower() == userEmail.ToLower()) ||
                p.PmoContacts.Any(pmo => pmo.User != null && pmo.User.Email.ToLower() == userEmail.ToLower())
            ))
            .Include(p => p.ProjectContacts)
                .ThenInclude(pc => pc.User)
            .Include(p => p.PrimaryContactUser)
            .Include(p => p.SeniorResponsibleOfficers)
                .ThenInclude(sro => sro.User)
            .Include(p => p.ServiceOwners)
                .ThenInclude(so => so.User)
            .Include(p => p.PmoContacts)
                .ThenInclude(pmo => pmo.User)
            .Include(p => p.DeliveryPriority)
            .Include(p => p.RagStatusLookup)
            .Include(p => p.PhaseLookup)
            .Include(p => p.BusinessAreaLookup)
            .Include(p => p.Milestones)
            .Include(p => p.Issues)
            .Include(p => p.Risks)
            .Include(p => p.Actions)
            .Include(p => p.Decisions)
            .Include(p => p.ProjectProducts)
            .Include(p => p.Successes)
            .Include(p => p.MonthlyUpdates)
            .OrderBy(p => p.Title)
            .ToListAsync();

        // Fetch watched projects
        var watchedProjectIds = await _context.ProjectWatchlists
            .Where(w => w.UserId == currentUser.Id)
            .Select(w => w.ProjectId)
            .ToListAsync();

        var watchedProjects = await _context.Projects
            .Where(p => !p.IsDeleted && watchedProjectIds.Contains(p.Id))
            .Include(p => p.ProjectContacts)
            .Include(p => p.PrimaryContactUser)
            .Include(p => p.DeliveryPriority)
            .Include(p => p.RagStatusLookup)
            .Include(p => p.PhaseLookup)
            .Include(p => p.BusinessAreaLookup)
            .Include(p => p.Milestones)
            .Include(p => p.Issues)
            .Include(p => p.Risks)
            .Include(p => p.Actions)
            .Include(p => p.Decisions)
            .Include(p => p.ProjectProducts)
            .Include(p => p.Successes)
            .OrderBy(p => p.Title)
            .ToListAsync();

        // Fetch user's products - from service_owner, product_manager, delivery_manager, and reporting_user (same approach as ProductReportingController)
        var productsByServiceOwner = await _productsApiService.GetProductsByServiceOwnerAsync(userEmail);
        var productsByProductManager = await _productsApiService.GetProductsByProductManagerAsync(userEmail);
        var productsByDeliveryManager = await _productsApiService.GetProductsByDeliveryManagerAsync(userEmail);
        var productsByReportingUser = await _productsApiService.GetProductsByReportingUserAsync(userEmail);
        
        // Combine and deduplicate products (by FipsId)
        var myProducts = productsByServiceOwner
            .Concat(productsByProductManager)
            .Concat(productsByDeliveryManager)
            .Concat(productsByReportingUser)
            .GroupBy(p => p.FipsId)
            .Where(g => !string.IsNullOrEmpty(g.Key))
            .Select(g => g.First())
            .OrderBy(p => p.Title)
            .ToList();

        var allActiveMilestones = myProjects.SelectMany(p => p.Milestones.Where(m => !m.IsDeleted)).ToList();
        var milestonesDueThisWeek = allActiveMilestones
            .Where(m => m.DueDate >= DateTime.Today && m.DueDate <= DateTime.Today.AddDays(7)).ToList();
        var overdueMilestones = allActiveMilestones
            .Where(m => m.DueDate < DateTime.Today && m.Status != "complete").ToList();

        var allActiveIssues = myProjects.SelectMany(p => p.Issues.Where(i => !i.IsDeleted)).ToList();
        var highPriorityIssues = allActiveIssues.Where(i => i.Severity == "high" || i.Severity == "critical").ToList();
        var openIssues = allActiveIssues.Where(i => i.Status != "resolved" && i.Status != "closed").ToList();

        var redProjects = myProjects.Where(p => p.RagStatusLookup != null && p.RagStatusLookup.Name == "Red").ToList();
        var amberRedProjects = myProjects.Where(p => p.RagStatusLookup != null && p.RagStatusLookup.Name == "Amber-Red").ToList();
        var atRiskProjects = redProjects.Concat(amberRedProjects).ToList();

        var thirtyDaysAgo = DateTime.UtcNow.AddDays(-30);
        var recentSuccesses = myProjects.SelectMany(p => p.Successes.Where(s => s.RecordedAt >= thirtyDaysAgo))
            .OrderByDescending(s => s.RecordedAt)
            .Take(10)
            .ToList();

        var projectsNeedingPathToGreen = atRiskProjects.Where(p => string.IsNullOrWhiteSpace(p.PathToGreen)).ToList();

        var expiredOpenMilestones = allActiveMilestones
            .Where(m => m.DueDate < DateTime.Today && m.Status != "complete" && m.Status != "cancelled")
            .OrderBy(m => m.DueDate)
            .ToList();

        var now = DateTime.UtcNow;
        var reportYear = now.Year;
        var reportMonth = now.Month;
        var currentPeriodDueDate = _monthlyUpdateService.GetMonthlyUpdateDueDate(reportYear, reportMonth);
        var daysUntilCurrentPeriodDueDate = (currentPeriodDueDate - now).Days;
        // Performance returns still use legacy applicable-period heuristic.
        var performanceReturnYear = daysUntilCurrentPeriodDueDate <= 10 ? reportYear : (reportMonth == 1 ? reportYear - 1 : reportYear);
        var performanceReturnMonth = daysUntilCurrentPeriodDueDate <= 10 ? reportMonth : (reportMonth == 1 ? 12 : reportMonth - 1);
        // Monthly delivery updates: align with work dashboard (open submission window for calendar month when active).
        var (applicableYear, applicableMonth) = _monthlyUpdateService.ResolveDashboardReportingPeriod(now);
        var explicitMonthlyPeriod = _monthlyUpdateService.TryGetActiveExplicitReportingPeriod(applicableYear, applicableMonth);
        var applicableMonthlyUpdatePeriodLabel = !string.IsNullOrWhiteSpace(explicitMonthlyPeriod?.PeriodLabel)
            ? explicitMonthlyPeriod!.PeriodLabel.Trim()
            : new DateTime(applicableYear, applicableMonth, 1).ToString("MMMM yyyy", System.Globalization.CultureInfo.GetCultureInfo("en-GB"));

        var productsNeedingReturns = new List<(ProductDto Product, ReturnStatus Status, DateTime DueDate)>();
        var enableMonthlyPerformanceReporting = _configuration.GetValue<bool>("FeatureFlags:EnableMonthlyPerformanceReporting", true);
        
        if (enableMonthlyPerformanceReporting)
        {
            foreach (var product in myProducts.Where(p => !string.IsNullOrEmpty(p.FipsId)))
            {
                var productReturn = await _context.ProductReturns
                    .Where(pr => pr.FipsId == product.FipsId && pr.Year == performanceReturnYear && pr.Month == performanceReturnMonth)
                    .FirstOrDefaultAsync();

                var status = _returnStatusService.CalculateReturnStatus(
                    performanceReturnYear,
                    performanceReturnMonth,
                    productReturn?.SubmittedDate);

                if (status == ReturnStatus.Due || status == ReturnStatus.Late)
                {
                    var dueDate = _returnStatusService.GetReturnDueDate(performanceReturnYear, performanceReturnMonth);
                    productsNeedingReturns.Add((product, status, dueDate));
                }
            }
        }

        // Monthly update status per project (aligned with work dashboard submission window)
        var monthlyUpdateStatusByProjectId = new Dictionary<int, UpdateSubmissionStatus>();
        foreach (var project in myProjects)
        {
            var update = project.MonthlyUpdates?.FirstOrDefault(u => u.Year == applicableYear && u.Month == applicableMonth);
            var updateStatus = _monthlyUpdateService.CalculateUpdateStatus(applicableYear, applicableMonth, update?.SubmittedAt);
            monthlyUpdateStatusByProjectId[project.Id] = updateStatus;
        }

        var projectsNeedingMonthlyUpdates = new List<(Project Project, UpdateSubmissionStatus Status, DateTime DueDate)>();
        foreach (var project in myProjects)
        {
            if (!monthlyUpdateStatusByProjectId.TryGetValue(project.Id, out var updateStatus))
                continue;
            if (updateStatus == UpdateSubmissionStatus.Due || updateStatus == UpdateSubmissionStatus.Late)
            {
                var dueDate = _monthlyUpdateService.GetMonthlyUpdateDueDate(applicableYear, applicableMonth);
                projectsNeedingMonthlyUpdates.Add((project, updateStatus, dueDate));
            }
        }

        var hasReportingEligibleWork = myProjects.Any(p =>
            string.Equals(p.Status, "Active", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(p.Status, "Paused", StringComparison.OrdinalIgnoreCase));
        var monthlyReportingWindowOpen = hasReportingEligibleWork &&
            _monthlyUpdateService.IsMonthlyReportEditingAllowed(applicableYear, applicableMonth);
        var monthlyReportingRemainingCount = projectsNeedingMonthlyUpdates.Count;

        // Calculate products needing commission reporting
        var productsNeedingCommissionReporting =
            await _yourTasksBuilder.LoadProductsNeedingCommissionReportingAsync(myProducts);

        var assignedActions = await _context.Actions
            .Include(a => a.Project)
            .Where(a => !a.IsDeleted && (
                (!string.IsNullOrEmpty(a.AssignedToEmail) && a.AssignedToEmail.ToLower() == userEmail.ToLower()) ||
                (a.AssignedToUserId == currentUser.Id)))
            .OrderBy(a => a.DueDate ?? DateTime.MaxValue)
            .ThenBy(a => a.Status)
            .Take(10)
            .ToListAsync();

        var unmonitoredRisks = myProjects
            .SelectMany(p => p.Risks.Where(r => !r.IsDeleted && r.Status != "closed"))
            .Where(r => !r.NextReviewDate.HasValue || r.NextReviewDate.Value < DateTime.Today.AddDays(-30))
            .OrderByDescending(r => r.RiskScore)
            .ThenBy(r => r.Title)
            .Take(10)
            .ToList();

        var priorityTasks = BuildPriorityTasks(
            projectsNeedingPathToGreen,
            expiredOpenMilestones,
            productsNeedingReturns,
            highPriorityIssues,
            assignedActions,
            myProjects,
            url);

        var reminders = BuildReminderCards(myProjects, myProducts, productsNeedingReturns, unmonitoredRisks, url);
        var quickLinkOptions = BuildQuickLinkOptions(preference, myProjects.Any(), myProducts.Any(), url);
        var quickLinks = quickLinkOptions
            .Where(o => o.Selected && !o.Disabled)
            .Take(4)
            .Select(o => new DashboardQuickLink
            {
                Code = o.Code,
                Title = o.Title,
                Description = o.Description,
                Icon = o.Icon,
                Url = o.Url
            })
            .ToList();

        // Get watched deliverables count
        var watchedDeliverablesCount = await _context.ProjectWatchlists
            .Where(w => w.UserId == currentUser.Id)
            .CountAsync();

        var metrics = new DashboardMetrics
        {
            TasksDue = priorityTasks.Count,
            ServiceHealthIssues = productsNeedingReturns.Count,
            ProjectHealthIssues = atRiskProjects.Count,
            ProductCount = myProducts.Count,
            UpcomingMilestones = milestonesDueThisWeek.Count,
            OpenIssues = openIssues.Count,
            UnreviewedRisks = unmonitoredRisks.Count,
            WatchedDeliverables = watchedDeliverablesCount
        };

        // Check for test role override (development only)
        LeadershipRoleTier? testRoleOverride = null;
        if (_environment.IsDevelopment() && httpContext != null &&
            httpContext.Request.Cookies.TryGetValue("TestDashboardRole", out var testRoleValue))
        {
            if (Enum.TryParse<LeadershipRoleTier>(testRoleValue, true, out var parsedRole))
            {
                testRoleOverride = parsedRole;
            }
        }

        var leadershipAssignments = await _context.UserBusinessAreaRoleAssignments
            .Where(a => a.UserId == currentUser.Id)
            .OrderByDescending(a => a.Role)
            .ToListAsync();

        var leadershipBusinessAreas = leadershipAssignments
            .Select(a => a.BusinessAreaName?.Trim())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Use test role override if available, otherwise use actual role
        var highestRole = testRoleOverride ?? (leadershipAssignments.Any()
            ? leadershipAssignments.Max(a => a.Role)
            : (LeadershipRoleTier?)null);
        
        // If test role is set, override business areas for business area leader roles
        if (testRoleOverride.HasValue && 
            (testRoleOverride == LeadershipRoleTier.DeputyDirectorOrSro || 
             testRoleOverride == LeadershipRoleTier.HeadOfProfession ||
             testRoleOverride == LeadershipRoleTier.PortfolioLead))
        {
            // For testing, use all business areas if none are assigned
            if (!leadershipBusinessAreas.Any())
            {
                leadershipBusinessAreas = await _context.Projects
                    .Where(p => !p.IsDeleted && p.BusinessAreaLookup != null && !string.IsNullOrWhiteSpace(p.BusinessAreaLookup.Name))
                    .Select(p => p.BusinessAreaLookup!.Name)
                    .Distinct()
                    .Take(3)
                    .ToListAsync();
            }
        }

        var leadershipProjects = new List<Project>();
        var leadershipMetrics = new DashboardMetrics();
        var enterpriseMetrics = new EnterpriseLeadershipMetrics();
        var activeMissions = new List<Mission>();
        var priorityOutcomes = new List<Objective>();
        var enterpriseAtRiskProjects = new List<Project>();

        // Gather data based on leadership role
        if (highestRole.HasValue)
        {
            if (highestRole == LeadershipRoleTier.PermanentSecretary || 
                highestRole == LeadershipRoleTier.DirectorGeneral || 
                highestRole == LeadershipRoleTier.CLevel)
            {
                // Enterprise-wide view for PS, DG, C-Level
                var allProjects = await _context.Projects
                    .Where(p => !p.IsDeleted)
                    .Include(p => p.Issues.Where(i => !i.IsDeleted))
                    .Include(p => p.Risks.Where(r => !r.IsDeleted))
                    .Include(p => p.Actions.Where(a => !a.IsDeleted))
                    .ToListAsync();

                bool IsOpen(string? status) =>
                    !string.Equals(status, "closed", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(status, "resolved", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(status, "complete", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase);

                var allOpenIssues = allProjects
                    .SelectMany(p => p.Issues.Where(i => IsOpen(i.Status)))
                    .ToList();

                var allOpenRisks = allProjects
                    .SelectMany(p => p.Risks.Where(r => !string.Equals(r.Status, "closed", StringComparison.OrdinalIgnoreCase)))
                    .ToList();

                var allOpenActions = allProjects
                    .SelectMany(p => p.Actions.Where(a => IsOpen(a.Status)))
                    .ToList();

                enterpriseAtRiskProjects = allProjects
                    .Where(p => (p.RagStatusLookup != null && string.Equals(p.RagStatusLookup.Name, "Red", StringComparison.OrdinalIgnoreCase)) ||
                                (p.RagStatusLookup != null && string.Equals(p.RagStatusLookup.Name, "Amber-Red", StringComparison.OrdinalIgnoreCase)))
                    .ToList();

                enterpriseMetrics = new EnterpriseLeadershipMetrics
                {
                    TotalOpenIssues = allOpenIssues.Count,
                    TotalOpenRisks = allOpenRisks.Count,
                    TotalOpenActions = allOpenActions.Count,
                    TotalAtRiskProjects = enterpriseAtRiskProjects.Count
                };

                // Get active missions
                activeMissions = await _context.Missions
                    .Where(m => !m.IsDeleted && 
                                (m.Status == null || m.Status == "Active" || string.Equals(m.Status, "active", StringComparison.OrdinalIgnoreCase)))
                    .OrderBy(m => m.Title)
                    .ToListAsync();

                enterpriseMetrics.ActiveMissionsCount = activeMissions.Count;

                // Get priority outcomes (active objectives linked to missions)
                priorityOutcomes = await _context.Objectives
                    .Where(o => !o.IsDeleted && 
                                (o.Status == "active" || string.Equals(o.Status, "active", StringComparison.OrdinalIgnoreCase)) &&
                                o.MissionId.HasValue)
                    .Include(o => o.Mission)
                    .OrderBy(o => o.Title)
                    .ToListAsync();

                enterpriseMetrics.ActivePriorityOutcomesCount = priorityOutcomes.Count;
            }
            else if (highestRole == LeadershipRoleTier.DeputyDirectorOrSro || 
                     highestRole == LeadershipRoleTier.HeadOfProfession ||
                     highestRole == LeadershipRoleTier.PortfolioLead)
            {
                // Business area specific view for Deputy Director and G6
                if (leadershipBusinessAreas.Any())
                {
                    var normalizedAreas = leadershipBusinessAreas
                        .Select(name => name.ToLower())
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);

                    leadershipProjects = await _context.Projects
                        .Where(p => !p.IsDeleted
                                    && p.BusinessAreaLookup != null
                                    && !string.IsNullOrWhiteSpace(p.BusinessAreaLookup.Name)
                                    && normalizedAreas.Contains(p.BusinessAreaLookup.Name.ToLower()))
                        .Include(p => p.BusinessAreaLookup)
                        .Include(p => p.Milestones.Where(m => !m.IsDeleted))
                        .Include(p => p.Issues.Where(i => !i.IsDeleted))
                        .Include(p => p.Risks.Where(r => !r.IsDeleted))
                        .Include(p => p.Actions.Where(a => !a.IsDeleted))
                        .OrderBy(p => p.Title)
                        .ToListAsync();

                    leadershipMetrics = BuildLeadershipMetrics(leadershipProjects);
                    leadershipMetrics.WatchedDeliverables = watchedDeliverablesCount;
                }
            }
        }
        
        // Set watched deliverables count for leadership metrics if not already set
        if (leadershipMetrics != null && leadershipMetrics.WatchedDeliverables == 0)
        {
            leadershipMetrics.WatchedDeliverables = watchedDeliverablesCount;
        }

        var sectionConfig = new DashboardSectionConfig
        {
            ShowTasksPanel = preference.ShowTasksPanel,
            ShowProductPanel = preference.ShowProductPanel,
            ShowRiskPanel = preference.ShowRiskPanel,
            ShowMilestonePanel = preference.ShowMilestonePanel,
            ShowRemindersPanel = preference.ShowRemindersPanel,
            ShowSuccessPanel = preference.ShowSuccessPanel,
            PreferredTaskGrouping = preference.PreferredTaskGrouping,
            DashboardFocus = preference.DashboardFocus
        };

        var firstName = !string.IsNullOrWhiteSpace(currentUser.FirstName) 
            ? currentUser.FirstName 
            : ExtractFirstName(currentUser.Name);

        var blockDefinitions = DashboardLayoutHelper.GetBlockCatalog();
        var blockInstances = DashboardLayoutHelper.ParseLayout(preference.DashboardLayout, blockDefinitions);
        if (!blockInstances.Any())
        {
            blockInstances = DashboardLayoutHelper.GetDefaultLayout(blockDefinitions);
        }

        if (string.IsNullOrWhiteSpace(preference.DashboardLayout))
        {
            preference.DashboardLayout = DashboardLayoutHelper.SerializeLayout(blockInstances);
            preference.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
        }

        // Get DDT Standards for the user (only published standards)
        var myOwnedStandards = await _context.DdtStandards
            .Where(s => !s.IsDeleted && s.IsPublished && s.Owners.Any(o => o.UserId == currentUser.Id))
            .Include(s => s.Owners).ThenInclude(o => o.User)
            .Include(s => s.Categories).ThenInclude(c => c.Category)
            .OrderByDescending(s => s.UpdatedAt)
            .ToListAsync();

        var myContactStandards = await _context.DdtStandards
            .Where(s => !s.IsDeleted && s.IsPublished && s.Contacts.Any(c => c.UserId == currentUser.Id))
            .Include(s => s.Contacts).ThenInclude(c => c.User)
            .Include(s => s.Categories).ThenInclude(c => c.Category)
            .OrderByDescending(s => s.UpdatedAt)
            .ToListAsync();

        var myDdtStandards = myOwnedStandards
            .Union(myContactStandards)
            .GroupBy(s => s.Id)
            .Select(g => g.First())
            .ToList();

        _logger.LogInformation(
            "Dashboard VM built for {Email}: {Projects} projects, {Products} products, {Milestones} milestones, {Issues} issues, {Actions} assigned actions, {Standards} DDT standards",
            userEmail, myProjects.Count, myProducts.Count, allActiveMilestones.Count, allActiveIssues.Count, assignedActions.Count, myDdtStandards.Count);

        var yourTasks = _yourTasksBuilder.Build(new YourTasksBuildInput
        {
            MyProjects = myProjects,
            OverdueMilestones = overdueMilestones,
            MilestonesDueThisWeek = milestonesDueThisWeek,
            HighPriorityIssues = highPriorityIssues,
            AssignedActions = assignedActions,
            ShowRaidIssues = showRaidIssues,
            MonthlyReportingWindowOpen = monthlyReportingWindowOpen,
            MonthlyReportingRemainingCount = monthlyReportingRemainingCount,
            ReportingPeriodLabel = applicableMonthlyUpdatePeriodLabel,
            PerformanceReturnsDueCount = productsNeedingCommissionReporting.Count,
            Links = new YourTasksLinkOptions
            {
                MonthlyReportingHref = url.Action("Dashboard", "ModernWork") ?? "/modern/work/dashboard",
                PerformanceCommissionsHref = url.Action("Index", "ModernPerformance") ?? "/modern/performance/commissions",
                AllWorkHref = url.Action("AllWork", "ModernWork") ?? "/modern/work/all"
            },
            IdPrefix = "dashboard-task",
            Url = url
        });

        return new HomeDashboardViewModel
        {
            CurrentUser = currentUser,
            FirstName = firstName,
            SectionConfig = sectionConfig,
            Metrics = metrics,
            LeadershipMetrics = leadershipMetrics,
            PriorityTasks = priorityTasks,
            Reminders = reminders,
            QuickLinks = quickLinks,
            QuickLinkOptions = quickLinkOptions,
            BlockDefinitions = blockDefinitions,
            BlockInstances = blockInstances,
            MyProjects = myProjects,
            WatchedProjects = watchedProjects,
            OversightProjects = leadershipProjects,
            MyProducts = myProducts,
            MilestonesDueThisWeek = milestonesDueThisWeek,
            OverdueMilestones = overdueMilestones,
            HighPriorityIssues = highPriorityIssues,
            UnmonitoredRisks = unmonitoredRisks,
            AssignedActions = assignedActions,
            AtRiskProjects = atRiskProjects,
            ProjectsNeedingPathToGreen = projectsNeedingPathToGreen,
            RecentSuccesses = recentSuccesses,
            ProductsNeedingReturns = productsNeedingReturns,
            ProjectsNeedingMonthlyUpdates = projectsNeedingMonthlyUpdates,
            ApplicableMonthlyUpdateYear = applicableYear,
            ApplicableMonthlyUpdateMonth = applicableMonth,
            ApplicableMonthlyUpdatePeriodLabel = applicableMonthlyUpdatePeriodLabel,
            MonthlyReportingWindowOpen = monthlyReportingWindowOpen,
            MonthlyReportingRemainingCount = monthlyReportingRemainingCount,
            MonthlyUpdateStatusByProjectId = monthlyUpdateStatusByProjectId,
            ProductsNeedingCommissionReporting = productsNeedingCommissionReporting,
            YourTasks = yourTasks,
            LeadershipAssignments = leadershipAssignments,
            LeadershipBusinessAreas = leadershipBusinessAreas,
            HighestLeadershipRole = highestRole,
            EnterpriseMetrics = enterpriseMetrics,
            ActiveMissions = activeMissions,
            PriorityOutcomes = priorityOutcomes,
            EnterpriseAtRiskProjects = enterpriseAtRiskProjects,
            MyDdtStandards = myDdtStandards,
            MyOwnedDdtStandards = myOwnedStandards,
            MyContactDdtStandards = myContactStandards
        };
    }

    private static DashboardMetrics BuildLeadershipMetrics(IReadOnlyCollection<Project> oversightProjects)
    {
        if (!oversightProjects.Any())
        {
            return new DashboardMetrics();
        }

        bool IsOpen(string? status) =>
            !string.Equals(status, "closed", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(status, "resolved", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(status, "complete", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase);

        var oversightIssues = oversightProjects
            .SelectMany(p => p.Issues.Where(i => !i.IsDeleted && IsOpen(i.Status)))
            .ToList();

        var oversightRisks = oversightProjects
            .SelectMany(p => p.Risks.Where(r => !r.IsDeleted && !string.Equals(r.Status, "closed", StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var oversightActions = oversightProjects
            .SelectMany(p => p.Actions.Where(a => !a.IsDeleted && IsOpen(a.Status)))
            .ToList();

        var riskReviewOverdue = oversightRisks
            .Where(r => !r.NextReviewDate.HasValue || r.NextReviewDate.Value < DateTime.Today.AddDays(-30))
            .Count();

        var upcomingMilestones = oversightProjects
            .SelectMany(p => p.Milestones.Where(m => !m.IsDeleted && m.DueDate >= DateTime.Today && m.DueDate <= DateTime.Today.AddDays(14)))
            .Count();

        var atRiskProjects = oversightProjects
            .Count(p => (p.RagStatusLookup != null && string.Equals(p.RagStatusLookup.Name, "Red", StringComparison.OrdinalIgnoreCase)) ||
                        (p.RagStatusLookup != null && string.Equals(p.RagStatusLookup.Name, "Amber-Red", StringComparison.OrdinalIgnoreCase)));

        return new DashboardMetrics
        {
            TasksDue = oversightActions.Count,
            ProjectHealthIssues = atRiskProjects,
            UpcomingMilestones = upcomingMilestones,
            OpenIssues = oversightIssues.Count,
            UnreviewedRisks = riskReviewOverdue
        };
    }

    private static string ExtractFirstName(string? fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName))
        {
            return "User";
        }

        var nameParts = fullName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return nameParts.Length > 0 ? nameParts[0] : fullName;
    }

    private List<DashboardTaskItem> BuildPriorityTasks(
        IEnumerable<Project> projectsNeedingPathToGreen,
        IEnumerable<Milestone> expiredOpenMilestones,
        IEnumerable<(ProductDto Product, ReturnStatus Status, DateTime DueDate)> productsNeedingReturns,
        IEnumerable<Issue> highPriorityIssues,
        IEnumerable<Models.Action> assignedActions,
        IReadOnlyCollection<Project> allProjects,
        IUrlHelper url)
    {
        var tasks = new List<DashboardTaskItem>();

        foreach (var project in projectsNeedingPathToGreen.Take(5))
        {
            tasks.Add(new DashboardTaskItem
            {
                Category = "Path to green",
                Title = project.Title,
                Description = $"Document how this project will return to green status ({project.ProjectCode}).",
                StatusLabel = "Missing narrative",
                PriorityBadgeClass = "badge badge-danger",
                LinkLabel = "Open project",
                LinkUrl = url.Action("Details", "Project", new { id = project.Id, tab = "rag" }) ?? "#"
            });
        }

        foreach (var milestone in expiredOpenMilestones.Take(5))
        {
            var milestoneProject = allProjects.FirstOrDefault(p => p.Id == milestone.ProjectId);
            tasks.Add(new DashboardTaskItem
            {
                Category = "Milestone",
                Title = milestone.Name,
                Description = milestoneProject != null
                    ? $"{milestoneProject.Title} is { (DateTime.Today - milestone.DueDate).Days } day(s) over the agreed date."
                    : "Milestone is overdue and waiting for an update.",
                StatusLabel = "Overdue",
                PriorityBadgeClass = "badge badge-warning",
                DueDate = milestone.DueDate,
                LinkLabel = "Update milestone",
                LinkUrl = url.Action("Details", "Project", new { id = milestone.ProjectId, tab = "milestones" }) ?? "#"
            });
        }

        foreach (var (product, status, dueDate) in productsNeedingReturns.Take(5))
        {
            tasks.Add(new DashboardTaskItem
            {
                Category = "Operational return",
                Title = product.Title,
                Description = status == ReturnStatus.Late
                    ? "Return is late – submit as soon as possible."
                    : "Return due this month – submit performance metrics.",
                StatusLabel = status == ReturnStatus.Late ? "Late" : "Due",
                PriorityBadgeClass = status == ReturnStatus.Late ? "badge badge-danger" : "badge badge-warning",
                DueDate = dueDate,
                LinkLabel = "Open performance metrics",
                LinkUrl = url.Action("PerformanceMetrics", "ProductReporting", new { documentId = product.DocumentId ?? product.FipsId }) ?? "#"
            });
        }

        foreach (var issue in highPriorityIssues.Take(5))
        {
            tasks.Add(new DashboardTaskItem
            {
                Category = "Issue",
                Title = issue.Title,
                Description = "Review severity, owners and current mitigation plan.",
                StatusLabel = issue.Severity?.Equals("critical", StringComparison.OrdinalIgnoreCase) == true ? "Critical" : "High",
                PriorityBadgeClass = issue.Severity?.Equals("critical", StringComparison.OrdinalIgnoreCase) == true ? "badge badge-danger" : "badge badge-warning",
                DueDate = issue.TargetResolutionDate,
                LinkLabel = "View issue",
                LinkUrl = url.Action("Details", "Issue", new { id = issue.Id }) ?? "#"
            });
        }

        foreach (var action in assignedActions.Take(5))
        {
            tasks.Add(new DashboardTaskItem
            {
                Category = "Action",
                Title = action.Title,
                Description = action.Project != null
                    ? $"Assigned to you for {action.Project.Title}."
                    : "Action assigned to you.",
                StatusLabel = action.Status?.Replace("_", " "),
                PriorityBadgeClass = action.Blocked ? "badge badge-danger" : "badge badge-info",
                DueDate = action.DueDate,
                LinkLabel = "Review action",
                LinkUrl = url.Action("Details", "Action", new { id = action.Id }) ?? "#"
            });
        }

        return tasks.Take(15).ToList();
    }

    private List<DashboardReminder> BuildReminderCards(
        IReadOnlyCollection<Project> myProjects,
        IReadOnlyCollection<ProductDto> myProducts,
        IReadOnlyCollection<(ProductDto Product, ReturnStatus Status, DateTime DueDate)> productsNeedingReturns,
        IReadOnlyCollection<Risk> unmonitoredRisks,
        IUrlHelper url)
    {
        var reminders = new List<DashboardReminder>();

        if (productsNeedingReturns.Any())
        {
            var earliestDueDate = productsNeedingReturns.Min(r => r.DueDate);
            reminders.Add(new DashboardReminder
            {
                Title = "Submit monthly performance metrics",
                Description = $"{productsNeedingReturns.Count} product(s) still need metrics for {earliestDueDate:MMMM}.",
                Icon = "fas fa-chart-line",
                FrequencyBadge = "Monthly",
                Tone = "warning",
                LinkLabel = "Go to performance hub",
                LinkUrl = url.Action("Index", "Products") ?? "#"
            });
        }

        if (unmonitoredRisks.Any())
        {
            reminders.Add(new DashboardReminder
            {
                Title = "Review ageing risks",
                Description = $"{unmonitoredRisks.Count} risk(s) have not been reviewed in the past month.",
                Icon = "fas fa-exclamation-triangle",
                FrequencyBadge = "Fortnightly",
                Tone = "danger",
                LinkLabel = "Open risk register",
                LinkUrl = url.Action("Index", "Risk", new { viewScope = "assigned_to_me" }) ?? "#"
            });
        }

        if (myProducts.Any())
        {
            reminders.Add(new DashboardReminder
            {
                Title = "Service Health Check App for standards compliance",
                Description = "Confirm each product still meets the service standard and report gaps.",
                Icon = "fas fa-notes-medical",
                FrequencyBadge = "Quarterly",
                Tone = "info",
                LinkLabel = "Open Service Health Check App",
                LinkUrl = "https://educationgovuk.sharepoint.com/sites/ServiceHealthCheckHub/SitePages/Service-Health-Check-App.aspx"
            });
        }

        var discoveryCount = myProducts.Count(p => p.Phase?.Equals("Discovery", StringComparison.OrdinalIgnoreCase) == true);
        if (discoveryCount > 0)
        {
            reminders.Add(new DashboardReminder
            {
                Title = "Book discovery peer review",
                Description = $"{discoveryCount} service(s) are in discovery and should confirm an external peer review slot.",
                Icon = "fas fa-compass",
                FrequencyBadge = "Lifecycle",
                Tone = "info",
                LinkLabel = "Book review",
                LinkUrl = "#"
            });
        }

        var alphaCount = myProducts.Count(p => p.Phase?.Equals("Alpha", StringComparison.OrdinalIgnoreCase) == true);
        if (alphaCount > 0)
        {
            reminders.Add(new DashboardReminder
            {
                Title = "Schedule alpha service assessment",
                Description = $"{alphaCount} product(s) are preparing for alpha – secure a service assessment slot now.",
                Icon = "fas fa-flag-checkered",
                FrequencyBadge = "Lifecycle",
                Tone = "primary",
                LinkLabel = "Assessment guidance",
                LinkUrl = "#"
            });
        }

        return reminders;
    }

    private IReadOnlyCollection<DashboardQuickLinkOption> BuildQuickLinkOptions(UserPreference preference, bool hasProjects, bool hasProducts, IUrlHelper url)
    {
        var selectedCodes = ParseQuickLinkCodes(preference.QuickLaunchShortcuts);
        var defaultCodes = new List<string> { "project-updates", "log-risk", "metrics", "book-assessment" };

        if (!selectedCodes.Any())
        {
            selectedCodes = defaultCodes;
        }

        var options = new List<DashboardQuickLinkOption>
        {
            new()
            {
                Code = "project-updates",
                Title = "Update delivery story",
                Description = "Refresh RAG, blockers and path to green.",
                Icon = "fas fa-clipboard-check",
                Url = url.Action("Index", "Project") ?? "#",
                Disabled = !hasProjects
            },
            new()
            {
                Code = "log-risk",
                Title = "Log a risk",
                Description = "Capture new threats and mitigation plans.",
                Icon = "fas fa-exclamation-circle",
                Url = url.Action("Create", "Risk") ?? "#"
            },
            new()
            {
                Code = "metrics",
                Title = "Performance metrics",
                Description = "Enter or review product performance data.",
                Icon = "fas fa-chart-line",
                Url = url.Action("Index", "Products") ?? "#",
                Disabled = !hasProducts
            },
            new()
            {
                Code = "book-assessment",
                Title = "Book service assessment",
                Description = "Secure a DDaT assessment slot for your service.",
                Icon = "fas fa-clipboard",
                Url = "#"
            },
            new()
            {
                Code = "add-milestone",
                Title = "Add milestone update",
                Description = "Keep upcoming delivery checkpoints up to date.",
                Icon = "fas fa-stream",
                Url = url.Action("Index", "Milestone", new { tab = "assigned_to_me" }) ?? "#"
            },
            new()
            {
                Code = "report-issue",
                Title = "Raise issue",
                Description = "Escalate blockers that need wider attention.",
                Icon = "fas fa-bolt",
                Url = url.Action("Create", "Issue") ?? "#"
            },
            new()
            {
                Code = "demand",
                Title = "Submit demand request",
                Description = "Kick off a new demand intake case.",
                Icon = "fas fa-inbox",
                Url = url.Action("Requests", "DemandManagement") ?? "#"
            }
        };

        foreach (var option in options)
        {
            option.Selected = selectedCodes.Contains(option.Code) && !option.Disabled;
        }

        if (!options.Any(o => o.Selected))
        {
            foreach (var code in defaultCodes)
            {
                var fallback = options.FirstOrDefault(o => o.Code == code && !o.Disabled);
                if (fallback != null)
                {
                    fallback.Selected = true;
                }
            }
        }

        return options;
    }

    private static List<string> ParseQuickLinkCodes(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv))
        {
            return new List<string>();
        }

        return csv
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(code => code.Trim().ToLowerInvariant())
            .Distinct()
            .ToList();
    }
}
