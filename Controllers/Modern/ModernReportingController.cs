using Compass.Attributes;
using Compass.Controllers;
using Compass.Data;
using Compass.Models;
using Compass.Models.Fips;
using Compass.Models.Modern.Work;
using Compass.Services;
using Compass.Services.Aiss;
using Compass.Services.Modern;
using Compass.ViewModels;
using Compass.ViewModels.Modern;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace Compass.Controllers.Modern;

/// <summary>Modern reporting UI at <c>/modern/reporting/*</c> (same period logic as Central Ops monthly reporting).</summary>
[Authorize]
[Route("modern/reporting")]
public partial class ModernReportingController : Controller
{
    private readonly CompassDbContext _context;
    private readonly IMonthlyUpdateService _monthlyUpdateService;
    private readonly ModernMonthlyReportService _monthlyReportService;
    private readonly ModernRaidReviewProgressService _raidReviewProgressService;
    private readonly ModernRisksByTierReportService _risksByTierReportService;
    private readonly ModernRaidRegisterCoverageReportService _raidRegisterCoverageReportService;
    private readonly ModernRaidReportingService _raidReportingService;
    private readonly ModernRaidReportService _raidReportService;
    private readonly CommissionReportingAnalyticsService _commissionReportingAnalytics;
    private readonly IServiceAssessmentApiService _serviceAssessmentApi;
    private readonly IAissSummaryService _aissSummary;
    private readonly IModernWorkService _modernWork;
    private readonly IWorkScopedExcelExportService _workScopedExcelExport;
    private readonly IConfiguration _configuration;
    private readonly IPermissionService _permissionService;
    private readonly ILogger<ModernReportingController> _logger;

    public ModernReportingController(
        CompassDbContext context,
        IMonthlyUpdateService monthlyUpdateService,
        ModernMonthlyReportService monthlyReportService,
        ModernRaidReviewProgressService raidReviewProgressService,
        ModernRisksByTierReportService risksByTierReportService,
        ModernRaidRegisterCoverageReportService raidRegisterCoverageReportService,
        ModernRaidReportingService raidReportingService,
        ModernRaidReportService raidReportService,
        CommissionReportingAnalyticsService commissionReportingAnalytics,
        IServiceAssessmentApiService serviceAssessmentApi,
        IAissSummaryService aissSummary,
        IModernWorkService modernWork,
        IWorkScopedExcelExportService workScopedExcelExport,
        IConfiguration configuration,
        IPermissionService permissionService,
        ILogger<ModernReportingController> logger)
    {
        _context = context;
        _monthlyUpdateService = monthlyUpdateService;
        _monthlyReportService = monthlyReportService;
        _raidReviewProgressService = raidReviewProgressService;
        _risksByTierReportService = risksByTierReportService;
        _raidRegisterCoverageReportService = raidRegisterCoverageReportService;
        _raidReportingService = raidReportingService;
        _raidReportService = raidReportService;
        _commissionReportingAnalytics = commissionReportingAnalytics;
        _serviceAssessmentApi = serviceAssessmentApi;
        _aissSummary = aissSummary;
        _modernWork = modernWork;
        _workScopedExcelExport = workScopedExcelExport;
        _configuration = configuration;
        _permissionService = permissionService;
        _logger = logger;
    }

    private string? CurrentUserEmail =>
        User.Identity?.Name
        ?? User.FindFirstValue(ClaimTypes.Email)
        ?? User.FindFirstValue("preferred_username")
        ?? User.FindFirstValue("email");

    private async Task<bool> CanActionTierChangesAsync(CancellationToken cancellationToken)
    {
        var email = CurrentUserEmail?.Trim();
        if (string.IsNullOrWhiteSpace(email))
            return false;

        return await _permissionService.IsCentralOperationsAdminOrSuperAdminAsync(email);
    }

    private static ModernRisksByTierReportViewModel WithCanActionTierChanges(
        ModernRisksByTierReportViewModel model,
        bool canActionTierChanges) =>
        new()
        {
            FilterDirectorateId = model.FilterDirectorateId,
            IncludeClosed = model.IncludeClosed,
            Directorates = model.Directorates,
            OpenRiskCount = model.OpenRiskCount,
            ClosedRiskCount = model.ClosedRiskCount,
            TotalRiskCount = model.TotalRiskCount,
            UntieredRiskCount = model.UntieredRiskCount,
            StaleRiskCount = model.StaleRiskCount,
            TierColumns = model.TierColumns,
            DirectorateMatrix = model.DirectorateMatrix,
            TierGroups = model.TierGroups,
            MatrixTotalRow = model.MatrixTotalRow,
            DrillRisksByKey = model.DrillRisksByKey,
            LikelihoodScaleLabels = model.LikelihoodScaleLabels,
            ImpactScaleLabels = model.ImpactScaleLabels,
            CanActionTierChanges = canActionTierChanges
        };

    private void SetNav(string subNavItem)
    {
        ViewBag.MainNavSection = "reporting";
        ViewBag.SubNavItem = subNavItem;
    }

    /// <summary>Landing URL — redirects to Dashboard.</summary>
    [HttpGet("")]
    public IActionResult Index() => RedirectToAction(nameof(Dashboard));

    /// <summary>Reporting dashboard — entry points to report areas.</summary>
    [HttpGet("dashboard")]
    public IActionResult Dashboard()
    {
        SetNav("reporting-dashboard");
        return View("~/Views/Modern/Reporting/Dashboard.cshtml", new ModernReportingDashboardViewModel());
    }

    /// <summary>Thematic report — delivery work counts by tag and filtered work register.</summary>
    [HttpGet("thematic")]
    public async Task<IActionResult> ThematicReport(
        int? themeId,
        string? tab,
        int page = 1,
        CancellationToken cancellationToken = default)
    {
        SetNav("reporting-thematic");

        var summaryRows = await BuildThematicSummaryAsync(cancellationToken);
        var dashboard = await _monthlyReportService.BuildThematicReportDashboardAsync(cancellationToken);
        var selectedRow = themeId.HasValue && themeId.Value > 0
            ? summaryRows.FirstOrDefault(r => r.TagId == themeId.Value)
            : null;

        var activeTab = (tab?.ToLowerInvariant()) switch
        {
            "completed" => "completed",
            _ => "active"
        };

        var model = new ModernThematicReportViewModel
        {
            SummaryRows = summaryRows,
            DashboardRows = dashboard.Rows,
            ScopeProjectItems = dashboard.ScopeProjectItems,
            ReportYear = dashboard.ReportYear,
            ReportMonth = dashboard.ReportMonth,
            MonthName = dashboard.MonthName,
            SelectedThemeId = selectedRow?.TagId,
            SelectedThemeName = selectedRow?.Name,
            SelectedThemeDescription = selectedRow?.Description,
            ActiveTab = activeTab
        };

        return View("~/Views/Modern/Reporting/ThematicReport.cshtml", model);
    }

    private async Task<List<ThematicReportTagSummaryRow>> BuildThematicSummaryAsync(CancellationToken cancellationToken)
    {
        var tags = await _context.WorkItemTagLookups.AsNoTracking()
            .Where(t => t.IsActive)
            .OrderBy(t => t.SortOrder)
            .ThenBy(t => t.Name)
            .Select(t => new { t.Id, t.Name, t.Description })
            .ToListAsync(cancellationToken);

        var statusByTag = await _context.ProjectWorkItemTags.AsNoTracking()
            .Where(l => l.WorkItemTagLookup != null && l.WorkItemTagLookup.IsActive)
            .Where(l => !l.Project.IsDeleted)
            .Select(l => new { l.WorkItemTagLookupId, l.Project.Status })
            .ToListAsync(cancellationToken);

        var countsByTag = statusByTag
            .GroupBy(x => x.WorkItemTagLookupId)
            .ToDictionary(
                g => g.Key,
                g => new
                {
                    Active = g.Count(x => x.Status == "Active" || x.Status == "Paused"),
                    Completed = g.Count(x => x.Status == "Completed")
                });

        return tags.Select(t =>
        {
            countsByTag.TryGetValue(t.Id, out var counts);
            return new ThematicReportTagSummaryRow
            {
                TagId = t.Id,
                Name = t.Name,
                Description = t.Description,
                ActiveCount = counts?.Active ?? 0,
                CompletedCount = counts?.Completed ?? 0
            };
        }).ToList();
    }

    /// <summary>Priorities report — monthly metrics by mission pillar, priority outcome, or delivery priority.</summary>
    [HttpGet("priorities")]
    public async Task<IActionResult> Priorities(
        string? dimension,
        int? year,
        int? month,
        int? groupId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var model = await _monthlyReportService.BuildPrioritiesReportAsync(dimension, year, month, groupId, cancellationToken);
            SetNav("reporting-priorities");
            return View("~/Views/Modern/Reporting/PrioritiesReport.cshtml", model);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading priorities report");
            TempData["ErrorMessage"] = "An error occurred while loading the priorities report. Please try again.";
            SetNav("reporting-priorities");
            return View("~/Views/Modern/Reporting/PrioritiesReport.cshtml", new ModernPrioritiesReportViewModel
            {
                Report = new ModernMonthlyReportDashboardViewModel
                {
                    MinReportYear = 2026,
                    MaxReportYear = Math.Max(2026, DateTime.UtcNow.Year)
                }
            });
        }
    }

    /// <summary>Resourcing report — FTE/MSP totals and configurable resourcing bands.</summary>
    [HttpGet("resourcing")]
    public async Task<IActionResult> Resourcing(
        int? year,
        int? month,
        int? businessAreaId,
        int? directorateId,
        string? dimension,
        int? groupId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var model = await _monthlyReportService.BuildResourcingReportAsync(
                year,
                month,
                businessAreaId,
                directorateId,
                dimension,
                groupId,
                cancellationToken);
            SetNav("reporting-resourcing");
            return View("~/Views/Modern/Reporting/Resourcing.cshtml", model);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading resourcing report");
            TempData["ErrorMessage"] = "An error occurred while loading the resourcing report. Please try again.";
            SetNav("reporting-resourcing");
            return View("~/Views/Modern/Reporting/Resourcing.cshtml", new ModernResourcingReportViewModel
            {
                MinReportYear = 2026,
                MaxReportYear = Math.Max(2026, DateTime.UtcNow.Year),
                MonthName = DateTime.UtcNow.ToString("MMMM yyyy")
            });
        }
    }

    /// <summary>Priority resourcing report — Perm/MSC FTE from monthly returns by delivery priority and directorate.</summary>
    [HttpGet("priority-resourcing")]
    public async Task<IActionResult> PriorityResourcing(
        int? year,
        int? month,
        int? businessAreaId,
        int? directorateId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var model = await _monthlyReportService.BuildPriorityResourcingReportAsync(
                year,
                month,
                businessAreaId,
                directorateId,
                cancellationToken);
            SetNav("reporting-resourcing");
            return View("~/Views/Modern/Reporting/PriorityResourcing.cshtml", model);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading priority resourcing report");
            TempData["ErrorMessage"] = "An error occurred while loading the priority resourcing report. Please try again.";
            SetNav("reporting-resourcing");
            return View("~/Views/Modern/Reporting/PriorityResourcing.cshtml", new ModernPriorityResourcingReportViewModel
            {
                MinReportYear = 2026,
                MaxReportYear = Math.Max(2026, DateTime.UtcNow.Year),
                MonthName = DateTime.UtcNow.ToString("MMMM yyyy")
            });
        }
    }

    /// <summary>RAID register coverage — work items and services not in any register scope.</summary>
    [HttpGet("raid-registers")]
    public async Task<IActionResult> RaidRegisters(CancellationToken cancellationToken = default)
    {
        try
        {
            var model = await _raidRegisterCoverageReportService.BuildAsync(cancellationToken);
            SetNav("reporting-raid-registers");
            return View("~/Views/Modern/Reporting/RaidRegisters.cshtml", model);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading RAID registers coverage report");
            TempData["ErrorMessage"] = "An error occurred while loading the RAID registers report. Please try again.";
            SetNav("reporting-raid-registers");
            return View("~/Views/Modern/Reporting/RaidRegisters.cshtml", new ModernRaidRegisterCoverageReportViewModel());
        }
    }

    /// <summary>Service register report — status, completion, and data-quality summaries for CMDB products.</summary>
    [HttpGet("service-register")]
    public async Task<IActionResult> ServiceRegister(CancellationToken cancellationToken = default)
    {
        try
        {
            var model = await _monthlyReportService.BuildServiceRegisterReportAsync(cancellationToken);
            SetNav("reporting-service-register");
            return View("~/Views/Modern/Reporting/ServiceRegister.cshtml", model);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading service register report");
            TempData["ErrorMessage"] = "An error occurred while loading the service register report. Please try again.";
            SetNav("reporting-service-register");
            return View("~/Views/Modern/Reporting/ServiceRegister.cshtml", new ModernServiceRegisterReportViewModel());
        }
    }

    /// <summary>Monthly submission progress — chart and league tables for monthly return completion.</summary>
    [HttpGet("monthly-submission-progress")]
    public async Task<IActionResult> MonthlySubmissionProgress(int? year, int? month, int? businessAreaId, int? directorateId)
    {
        try
        {
            var model = await _monthlyReportService.BuildSubmissionProgressAsync(year, month, businessAreaId, directorateId);
            SetNav("reporting-monthly-submission");
            return View("~/Views/Modern/Reporting/MonthlySubmissionProgress.cshtml", model);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading monthly submission progress report");
            TempData["ErrorMessage"] = "An error occurred while loading monthly submission progress. Please try again.";
            SetNav("reporting-monthly-submission");
            return View("~/Views/Modern/Reporting/MonthlySubmissionProgress.cshtml", new ModernMonthlySubmissionProgressViewModel
            {
                MinReportYear = 2026,
                MaxReportYear = Math.Max(2026, DateTime.UtcNow.Year)
            });
        }
    }

    /// <summary>Excel export for monthly submission progress (work items with historical period submission status).</summary>
    [HttpGet("monthly-submission-progress/export")]
    public async Task<IActionResult> ExportMonthlySubmissionProgress(
        int? year,
        int? month,
        int? businessAreaId,
        int? directorateId,
        CancellationToken cancellationToken = default)
    {
        var model = await _monthlyReportService.BuildSubmissionProgressAsync(
            year, month, businessAreaId, directorateId, cancellationToken);
        var bytes = MonthlySubmissionProgressExcelExport.BuildWorkbook(model);
        var periodLabel = model.MonthName.Replace(" ", "-", StringComparison.Ordinal);
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"monthly-submission-progress-{periodLabel}-{DateTime.UtcNow:yyyyMMdd-HHmm}.xlsx");
    }

    /// <summary>Risks by tier — risk-team oversight grouped by governance tier with directorate matrix.</summary>
    [HttpGet("risks-by-tier")]
    [ServiceFilter(typeof(Compass.Filters.RaidFeatureGateFilter))]
    public async Task<IActionResult> RisksByTier(
        int? directorateId,
        bool includeClosed = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var model = await _risksByTierReportService.BuildAsync(directorateId, includeClosed, cancellationToken);
            var canActionTierChanges = await CanActionTierChangesAsync(cancellationToken);
            SetNav("reporting-risks-by-tier");
            return View("~/Views/Modern/Reporting/RisksByTier.cshtml", WithCanActionTierChanges(model, canActionTierChanges));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading risks by tier report");
            TempData["ErrorMessage"] = "An error occurred while loading the risks by tier report. Please try again.";
            SetNav("reporting-risks-by-tier");
            return View("~/Views/Modern/Reporting/RisksByTier.cshtml", new ModernRisksByTierReportViewModel());
        }
    }

    /// <summary>RAID monthly review progress — chart and league tables for review completion.</summary>
    [HttpGet("raid-review-progress")]
    [ServiceFilter(typeof(Compass.Filters.RaidFeatureGateFilter))]
    public async Task<IActionResult> RaidReviewProgress(int? year, int? month, int? businessAreaId, int? directorateId)
    {
        try
        {
            var model = await _raidReviewProgressService.BuildAsync(year, month, businessAreaId, directorateId);
            SetNav("reporting-raid-review");
            return View("~/Views/Modern/Reporting/RaidReviewProgress.cshtml", model);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading RAID review progress report");
            TempData["ErrorMessage"] = "An error occurred while loading the RAID review report. Please try again.";
            SetNav("reporting-raid-review");
            return View("~/Views/Modern/Reporting/RaidReviewProgress.cshtml", new ModernRaidReviewProgressViewModel
            {
                MinReportYear = 2026,
                MaxReportYear = Math.Max(2026, DateTime.UtcNow.Year)
            });
        }
    }

    /// <summary>Monthly reporting dashboard — portfolio health, milestones, RAG/priority, and business area summary.</summary>
    [HttpGet("monthly-update")]
    public async Task<IActionResult> MonthlyUpdate(int? year, int? month, int? businessAreaId, int? directorateId)
    {
        try
        {
            var model = await _monthlyReportService.BuildDashboardAsync(year, month, businessAreaId, directorateId);
            SetNav("reporting-monthly");
            return View("~/Views/Modern/Reporting/MonthlyUpdate.cshtml", model);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading modern monthly report dashboard");
            TempData["ErrorMessage"] = "An error occurred while loading the monthly report. Please try again.";
            SetNav("reporting-monthly");
            return View("~/Views/Modern/Reporting/MonthlyUpdate.cshtml", new ModernMonthlyReportDashboardViewModel
            {
                MinReportYear = 2026,
                MaxReportYear = Math.Max(2026, DateTime.UtcNow.Year)
            });
        }
    }

    /// <summary>Full monthly update overview with stats (existing logic).</summary>
    [HttpGet("monthly-update-overview")]
    public async Task<IActionResult> MonthlyUpdateOverview(int? year, int? month, string? status)
    {
        try
        {
            var allProjects = await _context.Projects
                .AsNoTracking()
                .Include(p => p.MonthlyUpdates)
                .Include(p => p.PrimaryContactUser)
                .Include(p => p.DeliveryPriority)
                .Include(p => p.BusinessAreaLookup)
                .Include(p => p.ServiceOwners)
                    .ThenInclude(so => so.User)
                .Where(p => !p.IsDeleted && p.Status != "Cancelled" && p.Status != "Completed")
                .ToListAsync();

            var currentDate = DateTime.UtcNow;
            var currentYear = currentDate.Year;
            var currentMonth = currentDate.Month;

            var currentPeriodDueDate = _monthlyUpdateService.GetMonthlyUpdateDueDate(currentYear, currentMonth);
            var daysUntilCurrentPeriodDueDate = (currentPeriodDueDate - currentDate).Days;

            var reportYear = daysUntilCurrentPeriodDueDate <= 10 ? currentYear : (currentMonth == 1 ? currentYear - 1 : currentYear);
            var reportMonth = daysUntilCurrentPeriodDueDate <= 10 ? currentMonth : (currentMonth == 1 ? 12 : currentMonth - 1);

            var filterYear = year ?? reportYear;
            var filterMonth = month ?? reportMonth;

            var dueDate = _monthlyUpdateService.GetMonthlyUpdateDueDate(filterYear, filterMonth);

            var periodStats = CalculateMonthlyUpdateStats(allProjects, filterYear, filterMonth, _monthlyUpdateService);

            var statusGroups = new Dictionary<string, List<Project>>
            {
                { "Submitted", new List<Project>() },
                { "Late", new List<Project>() },
                { "In Progress", new List<Project>() },
                { "Not Started", new List<Project>() }
            };

            foreach (var project in allProjects)
            {
                var update = project.MonthlyUpdates?.FirstOrDefault(u => u.Year == filterYear && u.Month == filterMonth);
                string projectStatus;

                if (update != null && update.SubmittedAt.HasValue)
                    projectStatus = "Submitted";
                else if (currentDate > dueDate)
                    projectStatus = "Late";
                else if (update != null && !update.SubmittedAt.HasValue)
                    projectStatus = "In Progress";
                else
                    projectStatus = "Not Started";

                if (statusGroups.ContainsKey(projectStatus))
                    statusGroups[projectStatus].Add(project);
            }

            foreach (var group in statusGroups.Values)
                group.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase));

            var businessAreaGroups = new Dictionary<string, BusinessAreaMonthlyData>();

            var businessAreas = allProjects
                .Where(p => p.BusinessAreaLookup != null)
                .Select(p => p.BusinessAreaLookup!)
                .GroupBy(ba => ba.Id)
                .Select(g => g.First())
                .ToList();

            var notAssignedProjects = allProjects.Where(p => p.BusinessAreaLookup == null).ToList();
            if (notAssignedProjects.Any())
            {
                businessAreaGroups.Add("Not assigned", new BusinessAreaMonthlyData
                {
                    BusinessAreaName = "Not assigned",
                    TotalProjects = notAssignedProjects.Count,
                    SubmittedCount = notAssignedProjects.Count(p =>
                    {
                        var update = p.MonthlyUpdates?.FirstOrDefault(u => u.Year == filterYear && u.Month == filterMonth);
                        return update != null && update.SubmittedAt.HasValue;
                    }),
                    Projects = notAssignedProjects.OrderBy(p => p.Title).ToList()
                });
            }

            foreach (var businessArea in businessAreas)
            {
                var areaProjects = allProjects.Where(p => p.BusinessAreaLookup?.Id == businessArea.Id).ToList();
                var submittedProjects = areaProjects.Where(p =>
                {
                    var update = p.MonthlyUpdates?.FirstOrDefault(u => u.Year == filterYear && u.Month == filterMonth);
                    return update != null && update.SubmittedAt.HasValue;
                }).ToList();

                businessAreaGroups.Add(businessArea.Name, new BusinessAreaMonthlyData
                {
                    BusinessAreaName = businessArea.Name,
                    TotalProjects = areaProjects.Count,
                    SubmittedCount = submittedProjects.Count,
                    Projects = areaProjects.OrderBy(p => p.Title).ToList()
                });
            }

            ViewBag.PeriodStats = periodStats;
            ViewBag.StatusGroups = statusGroups;
            ViewBag.BusinessAreaGroups = businessAreaGroups;
            ViewBag.ReportYear = filterYear;
            ViewBag.ReportMonth = filterMonth;
            ViewBag.MonthName = new DateTime(filterYear, filterMonth, 1).ToString("MMMM yyyy");
            ViewBag.DueDate = dueDate;

            var previousMonthDate = new DateTime(filterYear, filterMonth, 1).AddMonths(-1);
            ViewBag.PreviousYear = previousMonthDate.Year;
            ViewBag.PreviousMonth = previousMonthDate.Month;
            ViewBag.HasPreviousMonth = true;

            var nextMonthDate = new DateTime(filterYear, filterMonth, 1).AddMonths(1);
            var nextMonthYear = nextMonthDate.Year;
            var nextMonthMonth = nextMonthDate.Month;
            var nextMonthAllowed = nextMonthYear < reportYear ||
                                  (nextMonthYear == reportYear && nextMonthMonth <= reportMonth);
            ViewBag.NextYear = nextMonthYear;
            ViewBag.NextMonth = nextMonthMonth;
            ViewBag.HasNextMonth = nextMonthAllowed;

            SetNav("reporting-monthly");

            return View("~/Views/Modern/Reporting/MonthlyUpdateOverview.cshtml");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading modern monthly update overview");
            TempData["ErrorMessage"] = "An error occurred while loading reporting compliance. Please try again.";
            SetNav("reporting-monthly");
            return View("~/Views/Modern/Reporting/MonthlyUpdateOverview.cshtml");
        }
    }

    /// <summary>Performance commissions — completed rounds, return rates, metric completion, and business area breakdown (catalogue scope).</summary>
    [HttpGet("performance")]
    public async Task<IActionResult> Performance(int? commissionId, int? businessAreaId, int? directorateId, CancellationToken cancellationToken = default)
    {
        try
        {
            var model = await _commissionReportingAnalytics.BuildPerformancePageAsync(commissionId, businessAreaId, directorateId, cancellationToken);
            SetNav("reporting-performance");
            return View("~/Views/Modern/Reporting/Performance.cshtml", model);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading performance commission reporting");
            TempData["ErrorMessage"] = "Could not load performance commission reporting. Please try again.";
            SetNav("reporting-performance");
            return View("~/Views/Modern/Reporting/Performance.cshtml", new ModernReportingPerformancePageViewModel());
        }
    }

    /// <summary>Live performance submission progress — same in-scope products as commission workspace All products.</summary>
    [HttpGet("performance-submission-progress")]
    public async Task<IActionResult> PerformanceSubmissionProgress(int? commissionId, CancellationToken cancellationToken = default)
    {
        try
        {
            var model = await _commissionReportingAnalytics.BuildPerformanceSubmissionProgressPageAsync(commissionId, cancellationToken);
            SetNav("reporting-performance-submission-progress");
            return View("~/Views/Modern/Reporting/PerformanceSubmissionProgress.cshtml", model);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading performance submission progress report");
            TempData["ErrorMessage"] = "Could not load performance submission progress. Please try again.";
            SetNav("reporting-performance-submission-progress");
            return View("~/Views/Modern/Reporting/PerformanceSubmissionProgress.cshtml", new ModernReportingPerformanceSubmissionProgressViewModel());
        }
    }

    /// <summary>Published service assessments from SAS: portfolio summary, published list, and actions by standard.</summary>
    [HttpGet("assessments")]
    public async Task<IActionResult> Assessments(CancellationToken cancellationToken = default)
    {
        try
        {
            var summaryTask = _serviceAssessmentApi.GetPublishedSummaryAsync(cancellationToken);
            var byStandardTask = _serviceAssessmentApi.GetPublishedActionsByStandardAsync(cancellationToken);
            var allWithActionsTask = _serviceAssessmentApi.GetActionsByStandardAsync();
            var assessorsTask = _serviceAssessmentApi.GetAssessorsSummaryAsync(cancellationToken);
            await Task.WhenAll(summaryTask, byStandardTask, allWithActionsTask, assessorsTask);

            var summary = await summaryTask;
            var byStandard = await byStandardTask;
            var allWithActions = await allWithActionsTask;
            var assessorsSummary = await assessorsTask;

            var reportBase = (_configuration["FipsSync:Sas:ReportBaseUrl"] ?? "https://service-assessments.education.gov.uk/reports/report")
                .TrimEnd('/');

            var vm = new ModernServiceAssessmentsReportViewModel
            {
                SummaryLoadFailed = summary is null,
                SasReportBaseUrl = reportBase
            };

            if (summary?.Summaries != null)
            {
                var s = summary.Summaries;
                vm.TotalAssessments = s.TotalAssessments;
                vm.ByType = SortCountDictionary(s.ByType);
                vm.ByPhase = OrderPhases(s.ByPhase);
                vm.ByYear = SortCountDictionary(s.ByYear, yearSort: true, yearAscending: true);
            }

            if (summary?.Assessments is { Count: > 0 } list)
            {
                vm.ByOutcome = OrderRagOutcomeRows(CountOutcomesForServiceAssessmentsOnly(list));

                var rows = list
                    .Select(a => new SasAssessmentListItem
                    {
                        AssessmentId = a.AssessmentID,
                        Name = a.Name,
                        Type = a.Type,
                        Phase = a.Phase,
                        Outcome = a.Outcome,
                        Portfolio = a.Portfolio,
                        AssessmentDate = a.AssessmentDateTime
                    })
                    .OrderByDescending(x => x.AssessmentDate)
                    .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                vm.AssessmentRows = rows;
                vm.AssessmentsGroupedByType = rows
                    .GroupBy(x => string.IsNullOrWhiteSpace(x.Type) ? "Other" : x.Type!.Trim(), StringComparer.OrdinalIgnoreCase)
                    .OrderBy(g => AssessmentTypeSortKey(g.Key))
                    .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(g => new SasAssessmentsTypeGroup
                    {
                        TypeLabel = g.Key,
                        Items = g
                            .OrderByDescending(x => x.AssessmentDate)
                            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                            .ToList()
                    })
                    .ToList();
            }

            {
                var (stdRows, outcomeDetail) = ServiceAssessmentStandardActionOutcomeBuilder.Build(
                    byStandard,
                    allWithActions,
                    summary?.Assessments);
                vm.StandardActionRows = stdRows;
                vm.ServiceStandardOutcomeBreakdownAvailable = outcomeDetail;
            }

            vm.ActionsByStandardLoadFailed = byStandard is null && allWithActions is null;

            vm.StandardsAnalysis = ServiceAssessmentStandardsAnalyticsBuilder.Build(
                allWithActions,
                summary?.Assessments,
                vm.StandardActionRows,
                vm.ServiceStandardOutcomeBreakdownAvailable);

            vm.AssessorAnalysis = ServiceAssessmentAssessorAnalyticsBuilder.Build(assessorsSummary);

            if (vm.SummaryLoadFailed || (byStandard is null && allWithActions is null))
            {
                TempData["ErrorMessage"] =
                    "Part of the service assessment data could not be loaded. Check the SAS connection or try again.";
            }

            SetNav("reporting-assessments");
            return View("~/Views/Modern/Reporting/Assessments.cshtml", vm);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading service assessments report");
            TempData["ErrorMessage"] = "Could not load service assessments. Please try again.";
            SetNav("reporting-assessments");
            return View("~/Views/Modern/Reporting/Assessments.cshtml", new ModernServiceAssessmentsReportViewModel
            {
                SummaryLoadFailed = true,
                ActionsByStandardLoadFailed = true
            });
        }
    }

    /// <summary>AISS portfolio summary and issue trend charts (accessibility report).</summary>
    [HttpGet("accessibility")]
    [RequireCentralOpsAdmin]
    public async Task<IActionResult> Accessibility(CancellationToken cancellationToken = default)
    {
        SetNav("reporting-accessibility");
        var summaryTask = _aissSummary.GetSummaryAsync(cancellationToken);
        var trendsTask = _aissSummary.GetCriterionTrendsAsync(12, cancellationToken);
        await Task.WhenAll(summaryTask, trendsTask);
        var (summary, error) = await summaryTask;
        var (trends, trendsError) = await trendsTask;
        var aissCfg = _configuration.GetSection("FipsSync:Aiss").Get<AissConfiguration>() ?? new AissConfiguration();
        var vm = new ModernOperationsAccessibilityViewModel
        {
            Summary = summary,
            Trends = trends,
            ErrorMessage = error,
            TrendsError = trendsError,
            AissWebBaseUrl = aissCfg.ResolveWebBaseUrl()
        };
        return View("~/Views/Modern/Reporting/Accessibility.cshtml", vm);
    }

    private static readonly string[] DeliveryPhaseOrder =
    [
        "Discovery", "Alpha", "Private Beta", "Public Beta", "Live"
    ];

    /// <summary>Green → Amber → Red, then other labels alphabetically — consistent legend and table order.</summary>
    private static List<KeyValuePair<string, int>> OrderRagOutcomeRows(
        List<KeyValuePair<string, int>> rows)
    {
        if (rows.Count == 0)
        {
            return rows;
        }

        static int OutcomeSortKey(string key)
        {
            var t = (key ?? string.Empty).Trim();
            if (t.Equals("Green", StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            if (t.Equals("Amber", StringComparison.OrdinalIgnoreCase)
                || t.Contains("Amber", StringComparison.OrdinalIgnoreCase))
            {
                return 1;
            }

            if (t.Equals("Red", StringComparison.OrdinalIgnoreCase))
            {
                return 2;
            }

            if (t.Equals("—", StringComparison.Ordinal) || t.Equals("–", StringComparison.Ordinal))
            {
                return 50;
            }

            return 10;
        }

        return rows
            .OrderBy(x => OutcomeSortKey(x.Key))
            .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int AssessmentTypeSortKey(string typeLabel)
    {
        if (string.Equals(typeLabel.Trim(), "Service assessment", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        return 1;
    }

    private static List<KeyValuePair<string, int>> CountOutcomesForServiceAssessmentsOnly(
        IEnumerable<SasPublishedAssessmentRow> list)
    {
        var d = list
            .Where(r => string.Equals(r.Type?.Trim(), "Service assessment", StringComparison.OrdinalIgnoreCase))
            .GroupBy(
                r => string.IsNullOrWhiteSpace(r.Outcome) ? "—" : r.Outcome!.Trim(),
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        return SortCountDictionary(d, yearSort: false);
    }

    /// <summary>Phases: Discovery, Alpha, Private Beta, Public Beta, Live, then any other known labels.</summary>
    private static List<KeyValuePair<string, int>> OrderPhases(Dictionary<string, int>? d)
    {
        if (d is null or { Count: 0 })
        {
            return new List<KeyValuePair<string, int>>();
        }

        var result = new List<KeyValuePair<string, int>>();
        var matchedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var canonical in DeliveryPhaseOrder)
        {
            var p = d.FirstOrDefault(
                x => !matchedKeys.Contains(x.Key) &&
                     string.Equals(x.Key.Trim(), canonical, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(p.Key))
            {
                result.Add(new KeyValuePair<string, int>(canonical, p.Value));
                matchedKeys.Add(p.Key);
            }
        }

        foreach (var p in d.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (matchedKeys.Contains(p.Key)) continue;
            result.Add(p);
            matchedKeys.Add(p.Key);
        }

        return result;
    }

    private static List<KeyValuePair<string, int>> SortCountDictionary(
        Dictionary<string, int>? d,
        bool yearSort = false,
        bool yearAscending = true)
    {
        if (d is null or { Count: 0 })
        {
            return new List<KeyValuePair<string, int>>();
        }

        if (yearSort)
        {
            return yearAscending
                ? d.OrderBy(p => int.TryParse(p.Key, out var y) ? y : 9999).ToList()
                : d.OrderByDescending(p => int.TryParse(p.Key, out var y) ? y : 0).ToList();
        }

        return d
            .OrderByDescending(p => p.Value)
            .ThenBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Canonical detail URL redirects to the unified performance report with <c>commissionId</c>.</summary>
    [HttpGet("performance/{commissionId:int}")]
    public IActionResult PerformanceCommission(int commissionId)
    {
        return RedirectToAction(nameof(Performance), new { commissionId });
    }

    /// <summary>Legacy URL — consolidated into <see cref="Raid"/>.</summary>
    [HttpGet("risk")]
    [ServiceFilter(typeof(Compass.Filters.RaidFeatureGateFilter))]
    public IActionResult Risk() => RedirectToAction(nameof(Raid), new { tab = "risks" });

    /// <summary>Design Decision Records (DDR) reporting (§14). Gated by the DDR feature flag.</summary>
    [HttpGet("design-decision-records")]
    [ServiceFilter(typeof(Compass.Filters.DdrFeatureGateFilter))]
    public async Task<IActionResult> DesignDecisionRecords(CancellationToken cancellationToken = default)
    {
        SetNav("reporting-ddr");

        try
        {
            var monthStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1);
            var today = DateTime.UtcNow.Date;
            var openStatuses = new[] { "Proposed", "In use", "Approved", "Under review" };

            var totalDdrs = await _context.DesignDecisionRecords.AsNoTracking()
                .CountAsync(r => r.DeletedAt == null, cancellationToken);
            var thisMonth = await _context.DesignDecisionRecords.AsNoTracking()
                .CountAsync(r => r.DeletedAt == null && r.CreatedAt >= monthStart, cancellationToken);

            var byStatus = await _context.DesignDecisionRecords.AsNoTracking()
                .Where(r => r.DeletedAt == null)
                .GroupBy(r => r.Status)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToListAsync(cancellationToken);
            var byCategory = await _context.DesignDecisionRecords.AsNoTracking()
                .Where(r => r.DeletedAt == null)
                .GroupBy(r => r.Category)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToListAsync(cancellationToken);

            var deviationsByType = await _context.DesignDecisionRecords.AsNoTracking()
                .Where(r => r.DeletedAt == null && r.DeviationFlag && r.DeviationType != null)
                .GroupBy(r => r.DeviationType)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToListAsync(cancellationToken);
            var deviationsCount = await _context.DesignDecisionRecords.AsNoTracking()
                .CountAsync(r => r.DeletedAt == null && r.DeviationFlag, cancellationToken);
            var overdueCount = await _context.DesignDecisionRecords.AsNoTracking()
                .CountAsync(r => r.DeletedAt == null
                    && r.ReviewDate != null && r.ReviewDate < today
                    && openStatuses.Contains(r.Status), cancellationToken);
            var retroCount = await _context.DesignDecisionRecords.AsNoTracking()
                .CountAsync(r => r.DeletedAt == null && r.RetrospectiveRecord, cancellationToken);

            var insightCounts = await _context.DdrInsightClassifications.AsNoTracking()
                .GroupBy(c => c.Classification)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToListAsync(cancellationToken);

            var productsWithDdrs = await _context.DdrProductLinks.AsNoTracking()
                .Select(l => l.FipsProductId).Distinct().CountAsync(cancellationToken);
            var totalProducts = await _context.CMDBProducts.AsNoTracking()
                .CountAsync(p => p.Status == Compass.Models.Fips.CMDBProductStatus.Active, cancellationToken);

            var model = new Compass.ViewModels.Modern.Ddr.DdrReportingViewModel
            {
                TotalDdrs = totalDdrs,
                DdrsThisMonth = thisMonth,
                ByStatus = byStatus.ToDictionary(x => x.Key ?? "(unknown)", x => x.Count),
                ByCategory = byCategory.ToDictionary(x => x.Key ?? "(unknown)", x => x.Count),
                DeviationsByType = deviationsByType.ToDictionary(x => x.Key ?? "(unknown)", x => x.Count),
                DeviationsCount = deviationsCount,
                OverdueReviewCount = overdueCount,
                RetrospectiveCount = retroCount,
                InsightCounts = insightCounts.ToDictionary(x => x.Key ?? "(unknown)", x => x.Count),
                ProductsWithDdrs = productsWithDdrs,
                TotalProducts = totalProducts,
            };
            return View("~/Views/Modern/Reporting/DesignDecisionRecords.cshtml", model);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading DDR reporting dashboard");
            TempData["ErrorMessage"] = "Could not load Design Decision Records reporting. Please try again.";
            return View("~/Views/Modern/Reporting/DesignDecisionRecords.cshtml",
                new Compass.ViewModels.Modern.Ddr.DdrReportingViewModel());
        }
    }

    /// <summary>RAID analytics — risk/issue heatmaps, trends, and portfolio signals.</summary>
    [HttpGet("raid")]
    [ServiceFilter(typeof(Compass.Filters.RaidFeatureGateFilter))]
    public async Task<IActionResult> Raid(
        string? tab,
        int? businessAreaId,
        int? directorateId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var model = await _raidReportService.BuildAsync(
                tab,
                businessAreaId,
                directorateId,
                cancellationToken);
            SetNav("reporting-raid");
            return View("~/Views/Modern/Reporting/Raid.cshtml", model);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading RAID report");
            TempData["ErrorMessage"] = "Could not load the RAID report. Please try again.";
            SetNav("reporting-raid");
            return View("~/Views/Modern/Reporting/Raid.cshtml", new ModernRaidReportViewModel());
        }
    }

    private static MonthlyUpdateStats CalculateMonthlyUpdateStats(
        List<Project> projects,
        int year,
        int month,
        IMonthlyUpdateService monthlyUpdateService)
    {
        var totalProjects = projects.Count;
        var dueDate = monthlyUpdateService.GetMonthlyUpdateDueDate(year, month);
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
}
