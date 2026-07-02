using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using Compass.Data;
using Compass.Helpers;
using Compass.Models;
using Compass.Models.Modern.Work;
using Compass.Services;
using Compass.Services.Modern;
using Compass.Services.Raid;
using Compass.ViewModels.Modern;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Compass.Controllers.Modern;

/// <summary>Modern work UI at <c>/modern/work/*</c>, backed by Compass <see cref="Project"/> data.</summary>
[Authorize]
[Route("modern/work")]
public partial class ModernWorkController : Controller
{
    private const int WorkGroupingRegisterPageSize = 25;
    private const string DefaultBusinessAreaCookieName = "compass_work_default_ba";
    private const string DefaultDirectorateCookieName = "compass_work_default_dir";
    private const string CentralOperationsAdminGroupName = "Central Operations Admin";

    private static readonly HashSet<string> WorkRegisterStatusFilterValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "Active", "Paused", "Completed", "Cancelled"
    };

    private static bool BusinessAreaWorkItemMatchesRag(WorkItem w, int ragId)
    {
        if (w.RagStatusId == ragId) return true;
        var latest = w.RagHistory.OrderByDescending(r => r.UpdatedAt).FirstOrDefault();
        return latest is { RagStatusId: > 0 } && latest.RagStatusId == ragId;
    }

    private static string NormalizeWorkRegisterTab(string? tab)
    {
        var tabKey = (tab ?? "active").Trim().ToLowerInvariant();
        return tabKey is "completed" or "cancelled" or "all" ? tabKey : "active";
    }

    private static bool WorkItemMatchesRegisterTab(WorkItem w, string tabKey)
    {
        var status = w.Status ?? "";
        return tabKey switch
        {
            "completed" => string.Equals(status, "Completed", StringComparison.OrdinalIgnoreCase),
            "cancelled" => string.Equals(status, "Cancelled", StringComparison.OrdinalIgnoreCase),
            "all" => string.Equals(status, "Active", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "Paused", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "Completed", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "Cancelled", StringComparison.OrdinalIgnoreCase),
            _ => string.Equals(status, "Active", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "Paused", StringComparison.OrdinalIgnoreCase)
        };
    }

    private static List<WorkItem> FilterWorkItemsByRegisterTab(IEnumerable<WorkItem> items, string? tab) =>
        items.Where(w => WorkItemMatchesRegisterTab(w, NormalizeWorkRegisterTab(tab))).ToList();

    private static string? ComposeMonthlyUpdateNarrativeForDisplay(ProjectMonthlyUpdate? mu)
    {
        if (mu == null) return null;
        if (!string.IsNullOrWhiteSpace(mu.Narrative))
            return mu.Narrative;
        if (mu.MonthlyUpdateNarratives is { Count: > 0 })
            return string.Join("\n\n", mu.MonthlyUpdateNarratives.OrderBy(n => n.CreatedAt).Select(n => n.Narrative));
        return mu.Narrative;
    }

    private readonly CompassDbContext _context;
    private readonly IModernWorkService _modernWork;
    private readonly IWorkScopedExcelExportService _workScopedExcelExport;
    private readonly INotificationRuleService _notificationRuleService;
    private readonly IMonthlyUpdateService _monthlyUpdateService;
    private readonly IWeeklyUpdateService _weeklyUpdateService;
    private readonly ILogger<ModernWorkController> _logger;
    private readonly IRaidRiskEditorFormService _raidRiskEditorForm;
    private readonly IRaidIssueEditorFormService _raidIssueEditorForm;
    private readonly IPermissionService _permissions;
    private readonly IWorkServiceRegisterLinkService _workServiceRegisterLinks;
    private readonly IWorkItemNotificationService _workItemNotifications;
    private readonly IViewAsUserService _viewAsUser;

    public ModernWorkController(
        CompassDbContext context,
        IModernWorkService modernWork,
        IWorkScopedExcelExportService workScopedExcelExport,
        INotificationRuleService notificationRuleService,
        IMonthlyUpdateService monthlyUpdateService,
        IWeeklyUpdateService weeklyUpdateService,
        ILogger<ModernWorkController> logger,
        IRaidRiskEditorFormService raidRiskEditorForm,
        IRaidIssueEditorFormService raidIssueEditorForm,
        IPermissionService permissions,
        IWorkServiceRegisterLinkService workServiceRegisterLinks,
        IWorkItemNotificationService workItemNotifications,
        IViewAsUserService viewAsUser)
    {
        _context = context;
        _modernWork = modernWork;
        _workScopedExcelExport = workScopedExcelExport;
        _notificationRuleService = notificationRuleService;
        _monthlyUpdateService = monthlyUpdateService;
        _weeklyUpdateService = weeklyUpdateService;
        _logger = logger;
        _raidRiskEditorForm = raidRiskEditorForm;
        _raidIssueEditorForm = raidIssueEditorForm;
        _permissions = permissions;
        _workServiceRegisterLinks = workServiceRegisterLinks;
        _workItemNotifications = workItemNotifications;
        _viewAsUser = viewAsUser;
    }

    private static readonly HashSet<string> ValidMilestoneStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "not_started", "in_progress", "on_track", "at_risk", "delayed", "complete", "cancelled"
    };

    private static int[]? MergeWorkTagQuery(int? tagId, int[]? tagIds)
    {
        var list = new List<int>();
        if (tagId.HasValue && tagId.Value > 0)
            list.Add(tagId.Value);
        if (tagIds != null)
        {
            foreach (var t in tagIds)
            {
                if (t > 0)
                    list.Add(t);
            }
        }

        var distinct = list.Distinct().ToArray();
        return distinct.Length == 0 ? null : distinct;
    }

    [HttpGet("dashboard")]
    public async Task<IActionResult> Dashboard(string? tab)
    {
        var scope = await ResolveViewScopeUserAsync();
        if (scope == null)
        {
            TempData["ErrorMessage"] = "Unable to identify the current user.";
            return View("~/Views/Modern/Work/Dashboard.cshtml");
        }

        var (currentUser, userEmail) = scope.Value;
        await _modernWork.PopulateWorkDashboardAsync(this, currentUser, userEmail, tab);
        return View("~/Views/Modern/Work/Dashboard.cshtml");
    }

    [HttpGet("")]
    [HttpGet("index")]
    public async Task<IActionResult> Index(
        string? search,
        int? businessAreaId,
        int? directorateId,
        int? phaseId,
        int? ragId,
        int? priorityId,
        string? monthlyUpdate = null,
        int? primaryContactUserId = null,
        int? tagId = null,
        [FromQuery] int[]? tagIds = null,
        string? sort = null,
        bool sd = false,
        CancellationToken cancellationToken = default)
    {
        ViewBag.MainNavSection = "work";
        ViewBag.SubNavItem = "work-all";

        var scope = await ResolveViewScopeUserAsync(cancellationToken);
        if (scope == null)
            return Unauthorized();

        var (currentUser, userEmail) = scope.Value;

        var mergedTags = MergeWorkTagQuery(tagId, tagIds);

        var vm = await _modernWork.BuildWorkRegisterAsync(
            isMyWork: false,
            search,
            portfolioId: null,
            directorateId,
            phaseId,
            ragId,
            priorityId,
            monthlyUpdate,
            currentUser,
            userEmail,
            Url,
            registerTab: null,
            registerPage: null,
            registerPageSize: 20,
            businessAreaId: businessAreaId,
            primaryContactUserId: primaryContactUserId,
            tagIds: mergedTags,
            registerSort: sort,
            registerSortDesc: sd,
            cancellationToken);

        return View("~/Views/Modern/Work/Index.cshtml", vm);
    }

    [HttpGet("all")]
    [HttpGet("/ModernWork/AllWork")]
    public async Task<IActionResult> AllWork(
        string? tab,
        [FromQuery(Name = "page")] int page = 1,
        string? search = null,
        int? businessAreaId = null,
        int? directorateId = null,
        int? phaseId = null,
        int? ragId = null,
        int? priorityId = null,
        string? monthlyUpdate = null,
        int? primaryContactUserId = null,
        int? tagId = null,
        [FromQuery] int[]? tagIds = null,
        bool mine = false,
        string? sort = null,
        bool sd = false,
        CancellationToken cancellationToken = default)
    {
        ViewBag.MainNavSection = "work";
        ViewBag.SubNavItem = "work-allwork";

        var scope = await ResolveViewScopeUserAsync(cancellationToken);
        if (scope == null)
            return Unauthorized();

        var (currentUser, userEmail) = scope.Value;

        var activeTab = (tab?.ToLowerInvariant()) switch
        {
            "completed" => "completed",
            "cancelled" => "cancelled",
            "all" => "all",
            _ => "active"
        };

        var safePage = page < 1 ? 1 : page;

        var mergedTags = MergeWorkTagQuery(tagId, tagIds);

        var vm = await _modernWork.BuildWorkRegisterAsync(
            isMyWork: mine,
            search,
            portfolioId: null,
            directorateId,
            phaseId,
            ragId,
            priorityId,
            monthlyUpdate,
            currentUser,
            userEmail,
            Url,
            registerTab: activeTab,
            registerPage: safePage,
            registerPageSize: 20,
            businessAreaId: businessAreaId,
            primaryContactUserId: primaryContactUserId,
            tagIds: mergedTags,
            registerSort: sort,
            registerSortDesc: sd,
            cancellationToken);

        vm.ActiveFilterChips = SearchAndFilterActiveChipsBuilder.ForWorkRegister(
            vm, Url, nameof(AllWork), "ModernWork", activeTab);

        var allWorkUrl = Url.Action(nameof(AllWork), "ModernWork") ?? "/modern/work/all";
        ViewBag.SearchAndFilter = new Compass.Models.SearchAndFilterViewModel
        {
            IdPrefix = "work",
            SearchPlaceholder = "Search work item titles…",
            SearchValue = search,
            FormActionUrl = Url.Action(nameof(AllWork), "ModernWork", new { tab = activeTab, page = 1 }) ?? allWorkUrl,
            FormMethod = "get",
            ClearUrl = Url.Action(nameof(AllWork), "ModernWork", new { tab = activeTab }) ?? allWorkUrl,
            ActiveChips = vm.ActiveFilterChips,
            Fields = new List<Compass.Models.SearchAndFilterFieldViewModel>()
        };
        ViewBag.ActiveTab = activeTab;
        ViewBag.AllWorkActiveTab = activeTab;
        ViewBag.WorkRegisterSubNav = WorkRegisterSubNavViewModel.FromRegister(vm, activeTab, mine);

        return View("~/Views/Modern/Work/AllWork.cshtml", vm);
    }

    [HttpGet("export-register")]
    public async Task<IActionResult> ExportRegister(string? scope, string? tab, string? search, int? portfolioId, int? businessAreaId, int? directorateId, int? phaseId, int? ragId, int? priorityId, string? monthlyUpdate,
        int? primaryContactUserId = null,
        int? tagId = null,
        [FromQuery] int[]? tagIds = null,
        bool mine = false,
        string? sort = null,
        bool sd = false,
        CancellationToken cancellationToken = default)
    {
        var scopeLabel = string.IsNullOrWhiteSpace(scope) ? "allwork" : scope.Trim().ToLowerInvariant();
        var scopeUser = await ResolveViewScopeUserAsync(cancellationToken);
        if (scopeUser == null)
            return Unauthorized();

        var (currentUser, userEmail) = scopeUser.Value;

        var normalizedTab = (tab ?? "active").Trim().ToLowerInvariant();
        var exportTab = normalizedTab is "completed" or "cancelled" or "all" ? normalizedTab : "active";

        var mergedTags = MergeWorkTagQuery(tagId, tagIds);

        var rows = await _modernWork.BuildWorkRegisterExportRowsAsync(
            mine,
            search,
            portfolioId,
            directorateId,
            phaseId,
            ragId,
            priorityId,
            monthlyUpdate,
            currentUser,
            userEmail,
            Url,
            exportTab,
            businessAreaId: businessAreaId,
            primaryContactUserId: primaryContactUserId,
            tagIds: mergedTags,
            projectIds: null,
            registerSort: sort,
            registerSortDesc: sd,
            cancellationToken);

        var projectIds = rows.Select(r => r.Id).ToList();
        var bytes = await _workScopedExcelExport.BuildWorkbookAsync(
            projectIds,
            currentUser,
            userEmail,
            Url,
            cancellationToken);

        var fileName = $"work-{scopeLabel}-{exportTab}-{DateTime.UtcNow:yyyyMMdd-HHmm}.xlsx";
        return File(
            bytes,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            fileName);
    }

    [HttpGet("create")]
    [HttpGet("/ModernWork/Create")]
    public async Task<IActionResult> Create(int? businessCaseId, CancellationToken cancellationToken = default)
    {
        ViewBag.MainNavSection = "work";
        ViewBag.SubNavItem = "work-all";

        var userEmail = User.Identity?.Name;
        if (string.IsNullOrEmpty(userEmail))
            return Unauthorized();

        var currentUser = await _context.Users
            .FirstOrDefaultAsync(u => u.Email.ToLower() == userEmail.ToLower(), cancellationToken);
        if (currentUser == null)
            return Unauthorized();

        await PopulateWorkCreateViewBagAsync(businessCaseId, cancellationToken);

        var today = DateTime.UtcNow.Date;
        var model = new WorkItem
        {
            Status = "Active",
            StartDate = today,
            SubjectToSpendControl = false,
            FlagshipProject = false
        };

        return View("~/Views/Modern/Work/Create.cshtml", model);
    }

    [HttpPost("create")]
    [HttpPost("/ModernWork/Create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        WorkItem model,
        int[]? directorateIds,
        int[]? priorityOutcomeIds,
        int[]? missionPillarIds,
        int[]? governmentDepartmentIds,
        int[]? workTagIds,
        string? initialRagJustification,
        string? initialPathToGreen,
        string? multiDept,
        int? businessCaseId,
        int? startDay,
        int? startMonth,
        int? startYear,
        int? targetEndDay,
        int? targetEndMonth,
        int? targetEndYear,
        CancellationToken cancellationToken = default)
    {
        ViewBag.MainNavSection = "work";
        ViewBag.SubNavItem = "work-all";

        var userEmail = User.Identity?.Name;
        if (string.IsNullOrEmpty(userEmail))
            return Unauthorized();

        var currentUser = await _context.Users
            .FirstOrDefaultAsync(u => u.Email.ToLower() == userEmail.ToLower(), cancellationToken);
        if (currentUser == null)
            return Unauthorized();

        directorateIds ??= Array.Empty<int>();
        priorityOutcomeIds ??= Array.Empty<int>();
        missionPillarIds ??= Array.Empty<int>();
        governmentDepartmentIds ??= Array.Empty<int>();
        workTagIds ??= Array.Empty<int>();

        GovUkDateBinding.BindGovUkDate(ModelState, nameof(model.StartDate), startDay, startMonth, startYear, required: true, out var startUtc);
        if (startUtc.HasValue)
            model.StartDate = startUtc;

        GovUkDateBinding.BindGovUkDate(ModelState, nameof(model.TargetEndDate), targetEndDay, targetEndMonth, targetEndYear, required: false, out var targetUtc);
        if (!ModelState.ContainsKey(nameof(model.TargetEndDate)))
            model.TargetEndDate = targetUtc;

        if (string.IsNullOrWhiteSpace(model.Title))
            ModelState.AddModelError(nameof(model.Title), "Enter a title.");
        if (string.IsNullOrWhiteSpace(model.ProblemStatement))
            ModelState.AddModelError(nameof(model.ProblemStatement), "Enter a problem statement.");
        if (string.IsNullOrWhiteSpace(model.Aim))
            ModelState.AddModelError(nameof(model.Aim), "Enter an aim.");
        if (!model.PortfolioId.HasValue)
            ModelState.AddModelError(nameof(model.PortfolioId), "Select a portfolio.");
        if (!model.PriorityId.HasValue)
            ModelState.AddModelError(nameof(model.PriorityId), "Select a priority.");
        if (!model.ActivityTypeId.HasValue)
            ModelState.AddModelError(nameof(model.ActivityTypeId), "Select an activity type.");
        if (!model.StartDate.HasValue && !ModelState.ContainsKey(nameof(model.StartDate)))
            ModelState.AddModelError(nameof(model.StartDate), "Enter a start date.");

        var multiDeptYes = string.Equals(multiDept, "yes", StringComparison.OrdinalIgnoreCase);
        if (multiDeptYes && governmentDepartmentIds.Length == 0)
            ModelState.AddModelError(nameof(governmentDepartmentIds), "Add at least one government department.");

        if (model.RagStatusId.HasValue)
        {
            var ragNameCheck = await _context.RagStatusLookups.AsNoTracking()
                .Where(r => r.Id == model.RagStatusId.Value)
                .Select(r => r.Name)
                .FirstOrDefaultAsync(cancellationToken);
            if (!MonthlyReportIsGreenRagName(ragNameCheck) && string.IsNullOrWhiteSpace(initialPathToGreen))
                ModelState.AddModelError("InitialPathToGreen", "Enter the path to green when RAG is not green.");
        }

        if (!ModelState.IsValid)
        {
            await PopulateWorkCreateViewBagAsync(businessCaseId, cancellationToken);
            ViewBag.SelectedDirectorateIds = directorateIds;
            ViewBag.SelectedPriorityOutcomeIds = priorityOutcomeIds;
            ViewBag.SelectedMissionPillarIds = missionPillarIds;
            ViewBag.SelectedWorkTagIds = workTagIds;
            ViewBag.InitialRagJustification = initialRagJustification;
            ViewBag.InitialPathToGreen = initialPathToGreen;
            ViewBag.SelectedGovernmentDepartmentIds = governmentDepartmentIds;
            ViewBag.MultiDeptYes = multiDeptYes;
            return View("~/Views/Modern/Work/Create.cshtml", model);
        }

        string? ragNameForInitial = null;
        if (model.RagStatusId.HasValue)
        {
            ragNameForInitial = await _context.RagStatusLookups.AsNoTracking()
                .Where(r => r.Id == model.RagStatusId.Value)
                .Select(r => r.Name)
                .FirstOrDefaultAsync(cancellationToken);
        }

        var pathToGreenPersist = model.RagStatusId.HasValue && !MonthlyReportIsGreenRagName(ragNameForInitial)
            ? (string.IsNullOrWhiteSpace(initialPathToGreen) ? null : initialPathToGreen.Trim())
            : null;

        var lastProject = await _context.Projects.OrderByDescending(p => p.ProjectCode).FirstOrDefaultAsync(cancellationToken);
        var nextNumber = 1;
        if (lastProject != null && !string.IsNullOrEmpty(lastProject.ProjectCode))
        {
            var parts = lastProject.ProjectCode.Split('-');
            if (parts.Length >= 2 && int.TryParse(parts[^1], out var lastNumber))
                nextNumber = lastNumber + 1;
        }

        var isMultiDept = multiDeptYes;
        string? otherDepartmentsJson = null;
        if (isMultiDept && governmentDepartmentIds.Length > 0)
            otherDepartmentsJson = JsonSerializer.Serialize(governmentDepartmentIds);

        var now = DateTime.UtcNow;
        var portfolioProject = new Project();
        var (portfolioOk, portfolioError) = await ApplyPortfolioSelectionAsync(
            portfolioProject, model.PortfolioId, cancellationToken);
        if (!portfolioOk)
        {
            ModelState.AddModelError(nameof(model.PortfolioId), portfolioError!);
            await PopulateWorkCreateViewBagAsync(businessCaseId, cancellationToken);
            ViewBag.SelectedDirectorateIds = directorateIds;
            ViewBag.SelectedPriorityOutcomeIds = priorityOutcomeIds;
            ViewBag.SelectedMissionPillarIds = missionPillarIds;
            ViewBag.SelectedWorkTagIds = workTagIds;
            ViewBag.InitialRagJustification = initialRagJustification;
            ViewBag.InitialPathToGreen = initialPathToGreen;
            ViewBag.SelectedGovernmentDepartmentIds = governmentDepartmentIds;
            ViewBag.MultiDeptYes = multiDeptYes;
            return View("~/Views/Modern/Work/Create.cshtml", model);
        }

        var project = new Project
        {
            ProjectCode = $"DDTDEL-{nextNumber:D4}",
            Title = model.Title.Trim(),
            Aim = model.Aim?.Trim(),
            Status = model.Status?.Trim() ?? "Active",
            StartDate = model.StartDate,
            TargetDeliveryDate = model.TargetEndDate,
            BusinessAreaId = portfolioProject.BusinessAreaId,
            PrimaryOrganizationalGroupId = portfolioProject.PrimaryOrganizationalGroupId,
            PhaseId = model.DeliveryPhaseId,
            DeliveryPriorityId = model.PriorityId,
            RagStatusLookupId = model.RagStatusId,
            ActivityTypeLookupId = model.ActivityTypeId,
            RiskAppetiteLookupId = model.RiskAppetiteId,
            IsFlagship = false,
            IsAiInitiative = false,
            IsSubjectToSpendControl = model.SubjectToSpendControl,
            RagJustification = string.IsNullOrWhiteSpace(initialRagJustification) ? null : initialRagJustification.Trim(),
            PathToGreen = pathToGreenPersist,
            IsMultiDepartmentProject = isMultiDept,
            OtherDepartments = otherDepartmentsJson,
            CreatedAt = now,
            UpdatedAt = now,
            CreationMethod = "Manual"
        };

        _context.Projects.Add(project);
        await _context.SaveChangesAsync(cancellationToken);

        _context.ProjectProblemStatements.Add(new ProjectProblemStatement
        {
            ProjectId = project.Id,
            ProblemStatement = model.ProblemStatement!.Trim(),
            CreatedByEmail = userEmail,
            CreatedByName = currentUser.Name,
            CreatedAt = now,
            UpdatedAt = now
        });

        foreach (var divId in directorateIds.Distinct())
        {
            _context.ProjectDirectorates.Add(new ProjectDirectorate
            {
                ProjectId = project.Id,
                DivisionId = divId,
                CreatedAt = now
            });
        }

        foreach (var objectiveId in priorityOutcomeIds.Distinct())
        {
            _context.ProjectObjectives.Add(new ProjectObjective
            {
                ProjectId = project.Id,
                ObjectiveId = objectiveId,
                CreatedAt = now
            });
        }

        foreach (var missionId in missionPillarIds.Distinct())
        {
            _context.ProjectMissions.Add(new ProjectMission
            {
                ProjectId = project.Id,
                MissionId = missionId,
                CreatedAt = now
            });
        }

        var activeTagIds = await _context.WorkItemTagLookups.AsNoTracking()
            .Where(t => t.IsActive && workTagIds.Contains(t.Id))
            .Select(t => t.Id)
            .ToListAsync(cancellationToken);
        foreach (var tagId in activeTagIds.Distinct())
        {
            _context.ProjectWorkItemTags.Add(new ProjectWorkItemTag
            {
                ProjectId = project.Id,
                WorkItemTagLookupId = tagId
            });
        }

        if (model.RagStatusId.HasValue)
        {
            var ragName = ragNameForInitial ?? string.Empty;
            _context.ProjectRagHistories.Add(new ProjectRagHistory
            {
                ProjectId = project.Id,
                RagStatusLookupId = model.RagStatusId,
                RagStatus = ragName,
                Justification = string.IsNullOrWhiteSpace(initialRagJustification) ? null : initialRagJustification.Trim(),
                PathToGreen = pathToGreenPersist,
                ChangedByEmail = userEmail,
                ChangedByName = currentUser.Name,
                ChangedAt = now
            });
        }

        if (businessCaseId.HasValue)
        {
            var bcExists = await _context.BusinessCases.AsNoTracking().AnyAsync(b => b.Id == businessCaseId.Value, cancellationToken);
            if (bcExists)
            {
                _context.BusinessCaseProjects.Add(new BusinessCaseProject
                {
                    BusinessCaseId = businessCaseId.Value,
                    ProjectId = project.Id,
                    CreatedAt = now
                });
            }
        }

        await _context.SaveChangesAsync(cancellationToken);

        try
        {
            await _workItemNotifications.TrySendWorkItemCreatedAsync(
                project.Id,
                userEmail,
                currentUser.Name,
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send work item created notification for project {ProjectId}", project.Id);
        }

        return RedirectToAction(nameof(Detail), new { id = project.Id });
    }

    private async Task PopulateWorkCreateViewBagAsync(int? businessCaseId, CancellationToken cancellationToken)
    {
        var orgGroups = await _context.OrganizationalGroups.AsNoTracking().Where(g => g.IsActive).OrderBy(g => g.Name).ToListAsync(cancellationToken);

        var businessAreas = await _context.BusinessAreaLookups.AsNoTracking().Where(b => b.IsActive).OrderBy(b => b.SortOrder).ThenBy(b => b.Name).ToListAsync(cancellationToken);
        ViewBag.Portfolios = businessAreas.Select(b => new Portfolio { Id = b.Id, Name = b.Name, IsActive = true }).ToList();

        var directorates = await _context.Divisions.AsNoTracking().Where(d => d.IsActive).OrderBy(d => d.Name)
            .Select(d => new Directorate { Id = d.Id, Name = d.Name, IsActive = true }).ToListAsync(cancellationToken);
        ViewBag.Directorates = directorates;

        ViewBag.WorkStatusOptions = new List<LookupOption>
        {
            new() { Name = "Active", Value = "Active" },
            new() { Name = "Paused", Value = "Paused" },
            new() { Name = "Completed", Value = "Completed" },
            new() { Name = "Cancelled", Value = "Cancelled" }
        };

        ViewBag.WorkPriorityOptions = await _context.DeliveryPriorities.AsNoTracking().OrderBy(p => p.SortOrder)
            .Select(p => new LookupOption { Id = p.Id, Name = p.Name ?? "", Value = p.Name ?? "" }).ToListAsync(cancellationToken);

        ViewBag.DeliveryPhaseOptions = await _context.PhaseLookups.AsNoTracking().Where(p => p.IsActive).OrderBy(p => p.SortOrder)
            .Select(p => new LookupOption { Id = p.Id, Name = p.Name ?? "", Value = p.Name ?? "" }).ToListAsync(cancellationToken);

        ViewBag.ActivityTypeOptions = await _context.ActivityTypeLookups.AsNoTracking().Where(a => a.IsActive).OrderBy(a => a.SortOrder)
            .Select(a => new LookupOption { Id = a.Id, Name = a.Name ?? "", Value = a.Name ?? "" }).ToListAsync(cancellationToken);

        ViewBag.RiskAppetiteOptions = await _context.RiskAppetiteLookups.AsNoTracking().Where(r => r.IsActive).OrderBy(r => r.SortOrder)
            .Select(r => new LookupOption { Id = r.Id, Name = r.Name ?? "", Value = r.Name ?? "" }).ToListAsync(cancellationToken);

        ViewBag.RagStatuses = await _context.RagStatusLookups.AsNoTracking().Where(r => r.IsActive).OrderBy(r => r.SortOrder)
            .Select(r => new RagStatus { Id = r.Id, Name = r.Name }).ToListAsync(cancellationToken);

        ViewBag.PriorityOutcomes = await _context.Objectives.AsNoTracking()
            .Where(o => !o.IsDeleted && o.Status == "active")
            .OrderBy(o => o.Title)
            .Select(o => new WorkLookupOption { Id = o.Id, Name = o.Title, Value = o.Title }).ToListAsync(cancellationToken);

        ViewBag.MissionPillars = await _context.Missions.AsNoTracking()
            .Where(m => !m.IsDeleted)
            .OrderBy(m => m.Title)
            .Select(m => new WorkLookupOption { Id = m.Id, Name = m.Title, Value = m.Title }).ToListAsync(cancellationToken);

        ViewBag.GovernmentDepartments = await _context.GovernmentDepartments.AsNoTracking().OrderBy(g => g.Title).ToListAsync(cancellationToken);

        ViewBag.SelectedDirectorateIds = Array.Empty<int>();
        ViewBag.SelectedPriorityOutcomeIds = Array.Empty<int>();
        ViewBag.SelectedMissionPillarIds = Array.Empty<int>();
        ViewBag.WorkTagOptions = await _context.WorkItemTagLookups.AsNoTracking()
            .Where(t => t.IsActive)
            .OrderBy(t => t.SortOrder).ThenBy(t => t.Name)
            .Select(t => new LookupOption { Id = t.Id, Name = t.Name ?? "", Value = t.Description ?? "" })
            .ToListAsync(cancellationToken);
        ViewBag.SelectedWorkTagIds = Array.Empty<int>();
        ViewBag.InitialRagJustification = null;
        ViewBag.FromDemand = null;
        ViewBag.FromBusinessCase = null;

        if (businessCaseId.HasValue)
        {
            var bc = await _context.BusinessCases.AsNoTracking().FirstOrDefaultAsync(b => b.Id == businessCaseId.Value, cancellationToken);
            if (bc != null)
                ViewBag.FromBusinessCase = bc;
        }
    }

    /// <summary>Start a monthly return for the given period (from reporting table) — modern monthly report flow.</summary>
    [HttpGet("{id:int}/monthly-update/add")]
    public IActionResult AddMonthlyUpdate(int id, [FromQuery] string? periodKey)
    {
        if (!string.IsNullOrWhiteSpace(periodKey))
        {
            var parts = periodKey.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length >= 2 && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y)
                && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var m)
                && m is >= 1 and <= 12)
            {
                return RedirectToAction(nameof(MonthlyReport), new { id, year = y, month = m });
            }
        }

        var now = DateTime.UtcNow;
        var (year, month) = _monthlyUpdateService.ResolveDashboardReportingPeriod(now);
        return RedirectToAction(nameof(MonthlyReport), new { id, year, month });
    }

    /// <summary>View a submitted monthly update (modern UI). Matches <c>/ModernWork/ViewMonthlyUpdate/{id}?updateId=</c> from default MVC routes.</summary>
    [HttpGet("{id:int}/monthly-update/view")]
    [HttpGet("ViewMonthlyUpdate/{id:int}")]
    [HttpGet("/ModernWork/ViewMonthlyUpdate/{id:int}")]
    public async Task<IActionResult> ViewMonthlyUpdate(int id, [FromQuery] int updateId, CancellationToken cancellationToken = default)
    {
        var userEmail = User.Identity?.Name;
        if (string.IsNullOrEmpty(userEmail))
            return Unauthorized();

        var currentUser = await _context.Users
            .FirstOrDefaultAsync(u => u.Email.ToLower() == userEmail.ToLower(), cancellationToken);
        if (currentUser == null)
            return Unauthorized();

        var mu = await _context.ProjectMonthlyUpdates.AsNoTracking()
            .Include(m => m.MonthlyUpdateNarratives)
            .Include(m => m.DraftRagStatusLookup)
            .FirstOrDefaultAsync(m => m.Id == updateId && m.ProjectId == id, cancellationToken);
        if (mu == null)
            return NotFound();

        if (!mu.SubmittedAt.HasValue)
            return RedirectToAction(nameof(MonthlyReport), new { id, year = mu.Year, month = mu.Month });

        var work = await _modernWork.PopulateWorkDetailAsync(
            this,
            id,
            currentUser,
            userEmail,
            tab: "updates",
            milestonestab: null,
            cancellationToken);
        if (work == null)
            return NotFound();

        ViewBag.WorkItem = work;

        var vm = new MonthlyUpdate
        {
            Id = mu.Id,
            WorkItemId = mu.ProjectId,
            ReportMonth = new DateTime(mu.Year, mu.Month, 1, 0, 0, 0, DateTimeKind.Utc),
            Narrative = ComposeMonthlyUpdateNarrativeForDisplay(mu),
            SubmittedAt = mu.SubmittedAt,
            SubmittedByUserId = mu.CreatedByUserId,
            SubmittedBy = mu.CreatedByName ?? (mu.SubmittedAt.HasValue ? mu.CreatedByEmail : null),
            PermFte = mu.MonthlyPermFte,
            MspFte = mu.MonthlyMspFte,
            PeopleNarrative = mu.PeopleNarrative
        };

        if (mu.CreatedByUserId.HasValue)
        {
            var sub = await _context.Users.AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == mu.CreatedByUserId.Value, cancellationToken);
            ViewBag.SubmittedByName = sub?.Name ?? sub?.Email ?? vm.SubmittedBy ?? "—";
        }
        else
        {
            ViewBag.SubmittedByName = vm.SubmittedBy ?? "—";
        }

        var ragHistDesc = await _context.ProjectRagHistories.AsNoTracking()
            .Include(r => r.RagStatusLookup)
            .Where(r => r.ProjectId == id)
            .OrderByDescending(r => r.ChangedAt)
            .ThenByDescending(r => r.Id)
            .ToListAsync(cancellationToken);
        var ragLookupById = await _context.RagStatusLookups.AsNoTracking()
            .ToDictionaryAsync(r => r.Id, cancellationToken);
        var projectRow = await _context.Projects.AsNoTracking()
            .Include(p => p.RagStatusLookup)
            .FirstOrDefaultAsync(p => p.Id == id && !p.IsDeleted, cancellationToken);
        var resolvedRag = MonthlyUpdateRagResolver.Resolve(mu, ragHistDesc, projectRow, ragLookupById);
        MonthlyUpdateRagResolver.ApplyDisplay(
            resolvedRag,
            ragLookupById,
            (statusId, name, cssClass) =>
            {
                vm.RagStatusId = statusId;
                vm.RagDisplayName = name;
                vm.RagCssClass = cssClass;
            });

        if (mu.DraftRagStatusLookupId.HasValue || resolvedRag.StatusId.HasValue)
        {
            vm.RagJustification = mu.DraftRagJustification;
            vm.PathToGreen = mu.DraftPathToGreen;
        }
        else
        {
            var nearestRag = MonthlyUpdateSubmittedRagResolver.Resolve(ragHistDesc, mu.SubmittedAt!.Value)
                ?? ragHistDesc.FirstOrDefault();
            if (nearestRag != null)
            {
                vm.RagJustification = nearestRag.Justification;
                vm.PathToGreen = nearestRag.PathToGreen;
            }
        }

        var ragRows = await _context.RagStatusLookups.AsNoTracking()
            .Where(r => r.IsActive)
            .OrderBy(r => r.SortOrder)
            .ToListAsync(cancellationToken);
        ViewBag.RagStatusesDict = ragLookupById.Values
            .GroupBy(r => r.Id)
            .ToDictionary(g => g.Key, g => g.First().Name);
        ViewBag.RagBgByStatusId = new Dictionary<int, string?>();
        ViewBag.RagTextByStatusId = new Dictionary<int, string?>();
        ViewBag.RagCssClassByStatusId = ragLookupById.ToDictionary(kv => kv.Key, kv => kv.Value.CssClass);

        var periodDue = _monthlyUpdateService.GetMonthlyUpdateDueDate(mu.Year, mu.Month);
        ViewBag.PeriodDueDate = periodDue;
        var canUnsubmit = mu.SubmittedAt.HasValue && DateTime.UtcNow.Date <= periodDue.Date;
        ViewBag.CanUnsubmit = canUnsubmit;

        ViewBag.WorkChromeSubPage = false;
        ViewBag.WorkChromeMinimalHeader = false;

        return View("~/Views/Modern/Work/ViewMonthlyUpdate.cshtml", vm);
    }

    /// <summary>Edit draft monthly return — modern monthly report flow.</summary>
    [HttpGet("{id:int}/monthly-update/edit")]
    [HttpGet("EditMonthlyUpdate/{id:int}")]
    [HttpGet("/ModernWork/EditMonthlyUpdate/{id:int}")]
    public async Task<IActionResult> EditMonthlyUpdate(int id, [FromQuery] int updateId, CancellationToken cancellationToken = default)
    {
        var mu = await _context.ProjectMonthlyUpdates.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == updateId && m.ProjectId == id, cancellationToken);
        if (mu == null)
            return NotFound();
        if (mu.SubmittedAt.HasValue)
            return RedirectToAction(nameof(ViewMonthlyUpdate), new { id, updateId });
        return RedirectToAction(nameof(MonthlyReport), new { id, year = mu.Year, month = mu.Month });
    }

    [HttpPost("{id:int}/monthly-update/unsubmit")]
    [HttpPost("/ModernWork/UnsubmitMonthlyUpdate/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UnsubmitMonthlyUpdate(int id, [FromForm] int updateId, CancellationToken cancellationToken = default)
    {
        var userEmail = User.Identity?.Name;
        if (string.IsNullOrEmpty(userEmail))
            return Unauthorized();

        var mu = await _context.ProjectMonthlyUpdates
            .FirstOrDefaultAsync(m => m.Id == updateId && m.ProjectId == id, cancellationToken);
        if (mu == null)
            return NotFound();
        if (!mu.SubmittedAt.HasValue)
        {
            TempData["Message"] = "This monthly update is not submitted.";
            return RedirectToAction(nameof(ViewMonthlyUpdate), new { id, updateId });
        }

        var periodDue = _monthlyUpdateService.GetMonthlyUpdateDueDate(mu.Year, mu.Month);
        if (DateTime.UtcNow.Date > periodDue.Date)
        {
            TempData["Error"] = "Unsubmit is only allowed before the period due date has passed.";
            return RedirectToAction(nameof(ViewMonthlyUpdate), new { id, updateId });
        }

        mu.SubmittedAt = null;
        var project = await _context.Projects.FirstOrDefaultAsync(p => p.Id == id && !p.IsDeleted, cancellationToken);
        if (project != null)
            project.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
        TempData["Message"] = "Monthly update unsubmitted. You can edit and resubmit.";
        return RedirectToAction(nameof(EditMonthlyUpdate), new { id, updateId });
    }

    [HttpGet("{id:int}/monthly-report/{year:int}/{month:int}")]
    public async Task<IActionResult> MonthlyReport(int id, int year, int month, CancellationToken cancellationToken = default)
    {
        if (month < 1 || month > 12)
            return BadRequest("Invalid month.");

        var vm = await LoadMonthlyReportViewModelAsync(id, year, month, posted: null, cancellationToken);
        if (vm == null)
            return NotFound();

        return await MonthlyReportViewResultAsync(vm, cancellationToken);
    }

    [HttpPost("{id:int}/monthly-report/{year:int}/{month:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MonthlyReportPost(
        int id, int year, int month,
        string? narrative, string? peopleNarrative, decimal? permFte, decimal? mspFte,
        int? ragStatusId, string? ragJustification, string? pathToGreen,
        string? command,
        CancellationToken cancellationToken = default)
    {
        if (month < 1 || month > 12)
            return BadRequest("Invalid month.");

        var userEmail = User.Identity?.Name;
        if (string.IsNullOrEmpty(userEmail))
            return Unauthorized();

        var currentUser = await _context.Users
            .FirstOrDefaultAsync(u => u.Email.ToLower() == userEmail.ToLower(), cancellationToken);

        var project = await _context.Projects
            .FirstOrDefaultAsync(p => p.Id == id && !p.IsDeleted, cancellationToken);
        if (project == null)
            return NotFound();

        var explicitPm = _monthlyUpdateService.TryGetActiveExplicitReportingPeriod(year, month);
        if (explicitPm != null &&
            !_monthlyUpdateService.IsMonthlyReportEditingAllowed(year, month))
        {
            TempData["MonthlyReportError"] =
                "This reporting period is not accepting submissions yet, or the submission window has closed.";
            return RedirectToAction(nameof(MonthlyReport), new { id, year, month });
        }

        var existingForLock = await _context.ProjectMonthlyUpdates.AsNoTracking()
            .FirstOrDefaultAsync(m => m.ProjectId == id && m.Year == year && m.Month == month, cancellationToken);
        if (existingForLock?.SubmittedAt != null)
        {
            TempData["MonthlyReportError"] = "This monthly report has already been submitted.";
            return RedirectToAction(nameof(MonthlyReport), new { id, year, month });
        }

        var isSubmit = string.Equals(command, "submit", StringComparison.OrdinalIgnoreCase);
        var isSave = string.Equals(command, "save", StringComparison.OrdinalIgnoreCase);
        if (!isSubmit && !isSave)
            ModelState.AddModelError(string.Empty, "Choose Save as draft or Submit monthly report.");

        RagStatusLookup? resolvedRag = null;
        if (ragStatusId is { } ridPost)
        {
            resolvedRag = await _context.RagStatusLookups.AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == ridPost && r.IsActive, cancellationToken);
        }

        ValidateMonthlyReportForm(
            ModelState,
            isSubmit,
            narrative,
            peopleNarrative,
            permFte,
            mspFte,
            ragStatusId,
            resolvedRag,
            ragJustification,
            pathToGreen);

        if (!ModelState.IsValid)
        {
            var posted = new MonthlyReportPostedForm(
                narrative,
                peopleNarrative,
                permFte,
                mspFte,
                ragStatusId,
                ragJustification,
                pathToGreen);
            var vmInvalid = await LoadMonthlyReportViewModelAsync(id, year, month, posted, cancellationToken);
            if (vmInvalid == null)
                return NotFound();
            return await MonthlyReportViewResultAsync(vmInvalid, cancellationToken);
        }

        var pathPersist = resolvedRag != null && MonthlyReportIsGreenRagName(resolvedRag.Name)
            ? null
            : pathToGreen;

        var update = await _context.ProjectMonthlyUpdates
            .FirstOrDefaultAsync(m => m.ProjectId == id && m.Year == year && m.Month == month, cancellationToken);

        if (update == null)
        {
            update = new ProjectMonthlyUpdate
            {
                ProjectId = id,
                Year = year,
                Month = month,
                CreatedAt = DateTime.UtcNow,
                CreatedByEmail = userEmail,
                CreatedByName = currentUser?.Name,
                CreatedByUserId = currentUser?.Id
            };
            _context.ProjectMonthlyUpdates.Add(update);
        }

        update.Narrative = narrative ?? string.Empty;
        update.PeopleNarrative = peopleNarrative;
        update.MonthlyPermFte = permFte;
        update.MonthlyMspFte = mspFte;
        update.UpdatedAt = DateTime.UtcNow;

        if (!isSubmit)
        {
            update.DraftRagStatusLookupId = ragStatusId;
            update.DraftRagJustification = ragStatusId.HasValue ? ragJustification : null;
            update.DraftPathToGreen = ragStatusId.HasValue ? pathPersist : null;
        }
        else
        {
            // Keep draft RAG fields as the submitted snapshot (used by work detail + ViewMonthlyUpdate).
            update.DraftRagStatusLookupId = ragStatusId;
            update.DraftRagJustification = ragStatusId.HasValue ? ragJustification : null;
            update.DraftPathToGreen = ragStatusId.HasValue ? pathPersist : null;
            if (ragStatusId.HasValue)
            {
                var ragEntry = new ProjectRagHistory
                {
                    ProjectId = id,
                    RagStatusLookupId = ragStatusId.Value,
                    RagStatus = resolvedRag?.Name ?? string.Empty,
                    Justification = ragJustification,
                    PathToGreen = pathPersist,
                    ChangedAt = DateTime.UtcNow,
                    ChangedByEmail = userEmail,
                    ChangedByName = currentUser?.Name
                };
                _context.ProjectRagHistories.Add(ragEntry);
                project.RagStatusLookupId = ragStatusId.Value;
#pragma warning disable CS0618
                project.RagStatus = resolvedRag?.Name;
#pragma warning restore CS0618
                project.RagJustification = ragJustification;
                project.PathToGreen = pathPersist;
            }
        }

        if (isSubmit)
            update.SubmittedAt = DateTime.UtcNow;

        project.UpdatedAt = DateTime.UtcNow;
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MonthlyReportPost SaveChanges failed for project {ProjectId} {Year}/{Month}", id, year, month);
            ModelState.AddModelError(string.Empty, "Unable to save the monthly report. Try again or contact support if the problem continues.");
            var postedOnError = new MonthlyReportPostedForm(
                narrative,
                peopleNarrative,
                permFte,
                mspFte,
                ragStatusId,
                ragJustification,
                pathToGreen);
            var vmOnError = await LoadMonthlyReportViewModelAsync(id, year, month, postedOnError, cancellationToken);
            if (vmOnError == null)
                return NotFound();
            return await MonthlyReportViewResultAsync(vmOnError, cancellationToken);
        }

        TempData["MonthlyReportMessage"] = isSubmit
            ? "Monthly update submitted successfully."
            : "Monthly update saved as draft.";

        return RedirectToAction(nameof(MonthlyReport), new { id, year, month });
    }

    [HttpPost("{id:int}/monthly-report/{year:int}/{month:int}/unsubmit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MonthlyReportUnsubmit(int id, int year, int month, CancellationToken cancellationToken = default)
    {
        var update = await _context.ProjectMonthlyUpdates
            .FirstOrDefaultAsync(m => m.ProjectId == id && m.Year == year && m.Month == month, cancellationToken);
        if (update == null)
            return NotFound();

        if (!update.SubmittedAt.HasValue)
        {
            TempData["MonthlyReportMessage"] = "This update is not submitted.";
            return RedirectToAction(nameof(MonthlyReport), new { id, year, month });
        }

        var dueDate = _monthlyUpdateService.GetMonthlyUpdateDueDate(year, month);
        if (DateTime.UtcNow.Date > dueDate.Date)
        {
            TempData["MonthlyReportError"] = "Unsubmit is only allowed before the period due date has passed.";
            return RedirectToAction(nameof(MonthlyReport), new { id, year, month });
        }

        update.SubmittedAt = null;
        var project = await _context.Projects.FirstOrDefaultAsync(p => p.Id == id && !p.IsDeleted, cancellationToken);
        if (project != null)
            project.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);

        TempData["MonthlyReportMessage"] = "Monthly update unsubmitted. You can now edit and resubmit.";
        return RedirectToAction(nameof(MonthlyReport), new { id, year, month });
    }

    private sealed record MonthlyReportPostedForm(
        string? Narrative,
        string? PeopleNarrative,
        decimal? PermFte,
        decimal? MspFte,
        int? RagStatusId,
        string? RagJustification,
        string? PathToGreen);

    private const int MonthlyReportMaxTextLength = 4000;
    private const decimal MonthlyReportMaxHeadcount = 10000m;

    private static bool MonthlyReportIsGreenRagName(string? name) =>
        !string.IsNullOrEmpty(name) &&
        string.Equals(name, "Green", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Server-side validation for monthly report. Path to green is required only when submitting with a non-green RAG (field not in scope when green).
    /// Permanent FTE and MSC are required on submit only (draft save may omit them).
    /// </summary>
    private static void ValidateMonthlyReportForm(
        ModelStateDictionary modelState,
        bool isSubmit,
        string? narrative,
        string? peopleNarrative,
        decimal? permFte,
        decimal? mspFte,
        int? ragStatusId,
        RagStatusLookup? resolvedRag,
        string? ragJustification,
        string? pathToGreen)
    {
        static bool FieldHasBinderError(ModelStateDictionary ms, string key) =>
            ms.TryGetValue(key, out var entry) && entry!.Errors.Count > 0;

        void MaxLen(string key, string? value, string label)
        {
            if (!string.IsNullOrEmpty(value) && value.Length > MonthlyReportMaxTextLength)
                modelState.AddModelError(key, $"{label} must be {MonthlyReportMaxTextLength:N0} characters or fewer.");
        }

        MaxLen(nameof(narrative), narrative, "Monthly update narrative");
        MaxLen(nameof(peopleNarrative), peopleNarrative, "Narrative on people this month");
        MaxLen(nameof(ragJustification), ragJustification, "Justification for RAG");
        if (resolvedRag != null && !MonthlyReportIsGreenRagName(resolvedRag.Name))
            MaxLen(nameof(pathToGreen), pathToGreen, "Path to green");

        if (!FieldHasBinderError(modelState, nameof(permFte)) && permFte is { } p &&
            (p < 0m || p > MonthlyReportMaxHeadcount))
        {
            modelState.AddModelError(nameof(permFte),
                $"Permanent FTE must be between 0 and {MonthlyReportMaxHeadcount:N0}.");
        }

        if (!FieldHasBinderError(modelState, nameof(mspFte)) && mspFte is { } m &&
            (m < 0m || m > MonthlyReportMaxHeadcount))
        {
            modelState.AddModelError(nameof(mspFte),
                $"MSC number must be between 0 and {MonthlyReportMaxHeadcount:N0}.");
        }

        if (!isSubmit)
            return;

        if (string.IsNullOrWhiteSpace(narrative))
            modelState.AddModelError(nameof(narrative), "Enter a monthly update narrative.");

        if (!FieldHasBinderError(modelState, nameof(permFte)) && !permFte.HasValue)
            modelState.AddModelError(nameof(permFte), "Enter permanent FTE.");

        if (!FieldHasBinderError(modelState, nameof(mspFte)) && !mspFte.HasValue)
            modelState.AddModelError(nameof(mspFte), "Enter MSC number.");

        if (!ragStatusId.HasValue)
        {
            modelState.AddModelError("rag-status-group", "Select a RAG outcome.");
        }
        else if (resolvedRag == null)
        {
            modelState.AddModelError("rag-status-group", "Select a valid RAG outcome.");
        }

        if (string.IsNullOrWhiteSpace(ragJustification))
            modelState.AddModelError(nameof(ragJustification), "Enter a justification for the RAG rating.");

        var needsPathToGreen = resolvedRag != null && !MonthlyReportIsGreenRagName(resolvedRag.Name);
        if (needsPathToGreen && string.IsNullOrWhiteSpace(pathToGreen))
            modelState.AddModelError(nameof(pathToGreen), "Enter the path to green — describe how delivery confidence will return to green.");
    }

    private async Task<MonthlyReportViewModel?> LoadMonthlyReportViewModelAsync(
        int id,
        int year,
        int month,
        MonthlyReportPostedForm? posted,
        CancellationToken cancellationToken)
    {
        if (month < 1 || month > 12)
            return null;

        var project = await _context.Projects.AsNoTracking()
            .Include(p => p.PrimaryOrganizationalGroup)
            .Include(p => p.BusinessAreaLookup)
            .Include(p => p.PhaseLookup)
            .Include(p => p.DeliveryPriority)
            .FirstOrDefaultAsync(p => p.Id == id && !p.IsDeleted, cancellationToken);
        if (project == null)
            return null;

        var update = await _context.ProjectMonthlyUpdates
            .AsNoTracking()
            .Include(m => m.MonthlyUpdateNarratives)
            .FirstOrDefaultAsync(m => m.ProjectId == id && m.Year == year && m.Month == month, cancellationToken);

        var ragHistDesc = await _context.ProjectRagHistories.AsNoTracking()
            .Include(r => r.RagStatusLookup)
            .Where(r => r.ProjectId == id)
            .OrderByDescending(r => r.ChangedAt)
            .ThenByDescending(r => r.Id)
            .ToListAsync(cancellationToken);
        var latestRag = ragHistDesc.FirstOrDefault();

        var ragStatuses = await _context.RagStatusLookups.AsNoTracking()
            .Where(r => r.IsActive)
            .OrderBy(r => r.SortOrder)
            .Select(r => new RagStatus { Id = r.Id, Name = r.Name, Description = r.Description })
            .ToListAsync(cancellationToken);

        var dueDate = _monthlyUpdateService.GetMonthlyUpdateDueDate(year, month);
        var closeDate = _monthlyUpdateService.GetMonthlyUpdateCloseDate(year, month);
        var canUnsubmit = update?.SubmittedAt != null && DateTime.UtcNow.Date <= dueDate.Date;

        int? currentRagId = null;
        string? currentRagJustification = null;
        string? currentPathToGreen = null;

        if (update?.SubmittedAt != null)
        {
            if (update.DraftRagStatusLookupId.HasValue)
            {
                currentRagId = update.DraftRagStatusLookupId;
                currentRagJustification = update.DraftRagJustification;
                currentPathToGreen = update.DraftPathToGreen;
            }
            else
            {
                var ragAtSubmit = MonthlyUpdateSubmittedRagResolver.Resolve(ragHistDesc, update.SubmittedAt.Value);
                if (ragAtSubmit != null)
                {
                    currentRagId = ragAtSubmit.RagStatusLookupId;
                    currentRagJustification = ragAtSubmit.Justification;
                    currentPathToGreen = ragAtSubmit.PathToGreen;
                }
            }
        }
        else if (update != null && (update.DraftRagStatusLookupId.HasValue ||
                                    !string.IsNullOrEmpty(update.DraftRagJustification) ||
                                    !string.IsNullOrEmpty(update.DraftPathToGreen)))
        {
            currentRagId = update.DraftRagStatusLookupId;
            currentRagJustification = update.DraftRagJustification;
            currentPathToGreen = update.DraftPathToGreen;
        }
        else if (latestRag != null)
        {
            currentRagId = latestRag.RagStatusLookupId;
            currentRagJustification = latestRag.Justification;
            currentPathToGreen = latestRag.PathToGreen;
        }

        string? submittedByName = null;
        if (update?.SubmittedAt != null)
        {
            submittedByName = update.CreatedByName;
            if (string.IsNullOrEmpty(submittedByName) && update.CreatedByUserId.HasValue)
            {
                var sub = await _context.Users.AsNoTracking()
                    .FirstOrDefaultAsync(u => u.Id == update.CreatedByUserId.Value, cancellationToken);
                submittedByName = sub?.Name ?? sub?.Email;
            }
            submittedByName ??= update.CreatedByEmail ?? "Unknown";
        }

        var explicitPeriod =
            _monthlyUpdateService.TryGetActiveExplicitReportingPeriod(year, month);

        var vm = new MonthlyReportViewModel
        {
            WorkItemId = id,
            WorkItemTitle = project.Title,
            WorkItemReference = "WI-" + id.ToString("D8", CultureInfo.InvariantCulture),
            Year = year,
            Month = month,
            UpdateId = update?.Id,
            IsSubmitted = update?.SubmittedAt.HasValue == true,
            SubmittedAt = update?.SubmittedAt,
            SubmittedByName = submittedByName,
            Narrative = ComposeMonthlyUpdateNarrativeForDisplay(update),
            PeopleNarrative = update?.PeopleNarrative,
            PermFte = update?.MonthlyPermFte,
            MspFte = update?.MonthlyMspFte,
            RagStatusId = currentRagId,
            RagJustification = currentRagJustification,
            PathToGreen = currentPathToGreen,
            DueDate = dueDate,
            CloseDate = closeDate,
            CanUnsubmit = canUnsubmit,
            IsPastCloseDate = DateTime.UtcNow.Date > closeDate.Date,
            SubmissionOpens = explicitPeriod?.SubmissionOpens.Date,
            SubmissionCloses = explicitPeriod?.SubmissionCloses.Date,
            DueRuleDescription = _monthlyUpdateService.GetMonthlyUpdateDueRuleDescription(year, month),
            UsesExplicitReportingPeriod = explicitPeriod != null,
            CanEditMonthlySubmission = _monthlyUpdateService.IsMonthlyReportEditingAllowed(year, month),
            RagStatuses = ragStatuses
        };

        if (posted != null)
        {
            vm.Narrative = posted.Narrative;
            vm.PeopleNarrative = posted.PeopleNarrative;
            vm.PermFte = posted.PermFte;
            vm.MspFte = posted.MspFte;
            vm.RagStatusId = posted.RagStatusId;
            vm.RagJustification = posted.RagJustification;
            vm.PathToGreen = posted.PathToGreen;
        }

        vm.PreviousMonthSubmission = await TryLoadPreviousMonthSubmissionAsync(
            id, year, month, ragHistDesc, cancellationToken);

        return vm;
    }

    private async Task<MonthlyReportPreviousSubmission?> TryLoadPreviousMonthSubmissionAsync(
        int projectId,
        int year,
        int month,
        List<ProjectRagHistory> ragHistDesc,
        CancellationToken cancellationToken)
    {
        var prevDate = new DateTime(year, month, 1).AddMonths(-1);
        var prevYear = prevDate.Year;
        var prevMonth = prevDate.Month;

        var prevUpdate = await _context.ProjectMonthlyUpdates
            .AsNoTracking()
            .Include(m => m.MonthlyUpdateNarratives)
            .FirstOrDefaultAsync(
                m => m.ProjectId == projectId && m.Year == prevYear && m.Month == prevMonth && m.SubmittedAt != null,
                cancellationToken);
        if (prevUpdate == null)
            return null;

        string? prevSubmittedByName = prevUpdate.CreatedByName;
        if (string.IsNullOrEmpty(prevSubmittedByName) && prevUpdate.CreatedByUserId.HasValue)
        {
            var sub = await _context.Users.AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == prevUpdate.CreatedByUserId.Value, cancellationToken);
            prevSubmittedByName = sub?.Name ?? sub?.Email;
        }

        prevSubmittedByName ??= prevUpdate.CreatedByEmail ?? "Unknown";

        int? prevRagId = null;
        string? prevRagJustification = null;
        string? prevPathToGreen = null;

        if (prevUpdate.DraftRagStatusLookupId.HasValue)
        {
            prevRagId = prevUpdate.DraftRagStatusLookupId;
            prevRagJustification = prevUpdate.DraftRagJustification;
            prevPathToGreen = prevUpdate.DraftPathToGreen;
        }
        else if (prevUpdate.SubmittedAt.HasValue)
        {
            var ragAtSubmit = MonthlyUpdateSubmittedRagResolver.Resolve(ragHistDesc, prevUpdate.SubmittedAt.Value);
            if (ragAtSubmit != null)
            {
                prevRagId = ragAtSubmit.RagStatusLookupId;
                prevRagJustification = ragAtSubmit.Justification;
                prevPathToGreen = ragAtSubmit.PathToGreen;
            }
        }

        string? prevRagName = null;
        string? prevRagCssClass = null;
        if (prevRagId.HasValue)
        {
            var ragLookup = await _context.RagStatusLookups.AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == prevRagId.Value, cancellationToken);
            prevRagName = ragLookup?.Name;
            prevRagCssClass = ragLookup?.CssClass;
        }

        return new MonthlyReportPreviousSubmission
        {
            Year = prevYear,
            Month = prevMonth,
            SubmittedAt = prevUpdate.SubmittedAt,
            SubmittedByName = prevSubmittedByName,
            Narrative = ComposeMonthlyUpdateNarrativeForDisplay(prevUpdate),
            PeopleNarrative = prevUpdate.PeopleNarrative,
            PermFte = prevUpdate.MonthlyPermFte,
            MspFte = prevUpdate.MonthlyMspFte,
            RagName = prevRagName,
            RagCssClass = prevRagCssClass,
            RagJustification = prevRagJustification,
            PathToGreen = prevPathToGreen,
            IsGreenRag = MonthlyReportIsGreenRagName(prevRagName)
        };
    }

    private async Task<IActionResult> MonthlyReportViewResultAsync(
        MonthlyReportViewModel vm,
        CancellationToken cancellationToken)
    {
        var userEmail = User.Identity?.Name;
        if (string.IsNullOrEmpty(userEmail))
            return Unauthorized();

        var currentUser = await _context.Users
            .FirstOrDefaultAsync(u => u.Email.ToLower() == userEmail.ToLower(), cancellationToken);
        if (currentUser == null)
            return Unauthorized();

        var work = await _modernWork.PopulateWorkDetailAsync(
            this,
            vm.WorkItemId,
            currentUser,
            userEmail,
            tab: "updates",
            milestonestab: null,
            cancellationToken);
        if (work == null)
            return NotFound();

        ViewBag.WorkItem = work;
        ViewBag.WorkChromeSubPage = false;
        ViewBag.WorkChromeMinimalHeader = false;

        return View("~/Views/Modern/Work/MonthlyReport.cshtml", vm);
    }

    [HttpGet("detail/{id:int}")]
    public async Task<IActionResult> Detail(
        int id,
        string? tab = null,
        string? milestonestab = null,
        CancellationToken cancellationToken = default)
    {
        var scope = await ResolveViewScopeUserAsync(cancellationToken);
        if (scope == null)
            return Unauthorized();

        var (currentUser, userEmail) = scope.Value;
        var realUserEmail = User.Identity?.Name ?? userEmail;

        var work = await _modernWork.PopulateWorkDetailAsync(
            this,
            id,
            currentUser,
            userEmail,
            tab,
            milestonestab,
            cancellationToken);

        if (work == null)
            return NotFound();

        if (ViewBag.ShowFipsDatabaseServiceRegister as bool? == true)
        {
            ViewBag.WorkServiceRegisterLinkCount =
                await _workServiceRegisterLinks.CountLinksForWorkItemAsync(id, cancellationToken);
            ViewBag.CanLinkWorkServiceRegister =
                await _workServiceRegisterLinks.CanLinkFromWorkItemAsync(id, userEmail, cancellationToken);
            var canCreateServiceOffering =
                await _workServiceRegisterLinks.CanCreateServiceOfferingFromWorkItemAsync(id, userEmail, cancellationToken);
            var srLinks = await _workServiceRegisterLinks.GetLinksForWorkItemAsync(
                id,
                productId => Url.Action("FipsProduct", "ModernManage", new { id = productId }) ?? "#",
                cancellationToken);
            ViewBag.WorkServiceRegisterLinksPanel = new Compass.ViewModels.Modern.WorkServiceRegisterLinksPanelViewModel
            {
                WorkItemId = id,
                CanLink = ViewBag.CanLinkWorkServiceRegister as bool? == true,
                CanCreateServiceOffering = canCreateServiceOffering,
                Links = srLinks,
                PickProductsUrl = Url.Action(nameof(PickServiceRegisterProducts), new { id }) ?? "",
                LinkUrl = Url.Action(nameof(LinkServiceRegisterProduct), new { id }) ?? "",
            };
        }

        var showWorkHistory = !_viewAsUser.IsActive(HttpContext)
            && await _permissions.IsCentralOperationsAdminOrSuperAdminAsync(realUserEmail);
        ViewBag.ShowWorkHistoryNav = showWorkHistory;
        if (showWorkHistory)
        {
            ViewBag.WorkHistoryTimeline =
                await WorkItemAuditTimelineBuilder.BuildAsync(_context, id, cancellationToken);
        }

        return View("~/Views/Modern/Work/Detail.cshtml", work);
    }

    private async Task<IActionResult?> EnsureUserCanEditWorkAsync(int projectId, CancellationToken cancellationToken = default)
    {
        var userEmail = User.Identity?.Name;
        if (string.IsNullOrEmpty(userEmail))
            return Unauthorized();
        if (!await _modernWork.CanUserEditWorkItemAsync(projectId, userEmail, cancellationToken))
            return Forbid();
        return null;
    }

    private Task<bool> CanEditWorkPriorityAsync(string userEmail) =>
        _permissions.IsInGroupAsync(userEmail, CentralOperationsAdminGroupName);

    [HttpGet("{id:int}/strategic-alignment/edit")]
    [HttpGet("/ModernWork/EditStrategicAlignment/{id:int}")]
    public async Task<IActionResult> EditStrategicAlignment(int id, CancellationToken cancellationToken = default)
    {
        var userEmail = User.Identity?.Name;
        if (string.IsNullOrEmpty(userEmail))
            return Unauthorized();

        var currentUser = await _context.Users
            .FirstOrDefaultAsync(u => u.Email.ToLower() == userEmail.ToLower(), cancellationToken);
        if (currentUser == null)
            return Unauthorized();

        var deny = await EnsureUserCanEditWorkAsync(id, cancellationToken);
        if (deny != null)
            return deny;

        var work = await _modernWork.PopulateWorkDetailAsync(
            this, id, currentUser, userEmail, "strategicalignment", null, cancellationToken);
        if (work == null)
            return NotFound();

        ViewBag.WorkItem = work;
        ViewBag.WorkChromeSubPage = true;
        ViewBag.WorkChromeSection = "strategicalignment";
        ViewBag.RiskAppetiteOptions = await _context.RiskAppetiteLookups.AsNoTracking().Where(r => r.IsActive).OrderBy(r => r.SortOrder)
            .Select(r => new LookupOption { Id = r.Id, Name = r.Name ?? "", Value = r.Description ?? "" }).ToListAsync(cancellationToken);
        ViewBag.PriorityOutcomes = await _context.Objectives.AsNoTracking()
            .Where(o => !o.IsDeleted && o.Status == "active")
            .OrderBy(o => o.Title)
            .Select(o => new WorkLookupOption { Id = o.Id, Name = o.Title, Value = o.Title })
            .ToListAsync(cancellationToken);
        ViewBag.MissionPillars = await _context.Missions.AsNoTracking()
            .Where(m => !m.IsDeleted)
            .OrderBy(m => m.Title)
            .Select(m => new WorkLookupOption { Id = m.Id, Name = m.Title, Value = m.Title })
            .ToListAsync(cancellationToken);
        ViewBag.Directorates = await _context.Divisions.AsNoTracking().Where(d => d.IsActive).OrderBy(d => d.Name)
            .Select(d => new Directorate { Id = d.Id, Name = d.Name, IsActive = true }).ToListAsync(cancellationToken);
        ViewBag.WorkTagOptions = await _context.WorkItemTagLookups.AsNoTracking()
            .Where(t => t.IsActive)
            .OrderBy(t => t.SortOrder).ThenBy(t => t.Name)
            .Select(t => new LookupOption { Id = t.Id, Name = t.Name ?? "", Value = t.Description ?? "" })
            .ToListAsync(cancellationToken);
        ViewBag.SelectedWorkTagIds = work.Tags?.Select(t => t.Id).ToArray() ?? Array.Empty<int>();

        return View("~/Views/Modern/Work/EditStrategicAlignment.cshtml", work);
    }

    [HttpPost("{id:int}/strategic-alignment/edit")]
    [HttpPost("/ModernWork/EditStrategicAlignment/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditStrategicAlignment(
        int id,
        bool? subjectToSpendControl,
        int? riskAppetiteId,
        int[]? priorityOutcomeIds,
        int[]? missionPillarIds,
        int[]? directorateIds,
        int[]? workTagIds,
        CancellationToken cancellationToken = default)
    {
        var deny = await EnsureUserCanEditWorkAsync(id, cancellationToken);
        if (deny != null)
            return deny;

        priorityOutcomeIds ??= Array.Empty<int>();
        missionPillarIds ??= Array.Empty<int>();
        directorateIds ??= Array.Empty<int>();
        workTagIds ??= Array.Empty<int>();

        var project = await _context.Projects
            .Include(p => p.ProjectObjectives)
            .Include(p => p.ProjectMissions)
            .Include(p => p.Directorates)
            .Include(p => p.ProjectWorkItemTags)
            .FirstOrDefaultAsync(p => p.Id == id && !p.IsDeleted, cancellationToken);
        if (project == null)
            return NotFound();

        var now = DateTime.UtcNow;
        project.IsSubjectToSpendControl = subjectToSpendControl == true;
        project.RiskAppetiteLookupId = riskAppetiteId > 0 ? riskAppetiteId : null;
        project.UpdatedAt = now;

        _context.ProjectObjectives.RemoveRange(project.ProjectObjectives);
        foreach (var objectiveId in priorityOutcomeIds.Distinct())
        {
            _context.ProjectObjectives.Add(new ProjectObjective
            {
                ProjectId = id,
                ObjectiveId = objectiveId,
                CreatedAt = now
            });
        }

        _context.ProjectMissions.RemoveRange(project.ProjectMissions);
        foreach (var missionId in missionPillarIds.Distinct())
        {
            _context.ProjectMissions.Add(new ProjectMission
            {
                ProjectId = id,
                MissionId = missionId,
                CreatedAt = now
            });
        }

        _context.ProjectDirectorates.RemoveRange(project.Directorates);
        foreach (var divId in directorateIds.Distinct())
        {
            _context.ProjectDirectorates.Add(new ProjectDirectorate
            {
                ProjectId = id,
                DivisionId = divId,
                CreatedAt = now
            });
        }

        var validTagIds = await _context.WorkItemTagLookups.AsNoTracking()
            .Where(t => t.IsActive && workTagIds.Contains(t.Id))
            .Select(t => t.Id)
            .ToListAsync(cancellationToken);

        _context.ProjectWorkItemTags.RemoveRange(project.ProjectWorkItemTags);
        foreach (var tagId in validTagIds.Distinct())
        {
            _context.ProjectWorkItemTags.Add(new ProjectWorkItemTag
            {
                ProjectId = id,
                WorkItemTagLookupId = tagId
            });
        }

        await _context.SaveChangesAsync(cancellationToken);
        TempData["SuccessMessage"] = "Strategic alignment updated.";
        var url = Url.Action(nameof(Detail), new { id });
        return string.IsNullOrEmpty(url)
            ? RedirectToAction(nameof(Detail), new { id })
            : LocalRedirect(url + "#wd-strategic-alignment");
    }

    [HttpGet("{id:int}/tags/edit")]
    [HttpGet("/ModernWork/EditWorkTags/{id:int}")]
    public async Task<IActionResult> EditWorkTags(int id, CancellationToken cancellationToken = default)
    {
        var userEmail = User.Identity?.Name;
        if (string.IsNullOrEmpty(userEmail))
            return Unauthorized();

        var currentUser = await _context.Users
            .FirstOrDefaultAsync(u => u.Email.ToLower() == userEmail.ToLower(), cancellationToken);
        if (currentUser == null)
            return Unauthorized();

        var deny = await EnsureUserCanEditWorkAsync(id, cancellationToken);
        if (deny != null)
            return deny;

        var work = await _modernWork.PopulateWorkDetailAsync(
            this, id, currentUser, userEmail, "overview", null, cancellationToken);
        if (work == null)
            return NotFound();

        ViewBag.WorkTagOptions = await _context.WorkItemTagLookups.AsNoTracking()
            .Where(t => t.IsActive)
            .OrderBy(t => t.SortOrder).ThenBy(t => t.Name)
            .Select(t => new WorkLookupOption { Id = t.Id, Name = t.Name, Value = t.Description })
            .ToListAsync(cancellationToken);

        return View("~/Views/Modern/Work/EditWorkTags.cshtml", work);
    }

    [HttpPost("{id:int}/tags/edit")]
    [HttpPost("/ModernWork/EditWorkTags/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditWorkTags(int id, int[]? workTagIds, CancellationToken cancellationToken = default)
    {
        var deny = await EnsureUserCanEditWorkAsync(id, cancellationToken);
        if (deny != null)
            return deny;

        workTagIds ??= Array.Empty<int>();

        var project = await _context.Projects
            .Include(p => p.ProjectWorkItemTags)
            .FirstOrDefaultAsync(p => p.Id == id && !p.IsDeleted, cancellationToken);
        if (project == null)
            return NotFound();

        var now = DateTime.UtcNow;
        project.UpdatedAt = now;

        var validIds = await _context.WorkItemTagLookups.AsNoTracking()
            .Where(t => t.IsActive && workTagIds.Contains(t.Id))
            .Select(t => t.Id)
            .ToListAsync(cancellationToken);

        _context.ProjectWorkItemTags.RemoveRange(project.ProjectWorkItemTags);
        foreach (var tagId in validIds.Distinct())
        {
            _context.ProjectWorkItemTags.Add(new ProjectWorkItemTag
            {
                ProjectId = id,
                WorkItemTagLookupId = tagId
            });
        }

        await _context.SaveChangesAsync(cancellationToken);
        TempData["SuccessMessage"] = "Tags updated.";
        return RedirectToAction(nameof(Detail), new { id, tab = "overview" });
    }

    [HttpPost("{id:int}/overview/title")]
    [HttpPost("/ModernWork/UpdateWorkOverviewTitle/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateWorkOverviewTitle(int id, [FromForm] string? title, CancellationToken cancellationToken = default)
    {
        var deny = await EnsureUserCanEditWorkAsync(id, cancellationToken);
        if (deny != null)
            return deny;

        var t = (title ?? "").Trim();
        if (string.IsNullOrEmpty(t))
        {
            TempData["ErrorMessage"] = "Enter a title.";
            return RedirectToAction(nameof(Detail), new { id, tab = "overview" });
        }

        var project = await _context.Projects.FirstOrDefaultAsync(p => p.Id == id && !p.IsDeleted, cancellationToken);
        if (project == null)
            return NotFound();

        project.Title = t;
        project.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
        TempData["SuccessMessage"] = "Title updated.";
        return RedirectToAction(nameof(Detail), new { id, tab = "overview" });
    }

    [HttpPost("{id:int}/overview/problem-statement")]
    [HttpPost("/ModernWork/UpdateWorkOverviewProblemStatement/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateWorkOverviewProblemStatement(int id, [FromForm] string? problemStatement, CancellationToken cancellationToken = default)
    {
        var deny = await EnsureUserCanEditWorkAsync(id, cancellationToken);
        if (deny != null)
            return deny;

        var userEmail = User.Identity?.Name ?? "";
        var currentUser = await _context.Users
            .FirstOrDefaultAsync(u => u.Email.ToLower() == userEmail.ToLower(), cancellationToken);

        var text = (problemStatement ?? "").Trim();
        var project = await _context.Projects
            .Include(p => p.ProblemStatements)
            .FirstOrDefaultAsync(p => p.Id == id && !p.IsDeleted, cancellationToken);
        if (project == null)
            return NotFound();

        var now = DateTime.UtcNow;
        var latest = project.ProblemStatements.OrderByDescending(x => x.UpdatedAt).FirstOrDefault();
        if (latest != null)
        {
            latest.ProblemStatement = text;
            latest.UpdatedAt = now;
        }
        else
        {
            _context.ProjectProblemStatements.Add(new ProjectProblemStatement
            {
                ProjectId = id,
                ProblemStatement = text,
                CreatedByEmail = userEmail,
                CreatedByName = currentUser?.Name,
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        project.UpdatedAt = now;
        await _context.SaveChangesAsync(cancellationToken);
        TempData["SuccessMessage"] = "Problem statement updated.";
        return RedirectToAction(nameof(Detail), new { id, tab = "overview" });
    }

    [HttpPost("{id:int}/overview/aim")]
    [HttpPost("/ModernWork/UpdateWorkOverviewAim/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateWorkOverviewAim(int id, [FromForm] string? aim, CancellationToken cancellationToken = default)
    {
        var deny = await EnsureUserCanEditWorkAsync(id, cancellationToken);
        if (deny != null)
            return deny;

        var project = await _context.Projects.FirstOrDefaultAsync(p => p.Id == id && !p.IsDeleted, cancellationToken);
        if (project == null)
            return NotFound();

        project.Aim = string.IsNullOrWhiteSpace(aim) ? null : aim.Trim();
        project.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
        TempData["SuccessMessage"] = "Aim updated.";
        return RedirectToAction(nameof(Detail), new { id, tab = "overview" });
    }

    [HttpPost("{id:int}/overview/flagship")]
    [HttpPost("/ModernWork/UpdateWorkOverviewFlagship/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateWorkOverviewFlagship(int id, [FromForm] bool? flagshipProject, CancellationToken cancellationToken = default)
    {
        var deny = await EnsureUserCanEditWorkAsync(id, cancellationToken);
        if (deny != null)
            return deny;

        var project = await _context.Projects.FirstOrDefaultAsync(p => p.Id == id && !p.IsDeleted, cancellationToken);
        if (project == null)
            return NotFound();

        project.IsFlagship = flagshipProject == true;
        project.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
        TempData["SuccessMessage"] = "Flagship setting updated.";
        return RedirectToAction(nameof(Detail), new { id, tab = "overview" });
    }

    [HttpPost("{id:int}/overview/spend-control")]
    [HttpPost("/ModernWork/UpdateWorkOverviewSpendControl/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateWorkOverviewSpendControl(int id, [FromForm] bool? subjectToSpendControl, CancellationToken cancellationToken = default)
    {
        var deny = await EnsureUserCanEditWorkAsync(id, cancellationToken);
        if (deny != null)
            return deny;

        var project = await _context.Projects.FirstOrDefaultAsync(p => p.Id == id && !p.IsDeleted, cancellationToken);
        if (project == null)
            return NotFound();

        project.IsSubjectToSpendControl = subjectToSpendControl == true;
        project.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
        TempData["SuccessMessage"] = "Spend control setting updated.";
        return RedirectToAction(nameof(Detail), new { id, tab = "overview" });
    }

    [HttpPost("{id:int}/overview/risk-appetite")]
    [HttpPost("/ModernWork/UpdateWorkOverviewRiskAppetite/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateWorkOverviewRiskAppetite(int id, [FromForm] int? riskAppetiteId, CancellationToken cancellationToken = default)
    {
        var deny = await EnsureUserCanEditWorkAsync(id, cancellationToken);
        if (deny != null)
            return deny;

        var project = await _context.Projects.FirstOrDefaultAsync(p => p.Id == id && !p.IsDeleted, cancellationToken);
        if (project == null)
            return NotFound();

        project.RiskAppetiteLookupId = riskAppetiteId is > 0 ? riskAppetiteId : null;
        project.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
        TempData["SuccessMessage"] = "Risk appetite updated.";
        return RedirectToAction(nameof(Detail), new { id, tab = "overview" });
    }

    [HttpPost("{id:int}/overview/start-date")]
    [HttpPost("/ModernWork/UpdateWorkOverviewStartDate/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateWorkOverviewStartDate(int id, [FromForm] string? startDate, CancellationToken cancellationToken = default)
    {
        var deny = await EnsureUserCanEditWorkAsync(id, cancellationToken);
        if (deny != null)
            return deny;

        var project = await _context.Projects.FirstOrDefaultAsync(p => p.Id == id && !p.IsDeleted, cancellationToken);
        if (project == null)
            return NotFound();

        if (!string.IsNullOrWhiteSpace(startDate) && DateTime.TryParse(startDate, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var sd))
            project.StartDate = sd.Date;
        else
            project.StartDate = null;

        project.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
        TempData["SuccessMessage"] = "Start date updated.";
        return RedirectToAction(nameof(Detail), new { id, tab = "overview" });
    }

    [HttpPost("{id:int}/overview/target-end-date")]
    [HttpPost("/ModernWork/UpdateWorkOverviewTargetEndDate/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateWorkOverviewTargetEndDate(int id, [FromForm] string? targetEndDate, CancellationToken cancellationToken = default)
    {
        var deny = await EnsureUserCanEditWorkAsync(id, cancellationToken);
        if (deny != null)
            return deny;

        var project = await _context.Projects.FirstOrDefaultAsync(p => p.Id == id && !p.IsDeleted, cancellationToken);
        if (project == null)
            return NotFound();

        if (!string.IsNullOrWhiteSpace(targetEndDate) && DateTime.TryParse(targetEndDate, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var ed))
            project.TargetDeliveryDate = ed.Date;
        else
            project.TargetDeliveryDate = null;

        project.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
        TempData["SuccessMessage"] = "Target end date updated.";
        return RedirectToAction(nameof(Detail), new { id, tab = "overview" });
    }

    [HttpPost("{id:int}/watch")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Watch(int id, string? tab = null, string? milestonestab = null, CancellationToken cancellationToken = default)
    {
        var result = await SetWorkItemWatchingAsync(id, watching: true, cancellationToken);
        if (result.ErrorStatus != null)
            return result.ErrorStatus;

        return RedirectToAction(nameof(Detail), new { id, tab, milestonestab });
    }

    [HttpPost("{id:int}/unwatch")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Unwatch(int id, string? tab = null, string? milestonestab = null, CancellationToken cancellationToken = default)
    {
        var result = await SetWorkItemWatchingAsync(id, watching: false, cancellationToken);
        if (result.ErrorStatus != null)
            return result.ErrorStatus;

        return RedirectToAction(nameof(Detail), new { id, tab, milestonestab });
    }

    [HttpPost("{id:int}/watch/toggle")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleWatch(int id, CancellationToken cancellationToken = default)
    {
        var result = await ToggleWorkItemWatchingAsync(id, cancellationToken);
        if (result.ErrorStatus != null)
            return result.ErrorStatus;

        return Json(new { watching = result.Watching });
    }

    private async Task<(bool Watching, IActionResult? ErrorStatus)> ToggleWorkItemWatchingAsync(
        int projectId,
        CancellationToken cancellationToken)
    {
        var currentUser = await GetCurrentUserAsync(cancellationToken);
        if (currentUser == null)
            return (false, Unauthorized());

        var projectExists = await _context.Projects.AsNoTracking()
            .AnyAsync(p => p.Id == projectId && !p.IsDeleted, cancellationToken);
        if (!projectExists)
            return (false, NotFound());

        var existing = await _context.ProjectWatchlists
            .FirstOrDefaultAsync(w => w.UserId == currentUser.Id && w.ProjectId == projectId, cancellationToken);

        if (existing != null)
        {
            _context.ProjectWatchlists.Remove(existing);
            await _context.SaveChangesAsync(cancellationToken);
            return (false, null);
        }

        _context.ProjectWatchlists.Add(new ProjectWatchlist
        {
            UserId = currentUser.Id,
            ProjectId = projectId,
            CreatedAt = DateTime.UtcNow
        });
        await _context.SaveChangesAsync(cancellationToken);
        return (true, null);
    }

    private async Task<(bool Watching, IActionResult? ErrorStatus)> SetWorkItemWatchingAsync(
        int projectId,
        bool watching,
        CancellationToken cancellationToken)
    {
        var currentUser = await GetCurrentUserAsync(cancellationToken);
        if (currentUser == null)
            return (false, Unauthorized());

        var projectExists = await _context.Projects.AsNoTracking()
            .AnyAsync(p => p.Id == projectId && !p.IsDeleted, cancellationToken);
        if (!projectExists)
            return (false, NotFound());

        var existing = await _context.ProjectWatchlists
            .FirstOrDefaultAsync(w => w.UserId == currentUser.Id && w.ProjectId == projectId, cancellationToken);

        if (watching)
        {
            if (existing == null)
            {
                _context.ProjectWatchlists.Add(new ProjectWatchlist
                {
                    UserId = currentUser.Id,
                    ProjectId = projectId,
                    CreatedAt = DateTime.UtcNow
                });
                await _context.SaveChangesAsync(cancellationToken);
            }

            return (true, null);
        }

        if (existing != null)
        {
            _context.ProjectWatchlists.Remove(existing);
            await _context.SaveChangesAsync(cancellationToken);
        }

        return (false, null);
    }

    private async Task<User?> GetCurrentUserAsync(CancellationToken cancellationToken = default)
    {
        var userEmail = User.Identity?.Name;
        if (string.IsNullOrEmpty(userEmail))
            return null;
        return await _context.Users
            .FirstOrDefaultAsync(u => u.Email.ToLower() == userEmail.ToLower(), cancellationToken);
    }

    /// <summary>Effective user for read/scoped views — view-as target when active, otherwise signed-in user.</summary>
    private async Task<(User User, string Email)?> ResolveViewScopeUserAsync(CancellationToken cancellationToken = default)
    {
        if (_viewAsUser.IsActive(HttpContext))
        {
            var active = _viewAsUser.GetActive(HttpContext);
            if (active != null)
            {
                var viewAsUser = await _context.Users
                    .FirstOrDefaultAsync(u => u.Id == active.UserId, cancellationToken);
                if (viewAsUser != null)
                    return (viewAsUser, viewAsUser.Email);
            }
        }

        var userEmail = User.Identity?.Name;
        if (string.IsNullOrEmpty(userEmail))
            return null;

        var currentUser = await _context.Users
            .FirstOrDefaultAsync(u => u.Email.ToLower() == userEmail.ToLower(), cancellationToken);
        if (currentUser == null)
            return null;

        return (currentUser, userEmail);
    }

    private async Task<WorkRegisterSubNavViewModel?> BuildWorkRegisterSubNavForScopeAsync(
        int? businessAreaId,
        int? directorateId,
        string? search,
        int? ragId,
        int? priorityId,
        string activeTab,
        string listAction,
        string? businessAreaFilterKey = null,
        string? directorateFilterKey = null,
        int[]? tagIds = null,
        string? themeFilterKey = null,
        CancellationToken cancellationToken = default)
    {
        var scope = await ResolveViewScopeUserAsync(cancellationToken);
        if (scope == null)
            return null;

        var (currentUser, userEmail) = scope.Value;

        var tabKey = NormalizeWorkRegisterTab(activeTab);

        var vm = await _modernWork.BuildWorkRegisterAsync(
            isMyWork: false,
            search,
            portfolioId: null,
            directorateId,
            phaseId: null,
            ragId,
            priorityId,
            monthlyUpdate: null,
            currentUser,
            userEmail,
            Url,
            registerTab: tabKey,
            registerPage: 1,
            registerPageSize: 20,
            businessAreaId,
            tagIds: tagIds,
            cancellationToken: cancellationToken);

        return WorkRegisterSubNavViewModel.FromRegister(
            vm,
            tabKey,
            isMyWork: false,
            listAction,
            businessAreaFilterKey,
            directorateFilterKey,
            themeFilterKey);
    }

    [HttpGet("{id:int}/milestone/add")]
    [HttpGet("/ModernWork/AddMilestone/{id:int}")]
    public async Task<IActionResult> AddMilestone(int id, CancellationToken cancellationToken = default)
    {
        ViewBag.MainNavSection = "work";

        var userEmail = User.Identity?.Name;
        if (string.IsNullOrEmpty(userEmail))
            return Unauthorized();

        var currentUser = await _context.Users
            .FirstOrDefaultAsync(u => u.Email.ToLower() == userEmail.ToLower(), cancellationToken);
        if (currentUser == null)
            return Unauthorized();

        var work = await _modernWork.PopulateWorkDetailAsync(
            this, id, currentUser, userEmail, "milestones", null, cancellationToken);
        if (work == null)
            return NotFound();

        ViewBag.WorkItem = work;
        ViewBag.WorkChromeSubPage = true;

        var milestone = new Milestone
        {
            ProjectId = id,
            DueDate = DateTime.UtcNow.Date,
            Status = "not_started"
        };

        return View("~/Views/Modern/Work/AddMilestone.cshtml", milestone);
    }

    /// <summary>Edit milestone (modern UI). Canonical path <c>/modern/work/{id}/milestone/{milestoneId}/edit</c>.</summary>
    [HttpGet("{id:int}/milestone/{milestoneId:int}/edit")]
    [HttpGet("/ModernWork/EditMilestone/{id:int}")]
    public async Task<IActionResult> EditMilestone(int id, int milestoneId, CancellationToken cancellationToken = default)
    {
        ViewBag.MainNavSection = "work";

        var userEmail = User.Identity?.Name;
        if (string.IsNullOrEmpty(userEmail))
            return Unauthorized();

        var currentUser = await _context.Users
            .FirstOrDefaultAsync(u => u.Email.ToLower() == userEmail.ToLower(), cancellationToken);
        if (currentUser == null)
            return Unauthorized();

        var milestone = await _context.Milestones
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == milestoneId && m.ProjectId == id && !m.IsDeleted, cancellationToken);
        if (milestone == null)
            return NotFound();

        var work = await _modernWork.PopulateWorkDetailAsync(
            this, id, currentUser, userEmail, "milestones", null, cancellationToken);
        if (work == null)
            return NotFound();

        ViewBag.WorkItem = work;
        ViewBag.WorkChromeSubPage = true;
        await LoadMilestoneUpdatesForViewAsync(milestoneId, cancellationToken);

        return View("~/Views/Modern/Work/EditMilestone.cshtml", milestone);
    }

    [HttpPost("{id:int}/milestone/{milestoneId:int}/progress-update")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddMilestoneUpdate(
        int id,
        int milestoneId,
        [FromForm] string updateDetails,
        [FromForm] string? newStatus,
        [FromForm] int? newProgress,
        CancellationToken cancellationToken = default)
    {
        var milestone = await _context.Milestones
            .FirstOrDefaultAsync(m => m.Id == milestoneId && m.ProjectId == id && !m.IsDeleted, cancellationToken);
        if (milestone == null)
            return NotFound();

        updateDetails = updateDetails?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(updateDetails))
        {
            TempData["ErrorMessage"] = "Enter progress update details.";
            return RedirectToAction(nameof(EditMilestone), new { id, milestoneId });
        }

        newStatus = newStatus?.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(newStatus) && !ValidMilestoneStatuses.Contains(newStatus))
        {
            TempData["ErrorMessage"] = "Select a valid status for the progress update.";
            return RedirectToAction(nameof(EditMilestone), new { id, milestoneId });
        }

        if (newProgress is < 0 or > 100)
        {
            TempData["ErrorMessage"] = "Progress must be between 0 and 100.";
            return RedirectToAction(nameof(EditMilestone), new { id, milestoneId });
        }

        var userEmail = User.Identity?.Name ?? "system@example.com";
        var userName = User.Claims.FirstOrDefault(c => c.Type == "name")?.Value;

        var milestoneUpdate = new MilestoneUpdate
        {
            MilestoneId = milestoneId,
            UpdateDetails = updateDetails,
            PreviousStatus = milestone.Status,
            NewStatus = !string.IsNullOrEmpty(newStatus) ? newStatus : null,
            PreviousProgress = milestone.ProgressPercent,
            NewProgress = newProgress,
            UpdatedByEmail = userEmail,
            UpdatedByName = userName,
            UpdatedAt = DateTime.UtcNow
        };

        _context.MilestoneUpdates.Add(milestoneUpdate);

        if (!string.IsNullOrEmpty(newStatus))
            milestone.Status = newStatus;

        if (newProgress.HasValue)
            milestone.ProgressPercent = newProgress;

        milestone.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);

        TempData["SuccessMessage"] = "Progress update added.";
        return RedirectToAction(nameof(EditMilestone), new { id, milestoneId });
    }

    [HttpPost("{id:int}/milestone/{milestoneId:int}/update")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateMilestone(
        int id,
        int milestoneId,
        [FromForm] string name,
        [FromForm] string? Description,
        [FromForm] DateTime dueDate,
        [FromForm] string status,
        CancellationToken cancellationToken = default)
    {
        var milestone = await _context.Milestones
            .FirstOrDefaultAsync(m => m.Id == milestoneId && m.ProjectId == id && !m.IsDeleted, cancellationToken);
        if (milestone == null)
            return NotFound();

        name = name?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
            ModelState.AddModelError("name", "Enter a milestone name.");

        status = status?.Trim().ToLowerInvariant() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(status) || !ValidMilestoneStatuses.Contains(status))
            ModelState.AddModelError("status", "Select a valid status.");

        if (!ModelState.IsValid)
        {
            milestone.Name = name;
            milestone.Description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim();
            milestone.DueDate = dueDate.Date;
            milestone.Status = status;
            if (!await TryPopulateEditMilestoneViewBagsAsync(id, cancellationToken))
                return NotFound();
            await LoadMilestoneUpdatesForViewAsync(milestoneId, cancellationToken);
            return View("~/Views/Modern/Work/EditMilestone.cshtml", milestone);
        }

        milestone.Name = name;
        milestone.Description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim();
        milestone.DueDate = dueDate.Date;
        milestone.Status = status;
        milestone.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);
        TempData["SuccessMessage"] = "Milestone updated successfully.";
        return RedirectToAction(nameof(Detail), new { id, tab = "milestones" });
    }

    [HttpPost("{id:int}/milestone/{milestoneId:int}/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteMilestone(int id, int milestoneId, CancellationToken cancellationToken = default)
    {
        try
        {
            var milestone = await _context.Milestones
                .FirstOrDefaultAsync(m => m.Id == milestoneId && m.ProjectId == id, cancellationToken);

            if (milestone == null)
                return NotFound();

            milestone.IsDeleted = true;
            milestone.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(cancellationToken);

            TempData["SuccessMessage"] = "Milestone deleted successfully.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting milestone {MilestoneId}", milestoneId);
            TempData["ErrorMessage"] = "Error deleting milestone. Please try again.";
        }

        return RedirectToAction(nameof(Detail), new { id, tab = "milestones" });
    }

    private async Task LoadMilestoneUpdatesForViewAsync(int milestoneId, CancellationToken cancellationToken)
    {
        var updates = await _context.MilestoneUpdates
            .AsNoTracking()
            .Where(u => u.MilestoneId == milestoneId)
            .OrderByDescending(u => u.UpdatedAt)
            .Take(10)
            .ToListAsync(cancellationToken);
        ViewBag.MilestoneUpdates = updates;
    }

    private async Task<bool> TryPopulateEditMilestoneViewBagsAsync(int id, CancellationToken cancellationToken = default)
    {
        var userEmail = User.Identity?.Name;
        if (string.IsNullOrEmpty(userEmail))
            return false;

        var currentUser = await _context.Users
            .FirstOrDefaultAsync(u => u.Email.ToLower() == userEmail.ToLower(), cancellationToken);
        if (currentUser == null)
            return false;

        var work = await _modernWork.PopulateWorkDetailAsync(
            this, id, currentUser, userEmail, "milestones", null, cancellationToken);
        if (work == null)
            return false;

        ViewBag.WorkItem = work;
        ViewBag.WorkChromeSubPage = true;
        return true;
    }

    [HttpGet("{id:int}/add-team-member")]
    [HttpGet("/ModernWork/AddTeamMember/{id:int}")]
    public async Task<IActionResult> AddTeamMember(int id, CancellationToken cancellationToken = default)
    {
        var prep = await PrepareAddTeamMemberPageAsync(id, cancellationToken);
        if (prep != null)
            return prep;

        var model = new WorkItemTeamMember { WorkItemId = id, TeamStatus = "active" };
        return View("~/Views/Modern/Work/AddTeamMember.cshtml", model);
    }

    [HttpPost("{id:int}/add-team-member")]
    [HttpPost("/ModernWork/AddTeamMember/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddTeamMember(int id, [FromForm] WorkItemTeamMember model, CancellationToken cancellationToken = default)
    {
        model.WorkItemId = id;

        var prep = await PrepareAddTeamMemberPageAsync(id, cancellationToken);
        if (prep != null)
            return prep;

        if (model.AppUserId <= 0)
            ModelState.AddModelError(nameof(WorkItemTeamMember.AppUserId), "Select a team member.");
        if (string.IsNullOrWhiteSpace(model.Role))
            ModelState.AddModelError(nameof(WorkItemTeamMember.Role), "Select or enter a role.");

        if (!ModelState.IsValid)
        {
            await PopulateUserPickerForTeamMemberAsync(model.AppUserId, cancellationToken);
            return View("~/Views/Modern/Work/AddTeamMember.cshtml", model);
        }

        var project = await _context.Projects
            .Include(p => p.ProjectContacts)
            .FirstOrDefaultAsync(p => p.Id == id && !p.IsDeleted, cancellationToken);
        if (project == null)
            return NotFound();

        var user = await _context.Users.FindAsync(model.AppUserId);
        if (user == null)
        {
            ModelState.AddModelError(nameof(WorkItemTeamMember.AppUserId), "Selected person could not be found.");
            await PopulateUserPickerForTeamMemberAsync(model.AppUserId, cancellationToken);
            return View("~/Views/Modern/Work/AddTeamMember.cshtml", model);
        }

        if (string.IsNullOrWhiteSpace(user.Email))
        {
            ModelState.AddModelError(nameof(WorkItemTeamMember.AppUserId), "Selected person does not have an email address.");
            await PopulateUserPickerForTeamMemberAsync(model.AppUserId, cancellationToken);
            return View("~/Views/Modern/Work/AddTeamMember.cshtml", model);
        }

        var already = await _context.ProjectContacts
            .AnyAsync(pc => pc.ProjectId == id && pc.UserId == model.AppUserId, cancellationToken);
        if (already)
        {
            ModelState.AddModelError(nameof(WorkItemTeamMember.AppUserId), "This person is already on the team.");
            await PopulateUserPickerForTeamMemberAsync(model.AppUserId, cancellationToken);
            return View("~/Views/Modern/Work/AddTeamMember.cshtml", model);
        }

        var fundingArrangement = model.FundingOptionId == 2 ? "Programme" : "Admin";
        var employmentType = model.TypeOptionId == 2 ? "MSP" : "Permanent";
        var timeAllocation = model.TimeAllocationPct.HasValue ? $"{model.TimeAllocationPct.Value}%" : null;
        var teamStatusLegacy = string.Equals(model.TeamStatus, "inactive", StringComparison.OrdinalIgnoreCase) ? "previous" : "current";
        var leaveReason = teamStatusLegacy == "previous" ? "Inactive" : null;
        var leftAt = teamStatusLegacy == "previous" ? DateTime.UtcNow : (DateTime?)null;

        var normalizedName = !string.IsNullOrWhiteSpace(user.Name) ? user.Name.Trim() : user.Email.Trim();
        var contactEmail = user.Email.Trim();

        var teamMember = new ProjectContact
        {
            ProjectId = id,
            UserId = user.Id,
            Name = normalizedName,
            Email = contactEmail,
            Role = model.Role!.Trim(),
            FundingArrangement = fundingArrangement,
            TimeAllocation = timeAllocation,
            EmploymentType = employmentType,
            TeamStatus = teamStatusLegacy,
            LeaveReason = leaveReason,
            LeftAt = leftAt,
            SortOrder = project.ProjectContacts.Count + 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _context.ProjectContacts.Add(teamMember);
        await _context.SaveChangesAsync(cancellationToken);

        try
        {
            var templateVariables = new Dictionary<string, object>
            {
                { "project_title", project.Title },
                { "project_code", project.ProjectCode ?? string.Empty },
                { "user_name", normalizedName },
                { "user_email", contactEmail },
                { "role", model.Role.Trim() }
            };
            await _notificationRuleService.ProcessNotificationTriggerAsync(
                "team_member_added",
                projectId: id,
                userId: user.Id,
                templateVariables: templateVariables);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send team member added notification");
        }

        TempData["SuccessMessage"] = "Team member added successfully.";
        return Redirect((Url.Action(nameof(Detail), new { id }) ?? "") + "#wd-overview");
    }

    [HttpGet("{id:int}/dependency/add")]
    [HttpGet("/ModernWork/AddDependency/{id:int}")]
    public async Task<IActionResult> AddDependency(int id, CancellationToken cancellationToken = default)
    {
        var deny = await EnsureUserCanEditWorkAsync(id, cancellationToken);
        if (deny != null)
            return deny;

        var prep = await PrepareAddDependencyPageAsync(id, cancellationToken);
        if (prep != null)
            return prep;

        var model = new WorkItemDependency { WorkItemId = id, Direction = "In", IsInternal = true };
        return View("~/Views/Modern/Work/AddDependency.cshtml", model);
    }

    [HttpPost("{id:int}/dependency/add")]
    [HttpPost("/ModernWork/AddDependency/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddDependency(
        int id,
        [FromForm] string? Direction,
        [FromForm] string? IsInternal,
        [FromForm] int? TargetWorkItemId,
        [FromForm] string? ExternalDescription,
        CancellationToken cancellationToken = default)
    {
        var deny = await EnsureUserCanEditWorkAsync(id, cancellationToken);
        if (deny != null)
            return deny;

        var prep = await PrepareAddDependencyPageAsync(id, cancellationToken);
        if (prep != null)
            return prep;

        var internalDep = string.Equals(IsInternal, "true", StringComparison.OrdinalIgnoreCase);
        var dir = (Direction ?? "In").Trim();
        if (!string.Equals(dir, "In", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(dir, "Out", StringComparison.OrdinalIgnoreCase))
        {
            dir = "In";
        }

        var extDesc = (ExternalDescription ?? "").Trim();

        if (internalDep)
        {
            if (!TargetWorkItemId.HasValue || TargetWorkItemId.Value <= 0)
            {
                ModelState.AddModelError(nameof(TargetWorkItemId), "Select a work item.");
            }
            else if (TargetWorkItemId.Value == id)
            {
                ModelState.AddModelError(nameof(TargetWorkItemId), "A work item cannot depend on itself.");
            }
            else if (!await _context.Projects.AsNoTracking()
                         .AnyAsync(p => p.Id == TargetWorkItemId.Value && !p.IsDeleted, cancellationToken))
            {
                ModelState.AddModelError(nameof(TargetWorkItemId), "Select a valid work item.");
            }
        }
        else if (string.IsNullOrEmpty(extDesc))
        {
            ModelState.AddModelError(nameof(ExternalDescription), "Enter a description for the external dependency.");
        }

        if (!ModelState.IsValid)
        {
            var invalid = new WorkItemDependency
            {
                WorkItemId = id,
                Direction = dir,
                IsInternal = internalDep,
                ExternalDescription = extDesc
            };
            ViewBag.PostedTargetWorkItemId = TargetWorkItemId;
            return View("~/Views/Modern/Work/AddDependency.cshtml", invalid);
        }

        string srcType;
        int srcId;
        string tgtType;
        int tgtId;
        string? desc = null;

        if (internalDep)
        {
            var otherId = TargetWorkItemId!.Value;
            if (string.Equals(dir, "Out", StringComparison.OrdinalIgnoreCase))
            {
                srcType = "Project";
                srcId = otherId;
                tgtType = "Project";
                tgtId = id;
            }
            else
            {
                srcType = "Project";
                srcId = id;
                tgtType = "Project";
                tgtId = otherId;
            }
        }
        else if (string.Equals(dir, "Out", StringComparison.OrdinalIgnoreCase))
        {
            srcType = "External";
            srcId = 0;
            tgtType = "Project";
            tgtId = id;
            desc = extDesc;
        }
        else
        {
            srcType = "Project";
            srcId = id;
            tgtType = "External";
            tgtId = 0;
            desc = extDesc;
        }

        var duplicate = await _context.Dependencies.AnyAsync(d =>
            d.SourceEntityType == srcType &&
            d.SourceEntityId == srcId &&
            d.TargetEntityType == tgtType &&
            d.TargetEntityId == tgtId, cancellationToken);
        if (duplicate)
        {
            ModelState.AddModelError(string.Empty, "This dependency relationship already exists.");
            var dupModel = new WorkItemDependency
            {
                WorkItemId = id,
                Direction = dir,
                IsInternal = internalDep,
                ExternalDescription = extDesc
            };
            ViewBag.PostedTargetWorkItemId = TargetWorkItemId;
            return View("~/Views/Modern/Work/AddDependency.cshtml", dupModel);
        }

        var row = new Dependency
        {
            SourceEntityType = srcType,
            SourceEntityId = srcId,
            TargetEntityType = tgtType,
            TargetEntityId = tgtId,
            DependencyType = "Related",
            Description = desc,
            Status = "Active",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _context.Dependencies.Add(row);
        await _context.SaveChangesAsync(cancellationToken);

        TempData["SuccessMessage"] = "Dependency added.";
        return Redirect((Url.Action(nameof(Detail), new { id, tab = "dependencies" }) ?? "") + "#wd-dependencies");
    }

    [HttpPost("{id:int}/dependency/remove")]
    [HttpPost("/ModernWork/RemoveDependency/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveDependency(int id, [FromForm] int dependencyId, CancellationToken cancellationToken = default)
    {
        var deny = await EnsureUserCanEditWorkAsync(id, cancellationToken);
        if (deny != null)
            return deny;

        var dep = await _context.Dependencies
            .FirstOrDefaultAsync(d => d.Id == dependencyId, cancellationToken);
        if (dep == null)
            return NotFound();

        var involves = (string.Equals(dep.SourceEntityType, "Project", StringComparison.OrdinalIgnoreCase) &&
                        dep.SourceEntityId == id) ||
                       (string.Equals(dep.TargetEntityType, "Project", StringComparison.OrdinalIgnoreCase) &&
                        dep.TargetEntityId == id);
        if (!involves)
            return NotFound();

        _context.Dependencies.Remove(dep);
        await _context.SaveChangesAsync(cancellationToken);

        TempData["SuccessMessage"] = "Dependency removed.";
        return Redirect((Url.Action(nameof(Detail), new { id, tab = "dependencies" }) ?? "") + "#wd-dependencies");
    }

    private async Task<IActionResult?> PrepareAddDependencyPageAsync(int id, CancellationToken cancellationToken)
    {
        ViewBag.MainNavSection = "work";

        var userEmail = User.Identity?.Name;
        if (string.IsNullOrEmpty(userEmail))
            return Unauthorized();

        var currentUser = await _context.Users
            .FirstOrDefaultAsync(u => u.Email.ToLower() == userEmail.ToLower(), cancellationToken);
        if (currentUser == null)
            return Unauthorized();

        var projectExists = await _context.Projects.AsNoTracking()
            .AnyAsync(p => p.Id == id && !p.IsDeleted, cancellationToken);
        if (!projectExists)
            return NotFound();

        var work = await _modernWork.GetWorkItemAsync(id);
        if (work == null)
            return NotFound();

        var project = await _context.Projects.AsNoTracking()
            .Include(p => p.PrimaryOrganizationalGroup)
            .Include(p => p.BusinessAreaLookup)
            .Include(p => p.PhaseLookup)
            .Include(p => p.DeliveryPriority)
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

        var others = await _context.Projects.AsNoTracking()
            .Where(p => !p.IsDeleted && p.Id != id)
            .OrderBy(p => p.Title)
            .Select(p => new { p.Id, p.Title })
            .ToListAsync(cancellationToken);
        ViewBag.WorkItemsSelectList = new SelectList(others, "Id", "Title");

        ViewBag.WorkItem = work;
        ViewBag.WorkChromeSection = "dependencies";
        ViewBag.WorkChromeSubPage = true;
        ViewBag.WorkChromeTabsAsLinks = true;
        ViewBag.WorkIdShort = work.Id.ToString("X8").ToUpperInvariant();
        if (project != null)
        {
            ViewBag.PortfolioName = ModernWorkService.ResolveProjectBusinessAreaDisplayName(project);
            ViewBag.DeliveryPhaseName = project.PhaseLookup?.Name;
            ViewBag.PriorityName = project.DeliveryPriority?.Name;
        }

        return null;
    }

    private async Task PopulateUserPickerForTeamMemberAsync(int appUserId, CancellationToken cancellationToken)
    {
        if (appUserId <= 0)
            return;
        var u = await _context.Users.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == appUserId, cancellationToken);
        if (u != null)
        {
            ViewBag.OwnerName = u.Name;
            ViewBag.OwnerEmail = u.Email;
        }
    }

    private async Task<IActionResult?> PrepareAddTeamMemberPageAsync(int id, CancellationToken cancellationToken)
    {
        ViewBag.MainNavSection = "work";

        var userEmail = User.Identity?.Name;
        if (string.IsNullOrEmpty(userEmail))
            return Unauthorized();

        var currentUser = await _context.Users
            .FirstOrDefaultAsync(u => u.Email.ToLower() == userEmail.ToLower(), cancellationToken);
        if (currentUser == null)
            return Unauthorized();

        var projectExists = await _context.Projects.AsNoTracking()
            .AnyAsync(p => p.Id == id && !p.IsDeleted, cancellationToken);
        if (!projectExists)
            return NotFound();

        var work = await _modernWork.GetWorkItemAsync(id);
        if (work == null)
            return NotFound();

        var project = await _context.Projects.AsNoTracking()
            .Include(p => p.PrimaryOrganizationalGroup)
            .Include(p => p.BusinessAreaLookup)
            .Include(p => p.PhaseLookup)
            .Include(p => p.DeliveryPriority)
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

        ViewBag.WorkItem = work;
        ViewBag.WorkChromeSection = "overview";
        ViewBag.WorkChromeSubPage = true;
        ViewBag.WorkChromeTabsAsLinks = true;
        ViewBag.WorkIdShort = work.Id.ToString("X8").ToUpperInvariant();
        if (project != null)
        {
            ViewBag.PortfolioName = ModernWorkService.ResolveProjectBusinessAreaDisplayName(project);
            ViewBag.DeliveryPhaseName = project.PhaseLookup?.Name;
            ViewBag.PriorityName = project.DeliveryPriority?.Name;
        }

        ViewBag.TeamMemberFundingOptions = new List<LookupOption>
        {
            new() { Id = 1, Name = "Admin", Value = "Admin" },
            new() { Id = 2, Name = "Programme", Value = "Programme" }
        };
        ViewBag.TeamMemberTypeOptions = new List<LookupOption>
        {
            new() { Id = 1, Name = "Permanent", Value = "Permanent" },
            new() { Id = 2, Name = "MSP", Value = "MSP" }
        };

        var roleNames = await _context.ProjectContacts.AsNoTracking()
            .Where(pc => !string.IsNullOrWhiteSpace(pc.Role))
            .Select(pc => pc.Role!.Trim())
            .Distinct()
            .OrderBy(r => r)
            .Take(50)
            .ToListAsync(cancellationToken);
        foreach (var d in new[] { "Delivery manager", "Product manager", "Technical lead" }.Reverse())
        {
            if (!roleNames.Any(x => string.Equals(x, d, StringComparison.OrdinalIgnoreCase)))
                roleNames.Insert(0, d);
        }

        ViewBag.TeamMemberRoleOptions = roleNames
            .Select((name, i) => new LookupOption { Id = i + 1, Name = name, Value = name })
            .ToList();

        return null;
    }

    [HttpGet("{id:int}/edit")]
    [HttpGet("/ModernWork/Edit/{id:int}")]
    public async Task<IActionResult> Edit(int id, CancellationToken cancellationToken = default)
    {
        var userEmail = User.Identity?.Name;
        if (string.IsNullOrEmpty(userEmail))
            return Unauthorized();

        var currentUser = await _context.Users
            .FirstOrDefaultAsync(u => u.Email.ToLower() == userEmail.ToLower(), cancellationToken);
        if (currentUser == null)
            return Unauthorized();

        var deny = await EnsureUserCanEditWorkAsync(id, cancellationToken);
        if (deny != null)
            return deny;

        var work = await _modernWork.PopulateWorkDetailAsync(
            this, id, currentUser, userEmail, "overview", null, cancellationToken);
        if (work == null)
            return NotFound();

        await PopulateWorkCreateViewBagAsync(null, cancellationToken);
        ViewBag.WorkChromeSubPage = true;
        ViewBag.CanEditPriority = await CanEditWorkPriorityAsync(userEmail);
        ViewBag.SelectedDirectorateIds = work.Directorates?.Select(d => d.DirectorateId).Distinct().ToArray() ?? Array.Empty<int>();
        ViewBag.SelectedWorkTagIds = work.Tags?.Select(t => t.Id).ToArray() ?? Array.Empty<int>();

        return View("~/Views/Modern/Work/Edit.cshtml", work);
    }

    [HttpPost("{id:int}/edit")]
    [HttpPost("/ModernWork/Edit/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(
        int id,
        WorkItem model,
        int[]? directorateIds,
        int[]? workTagIds,
        int? startDay,
        int? startMonth,
        int? startYear,
        int? targetEndDay,
        int? targetEndMonth,
        int? targetEndYear,
        CancellationToken cancellationToken = default)
    {
        var userEmail = User.Identity?.Name;
        if (string.IsNullOrEmpty(userEmail))
            return Unauthorized();

        var currentUser = await _context.Users
            .FirstOrDefaultAsync(u => u.Email.ToLower() == userEmail.ToLower(), cancellationToken);
        if (currentUser == null)
            return Unauthorized();

        var deny = await EnsureUserCanEditWorkAsync(id, cancellationToken);
        if (deny != null)
            return deny;

        var canEditPriority = await CanEditWorkPriorityAsync(userEmail);

        directorateIds ??= Array.Empty<int>();
        workTagIds ??= Array.Empty<int>();

        GovUkDateBinding.BindGovUkDate(ModelState, nameof(model.StartDate), startDay, startMonth, startYear, required: false, out var startUtc);
        if (!ModelState.ContainsKey(nameof(model.StartDate)))
            model.StartDate = startUtc;

        GovUkDateBinding.BindGovUkDate(ModelState, nameof(model.TargetEndDate), targetEndDay, targetEndMonth, targetEndYear, required: false, out var targetUtc);
        if (!ModelState.ContainsKey(nameof(model.TargetEndDate)))
            model.TargetEndDate = targetUtc;

        if (string.IsNullOrWhiteSpace(model.Title))
            ModelState.AddModelError(nameof(model.Title), "Enter a title.");

        if (!ModelState.IsValid)
        {
            model.Id = id;
            await PopulateWorkCreateViewBagAsync(null, cancellationToken);
            ViewBag.WorkChromeSubPage = true;
            ViewBag.CanEditPriority = canEditPriority;
            ViewBag.SelectedDirectorateIds = directorateIds;
            ViewBag.SelectedWorkTagIds = workTagIds;

            var chrome = await _modernWork.PopulateWorkDetailAsync(
                this, id, currentUser, userEmail, "overview", null, cancellationToken);
            ViewBag.WorkItem = chrome;

            return View("~/Views/Modern/Work/Edit.cshtml", model);
        }

        var project = await _context.Projects
            .Include(p => p.ProblemStatements)
            .Include(p => p.Directorates)
            .Include(p => p.ProjectWorkItemTags)
            .FirstOrDefaultAsync(p => p.Id == id && !p.IsDeleted, cancellationToken);
        if (project == null)
            return NotFound();

        var now = DateTime.UtcNow;
        var oldPriorityId = project.DeliveryPriorityId;

        project.Title = model.Title.Trim();
        project.Aim = model.Aim?.Trim();
        project.Status = model.Status?.Trim() ?? project.Status;

        var (portfolioOk, portfolioError) = await ApplyPortfolioSelectionAsync(
            project, model.PortfolioId, cancellationToken);
        if (!portfolioOk)
        {
            ModelState.AddModelError(nameof(model.PortfolioId), portfolioError!);
            model.Id = id;
            await PopulateWorkCreateViewBagAsync(null, cancellationToken);
            ViewBag.WorkChromeSubPage = true;
            ViewBag.CanEditPriority = canEditPriority;
            ViewBag.SelectedDirectorateIds = directorateIds;
            ViewBag.SelectedWorkTagIds = workTagIds;
            var chrome = await _modernWork.PopulateWorkDetailAsync(
                this, id, currentUser, userEmail, "overview", null, cancellationToken);
            ViewBag.WorkItem = chrome;
            return View("~/Views/Modern/Work/Edit.cshtml", model);
        }

        project.PhaseId = model.DeliveryPhaseId;
        if (canEditPriority)
            project.DeliveryPriorityId = model.PriorityId;
        project.ActivityTypeLookupId = model.ActivityTypeId;
        project.RiskAppetiteLookupId = model.RiskAppetiteId;
        project.IsFlagship = false;
        project.IsSubjectToSpendControl = model.SubjectToSpendControl;
        project.StartDate = model.StartDate;
        project.TargetDeliveryDate = model.TargetEndDate;
        project.PrimaryContactUserId = model.PrimaryContactUserId;
        project.UpdatedAt = now;

        if (canEditPriority && model.PriorityId != oldPriorityId && !string.IsNullOrWhiteSpace(model.PriorityChangeReason))
            project.DeliveryPriorityChangeReason = model.PriorityChangeReason.Trim();

        if (!string.IsNullOrWhiteSpace(model.ProblemStatement))
        {
            var latestPs = project.ProblemStatements?
                .OrderByDescending(x => x.UpdatedAt).FirstOrDefault();
            if (latestPs != null)
            {
                latestPs.ProblemStatement = model.ProblemStatement.Trim();
                latestPs.UpdatedAt = now;
            }
            else
            {
                _context.ProjectProblemStatements.Add(new ProjectProblemStatement
                {
                    ProjectId = id,
                    ProblemStatement = model.ProblemStatement.Trim(),
                    CreatedByEmail = userEmail,
                    CreatedByName = currentUser.Name,
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }
        }

        _context.ProjectDirectorates.RemoveRange(project.Directorates);
        foreach (var divId in directorateIds.Distinct())
        {
            _context.ProjectDirectorates.Add(new ProjectDirectorate
            {
                ProjectId = id,
                DivisionId = divId,
                CreatedAt = now
            });
        }

        var validTagIds = await _context.WorkItemTagLookups.AsNoTracking()
            .Where(t => t.IsActive && workTagIds.Contains(t.Id))
            .Select(t => t.Id)
            .ToListAsync(cancellationToken);

        _context.ProjectWorkItemTags.RemoveRange(project.ProjectWorkItemTags);
        foreach (var tagId in validTagIds.Distinct())
        {
            _context.ProjectWorkItemTags.Add(new ProjectWorkItemTag
            {
                ProjectId = id,
                WorkItemTagLookupId = tagId
            });
        }

        await _context.SaveChangesAsync(cancellationToken);

        TempData["Message"] = "Work item updated.";
        return RedirectToAction(nameof(Detail), new { id });
    }

    [HttpGet("{id:int}/people/edit")]
    [HttpGet("/ModernWork/EditPeople/{id:int}")]
    public async Task<IActionResult> EditPeople(int id, CancellationToken cancellationToken = default)
    {
        var userEmail = User.Identity?.Name;
        if (string.IsNullOrEmpty(userEmail))
            return Unauthorized();

        var currentUser = await _context.Users
            .FirstOrDefaultAsync(u => u.Email.ToLower() == userEmail.ToLower(), cancellationToken);
        if (currentUser == null)
            return Unauthorized();

        var deny = await EnsureUserCanEditWorkAsync(id, cancellationToken);
        if (deny != null)
            return deny;

        var work = await _modernWork.PopulateWorkDetailAsync(
            this, id, currentUser, userEmail, "overview", null, cancellationToken);
        if (work == null)
            return NotFound();

        var roleTypes = new List<ContactRoleType>
        {
            new() { Id = 1, Name = "SRO" },
            new() { Id = 2, Name = "Service Owner" },
            new() { Id = 3, Name = "PMO Contact" },
            new() { Id = 4, Name = "Reporting contact" },
            new() { Id = 5, Name = "Other (custom role)" }
        };

        var project = await _context.Projects.AsNoTracking()
            .Include(p => p.PrimaryContactUser)
            .Include(p => p.ProjectContacts).ThenInclude(pc => pc.User)
            .Include(p => p.SeniorResponsibleOfficers).ThenInclude(sro => sro.User)
            .Include(p => p.ServiceOwners).ThenInclude(so => so.User)
            .Include(p => p.PmoContacts).ThenInclude(pmo => pmo.User)
            .Include(p => p.BudgetOwners)
            .FirstOrDefaultAsync(p => p.Id == id && !p.IsDeleted, cancellationToken);

        if (project != null)
        {
            work.Contacts.Clear();
            ProjectGovernanceContacts.PopulateWorkItemContacts(work, project);

            var sroContact = project.SeniorResponsibleOfficers.FirstOrDefault()?.User
                ?? project.ProjectContacts.FirstOrDefault(c => string.Equals(c.Role, "SRO", StringComparison.OrdinalIgnoreCase))?.User;
            ViewBag.FirstSroName = sroContact?.Name;
            ViewBag.PrimaryContactSubtitle = project.PrimaryContactUser?.Email;
            if (ViewBag.PrimaryContactName == null || ViewBag.PrimaryContactName == "—")
                ViewBag.PrimaryContactName = project.PrimaryContactUser?.Name;
            ViewBag.BudgetOwnerSameAsSro = !project.BudgetOwners.Any();
        }
        else
        {
            ViewBag.FirstSroName = (string?)null;
            ViewBag.PrimaryContactSubtitle = (string?)null;
            ViewBag.BudgetOwnerSameAsSro = true;
        }

        ViewBag.ContactRoleTypes = roleTypes;
        ViewBag.WorkChromeSubPage = true;

        return View("~/Views/Modern/Work/EditPeople.cshtml", work);
    }

    // ─── SetPrimaryContact / SetBudgetOwner (legacy → unified contact form) ─
    [HttpGet("{id:int}/primary-contact/set")]
    [HttpGet("/ModernWork/SetPrimaryContact/{id:int}")]
    public IActionResult SetPrimaryContact(int id, string? returnTo = "detail")
        => RedirectToAction(nameof(EditContact), new { id, kind = "primary", returnTo });

    [HttpPost("{id:int}/primary-contact/set")]
    [HttpPost("/ModernWork/SetPrimaryContact/{id:int}")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> SetPrimaryContactPost(int id, [FromForm] int AppUserId, CancellationToken cancellationToken = default)
        => EditContactPost(id, "primary", contactId: null, AppUserId, SameAsSro: false, BusinessAreaLookupId: null,
            ContactRoleTypeId: null, CustomRole: null, returnTo: "detail", cancellationToken: cancellationToken);

    [HttpGet("{id:int}/budget-owner/set")]
    [HttpGet("/ModernWork/SetBudgetOwner/{id:int}")]
    public IActionResult SetBudgetOwner(int id, string? returnTo = "detail")
        => RedirectToAction(nameof(EditContact), new { id, kind = "budget", returnTo });

    [HttpPost("{id:int}/budget-owner/set")]
    [HttpPost("/ModernWork/SetBudgetOwner/{id:int}")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> SetBudgetOwnerPost(int id, [FromForm] bool SameAsSro, [FromForm] int AppUserId, CancellationToken cancellationToken = default)
        => EditContactPost(id, "budget", contactId: null, AppUserId: 0, SameAsSro: SameAsSro,
            BusinessAreaLookupId: AppUserId > 0 ? AppUserId : null, ContactRoleTypeId: null, CustomRole: null,
            returnTo: "detail", cancellationToken: cancellationToken);

    // ─── RemoveContact ───────────────────────────────────────────
    [HttpPost("{id:int}/contact/{contactId:int}/remove")]
    [HttpPost("/ModernWork/RemoveContact/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveContact(int id, int contactId, [FromForm] string? returnTo, CancellationToken cancellationToken = default)
    {
        var deny = await EnsureUserCanEditWorkAsync(id, cancellationToken);
        if (deny != null) return deny;

        if (ProjectGovernanceContacts.IsJunctionContactId(contactId))
        {
            await ProjectGovernanceContacts.TryRemoveContactAsync(_context, id, contactId, cancellationToken);
        }
        else
        {
            var contact = await _context.Set<ProjectContact>()
                .FirstOrDefaultAsync(c => c.Id == contactId && c.ProjectId == id, cancellationToken);
            if (contact != null)
            {
                _context.Set<ProjectContact>().Remove(contact);
                await _context.SaveChangesAsync(cancellationToken);
            }
        }

        TempData["SuccessMessage"] = "Contact removed.";

        if (string.Equals(returnTo, "detail", StringComparison.OrdinalIgnoreCase))
        {
            var url = Url.Action(nameof(Detail), new { id });
            return string.IsNullOrEmpty(url) ? RedirectToAction(nameof(Detail), new { id }) : LocalRedirect(url + "#wd-contacts");
        }

        return RedirectToAction(nameof(EditPeople), new { id });
    }

    [HttpPost("{id:int}/contact/update-display-name")]
    [HttpPost("/ModernWork/UpdateContactDisplayName/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateContactDisplayName(
        int id,
        [FromForm] int contactId,
        [FromForm] string? displayName,
        [FromForm] string? returnTo,
        CancellationToken cancellationToken = default)
    {
        var deny = await EnsureUserCanEditWorkAsync(id, cancellationToken);
        if (deny != null) return deny;

        var name = (displayName ?? "").Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 200)
        {
            TempData["ErrorMessage"] = "Enter a display name up to 200 characters.";
            if (string.Equals(returnTo, "detail", StringComparison.OrdinalIgnoreCase))
            {
                var url = Url.Action(nameof(Detail), new { id });
                return string.IsNullOrEmpty(url) ? RedirectToAction(nameof(Detail), new { id }) : LocalRedirect(url + "#wd-contacts");
            }

            return RedirectToAction(nameof(EditPeople), new { id });
        }

        var contact = await _context.Set<ProjectContact>()
            .FirstOrDefaultAsync(c => c.Id == contactId && c.ProjectId == id, cancellationToken);
        if (contact == null)
            return NotFound();

        contact.Name = name;
        contact.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
        TempData["SuccessMessage"] = "Contact name updated.";

        if (string.Equals(returnTo, "detail", StringComparison.OrdinalIgnoreCase))
        {
            var url = Url.Action(nameof(Detail), new { id });
            return string.IsNullOrEmpty(url) ? RedirectToAction(nameof(Detail), new { id }) : LocalRedirect(url + "#wd-contacts");
        }

        return RedirectToAction(nameof(EditPeople), new { id });
    }

    private static readonly Dictionary<int, string> WorkContactRoleNames = new()
    {
        { 1, "SRO" },
        { 2, "Service Owner" },
        { 3, "PMO Contact" },
        { 4, "Reporting contact" }
    };

    private IActionResult RedirectAfterContactChange(int id, string? returnTo)
    {
        if (string.Equals(returnTo, "EditPeople", StringComparison.OrdinalIgnoreCase))
            return RedirectToAction(nameof(EditPeople), new { id });

        var url = Url.Action(nameof(Detail), new { id });
        return string.IsNullOrEmpty(url)
            ? RedirectToAction(nameof(Detail), new { id })
            : LocalRedirect(url + "#wd-contacts");
    }

    private static int? ContactRoleTypeIdFromProjectRole(string role) =>
        WorkContactRoleNames.FirstOrDefault(kv => string.Equals(kv.Value, role, StringComparison.OrdinalIgnoreCase)).Key is int id and > 0
            ? id
            : 5;

    private async Task<List<ContactRoleType>> LoadWorkContactRoleTypesAsync(CancellationToken cancellationToken) =>
        await Task.FromResult(new List<ContactRoleType>
        {
            new() { Id = 1, Name = "SRO" },
            new() { Id = 2, Name = "Service Owner" },
            new() { Id = 3, Name = "PMO Contact" },
            new() { Id = 4, Name = "Reporting contact" },
            new() { Id = 5, Name = "Other (custom role)" }
        });

    private async Task<IActionResult?> LoadWorkContactFormViewAsync(
        int id,
        string formKind,
        WorkItemContact model,
        string? returnTo,
        CancellationToken cancellationToken)
    {
        var deny = await EnsureUserCanEditWorkAsync(id, cancellationToken);
        if (deny != null) return deny;

        var userEmail = User.Identity?.Name;
        var currentUser = await _context.Users.FirstOrDefaultAsync(u => u.Email.ToLower() == userEmail!.ToLower(), cancellationToken);
        if (currentUser == null) return Unauthorized();

        var work = await _modernWork.PopulateWorkDetailAsync(this, id, currentUser, userEmail!, "contacts", null, cancellationToken);
        if (work == null) return NotFound();

        ViewBag.WorkItem = work;
        ViewBag.ContactFormKind = formKind;
        ViewBag.ContactRoleTypes = await LoadWorkContactRoleTypesAsync(cancellationToken);
        ViewBag.ReturnTo = returnTo ?? "detail";
        ViewBag.ReturnToEditPeople = string.Equals(returnTo, "EditPeople", StringComparison.OrdinalIgnoreCase);
        ViewBag.WorkChromeSubPage = true;

        return null;
    }

    [HttpGet("{id:int}/contact/edit")]
    [HttpGet("/ModernWork/EditContact/{id:int}")]
    public async Task<IActionResult> EditContact(
        int id,
        string? kind,
        int? contactId,
        string? returnTo,
        CancellationToken cancellationToken = default)
    {
        var formKind = (kind ?? "").Trim().ToLowerInvariant();
        var model = new WorkItemContact { WorkItemId = id };
        ViewBag.CustomRoleName = null;

        if (contactId.HasValue)
        {
            if (ProjectGovernanceContacts.TryGovernanceRoleKindFromJunctionContactId(contactId.Value, out var junctionKind))
                return RedirectToAction(nameof(EditContact), new { id, kind = junctionKind, returnTo });

            var pc = await _context.Set<ProjectContact>()
                .Include(c => c.User)
                .FirstOrDefaultAsync(c => c.Id == contactId.Value && c.ProjectId == id, cancellationToken);
            if (pc == null) return NotFound();

            model.Id = pc.Id;
            model.ContactRoleTypeId = ContactRoleTypeIdFromProjectRole(pc.Role);
            model.RoleName = model.ContactRoleTypeId == 5 ? pc.Role : null;
            ViewBag.CustomRoleName = model.RoleName;
            ViewBag.ExistingContactUserId = pc.UserId;
            ViewBag.ExistingContactUserName = pc.User?.Name ?? pc.Name;
            ViewBag.ExistingContactUserEmail = pc.User?.Email ?? pc.Email;
            ViewBag.ContactFormKind = "EditContact";
            var prepContact = await LoadWorkContactFormViewAsync(id, "EditContact", model, returnTo, cancellationToken);
            if (prepContact != null) return prepContact;
            return View("~/Views/Modern/Work/AddContact.cshtml", model);
        }

        if (formKind == "primary")
        {
            ViewBag.ContactFormKind = "EditPrimary";
            var prep = await LoadWorkContactFormViewAsync(id, "EditPrimary", model, returnTo, cancellationToken);
            if (prep != null) return prep;

            var project = await _context.Projects.AsNoTracking()
                .Include(p => p.PrimaryContactUser)
                .FirstOrDefaultAsync(p => p.Id == id && !p.IsDeleted, cancellationToken);
            ViewBag.PrimaryContactUserId = project?.PrimaryContactUserId;
            ViewBag.PrimaryContactUserName = project?.PrimaryContactUser?.Name;
            ViewBag.PrimaryContactUserEmail = project?.PrimaryContactUser?.Email;
            return View("~/Views/Modern/Work/AddContact.cshtml", model);
        }

        if (formKind == "budget")
        {
            ViewBag.ContactFormKind = "EditBudget";
            var prep = await LoadWorkContactFormViewAsync(id, "EditBudget", model, returnTo, cancellationToken);
            if (prep != null) return prep;

            var project = await _context.Projects.AsNoTracking()
                .Include(p => p.BudgetOwners)
                .Include(p => p.ProjectContacts).ThenInclude(pc => pc.User)
                .Include(p => p.SeniorResponsibleOfficers).ThenInclude(sro => sro.User)
                .FirstOrDefaultAsync(p => p.Id == id && !p.IsDeleted, cancellationToken);

            var sroUser = project?.SeniorResponsibleOfficers.FirstOrDefault()?.User
                ?? project?.ProjectContacts.FirstOrDefault(c => string.Equals(c.Role, "SRO", StringComparison.OrdinalIgnoreCase))?.User;
            ViewBag.FirstSroName = sroUser?.Name;
            ViewBag.BudgetOwnerSameAsSro = project == null || !project.BudgetOwners.Any();
            ViewBag.SelectedBusinessAreaLookupId = project?.BudgetOwners.FirstOrDefault()?.BusinessAreaLookupId;
            ViewBag.BusinessAreaOptions = await _context.BusinessAreaLookups.AsNoTracking()
                .Where(b => b.IsActive)
                .OrderBy(b => b.Name)
                .Select(b => new LookupOption { Id = b.Id, Name = b.Name })
                .ToListAsync(cancellationToken);
            return View("~/Views/Modern/Work/AddContact.cshtml", model);
        }

        if (formKind == "directorates")
        {
            ViewBag.ContactFormKind = "EditDirectorates";
            var prep = await LoadWorkContactFormViewAsync(id, "EditDirectorates", model, returnTo, cancellationToken);
            if (prep != null) return prep;
            await PopulateDirectoratesContactFormAsync(id, cancellationToken);
            return View("~/Views/Modern/Work/AddContact.cshtml", model);
        }

        if (formKind == "businesscase")
        {
            ViewBag.ContactFormKind = "EditBusinessCase";
            var prep = await LoadWorkContactFormViewAsync(id, "EditBusinessCase", model, returnTo, cancellationToken);
            if (prep != null) return prep;
            var project = await _context.Projects.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == id && !p.IsDeleted, cancellationToken);
            ViewBag.BusinessCaseApproval = project?.BusinessCaseApproval;
            return View("~/Views/Modern/Work/AddContact.cshtml", model);
        }

        if (ProjectGovernanceContacts.TryGovernanceRoleKindToTypeId(formKind, out var governanceRoleTypeId))
        {
            ViewBag.ContactFormKind = "EditGovernanceRole";
            ViewBag.GovernanceRoleTypeId = governanceRoleTypeId;
            ViewBag.GovernanceRoleKind = formKind;
            var prep = await LoadWorkContactFormViewAsync(id, "EditGovernanceRole", model, returnTo, cancellationToken);
            if (prep != null) return prep;
            await PopulateGovernanceRoleContactFormAsync(id, governanceRoleTypeId, cancellationToken);
            return View("~/Views/Modern/Work/AddContact.cshtml", model);
        }

        return NotFound();
    }

    private async Task PopulateDirectoratesContactFormAsync(int projectId, CancellationToken cancellationToken)
    {
        var directorates = await _context.Divisions.AsNoTracking().Where(d => d.IsActive).OrderBy(d => d.Name)
            .Select(d => new Directorate { Id = d.Id, Name = d.Name, IsActive = true }).ToListAsync(cancellationToken);
        ViewBag.Directorates = directorates;
        var selected = await _context.ProjectDirectorates.AsNoTracking()
            .Where(pd => pd.ProjectId == projectId)
            .Select(pd => pd.DivisionId)
            .ToArrayAsync(cancellationToken);
        ViewBag.SelectedDirectorateIds = selected;
    }

    private async Task PopulateGovernanceRoleContactFormAsync(int projectId, int roleTypeId, CancellationToken cancellationToken)
    {
        var project = await _context.Projects.AsNoTracking()
            .Include(p => p.SeniorResponsibleOfficers).ThenInclude(s => s.User)
            .Include(p => p.ServiceOwners).ThenInclude(s => s.User)
            .Include(p => p.PmoContacts).ThenInclude(p => p.User)
            .FirstOrDefaultAsync(p => p.Id == projectId && !p.IsDeleted, cancellationToken);

        IEnumerable<User?> users = project == null
            ? Enumerable.Empty<User?>()
            : roleTypeId switch
            {
                1 => project.SeniorResponsibleOfficers.OrderBy(s => s.Id).Select(s => s.User),
                2 => project.ServiceOwners.OrderBy(s => s.Id).Select(s => s.User),
                3 => project.PmoContacts.OrderBy(s => s.Id).Select(s => s.User),
                _ => Enumerable.Empty<User?>()
            };

        ViewBag.GovernanceRoleCurrentUsers = users
            .Where(u => u != null && !string.IsNullOrEmpty(u!.AzureObjectId))
            .Select(u => new GovernanceRoleUserRow(
                u!.AzureObjectId!,
                u.Name ?? u.Email ?? "—",
                u.Email ?? ""))
            .ToList();
    }

    public sealed record GovernanceRoleUserRow(string AzureObjectId, string Name, string Email);

    [HttpPost("{id:int}/contact/governance/add")]
    [HttpPost("/ModernWork/GovernanceRoleAdd/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> GovernanceRoleAddUser(
        int id,
        [FromForm] string kind,
        [FromForm] Guid? azureObjectId,
        [FromForm] string? returnTo,
        CancellationToken cancellationToken = default)
    {
        var deny = await EnsureUserCanEditWorkAsync(id, cancellationToken);
        if (deny != null) return deny;

        if (!ProjectGovernanceContacts.TryGovernanceRoleKindToTypeId(kind, out var roleTypeId))
            return NotFound();

        if (!azureObjectId.HasValue)
        {
            TempData["ErrorMessage"] = "Select a person from the directory.";
            return RedirectToAction(nameof(EditContact), new { id, kind, returnTo });
        }

        await ProjectGovernanceContacts.AddGovernanceRoleUserAsync(
            _context, id, roleTypeId, azureObjectId.Value, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);

        TempData["SuccessMessage"] = $"{ProjectGovernanceContacts.GovernanceRoleDisplayName(roleTypeId)} updated.";
        return RedirectToAction(nameof(EditContact), new { id, kind, returnTo });
    }

    [HttpPost("{id:int}/contact/governance/remove")]
    [HttpPost("/ModernWork/GovernanceRoleRemove/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> GovernanceRoleRemoveUser(
        int id,
        [FromForm] string kind,
        [FromForm] Guid? azureObjectId,
        [FromForm] string? returnTo,
        CancellationToken cancellationToken = default)
    {
        var deny = await EnsureUserCanEditWorkAsync(id, cancellationToken);
        if (deny != null) return deny;

        if (!ProjectGovernanceContacts.TryGovernanceRoleKindToTypeId(kind, out var roleTypeId))
            return NotFound();

        if (!azureObjectId.HasValue)
            return RedirectToAction(nameof(EditContact), new { id, kind, returnTo });

        await ProjectGovernanceContacts.RemoveGovernanceRoleUserAsync(
            _context, id, roleTypeId, azureObjectId.Value, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);

        TempData["SuccessMessage"] = $"{ProjectGovernanceContacts.GovernanceRoleDisplayName(roleTypeId)} updated.";
        return RedirectToAction(nameof(EditContact), new { id, kind, returnTo });
    }

    [HttpPost("{id:int}/contact/edit")]
    [HttpPost("/ModernWork/EditContact/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditContactPost(
        int id,
        [FromForm] string kind,
        [FromForm] int? contactId,
        [FromForm] int AppUserId,
        [FromForm] bool SameAsSro,
        [FromForm] int? BusinessAreaLookupId,
        [FromForm] int? ContactRoleTypeId,
        [FromForm] string? CustomRole,
        [FromForm] string? returnTo,
        [FromForm] int[]? directorateIds = null,
        [FromForm] string? businessCaseApproval = null,
        [FromForm] string? selectedUserObjectIds = null,
        CancellationToken cancellationToken = default)
    {
        var formKind = (kind ?? "").Trim().ToLowerInvariant();

        if (formKind == "directorates")
        {
            var deny = await EnsureUserCanEditWorkAsync(id, cancellationToken);
            if (deny != null) return deny;

            var project = await _context.Projects
                .Include(p => p.Directorates)
                .FirstOrDefaultAsync(p => p.Id == id && !p.IsDeleted, cancellationToken);
            if (project == null) return NotFound();

            directorateIds ??= Array.Empty<int>();
            _context.ProjectDirectorates.RemoveRange(project.Directorates);
            foreach (var divId in directorateIds.Distinct())
            {
                _context.ProjectDirectorates.Add(new ProjectDirectorate
                {
                    ProjectId = id,
                    DivisionId = divId,
                    CreatedAt = DateTime.UtcNow
                });
            }

            project.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(cancellationToken);
            TempData["SuccessMessage"] = "Directorates updated.";
            return RedirectAfterContactChange(id, returnTo);
        }

        if (formKind == "businesscase")
        {
            var deny = await EnsureUserCanEditWorkAsync(id, cancellationToken);
            if (deny != null) return deny;

            var project = await _context.Projects.FirstOrDefaultAsync(p => p.Id == id && !p.IsDeleted, cancellationToken);
            if (project == null) return NotFound();

            project.BusinessCaseApproval = string.IsNullOrWhiteSpace(businessCaseApproval) ? null : businessCaseApproval.Trim();
            project.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(cancellationToken);
            TempData["SuccessMessage"] = "Business case approval updated.";
            return RedirectAfterContactChange(id, returnTo);
        }

        if (ProjectGovernanceContacts.TryGovernanceRoleKindToTypeId(formKind, out var governanceRoleTypeId))
        {
            var deny = await EnsureUserCanEditWorkAsync(id, cancellationToken);
            if (deny != null) return deny;

            var objectIds = ProjectGovernanceContacts.ParseSelectedObjectIds(selectedUserObjectIds);
            await ProjectGovernanceContacts.ReplaceGovernanceRoleUsersAsync(
                _context, id, governanceRoleTypeId, objectIds, cancellationToken);
            await _context.SaveChangesAsync(cancellationToken);
            TempData["SuccessMessage"] = $"{ProjectGovernanceContacts.GovernanceRoleDisplayName(governanceRoleTypeId)} updated.";
            return RedirectAfterContactChange(id, returnTo);
        }

        if (formKind == "primary")
        {
            var deny = await EnsureUserCanEditWorkAsync(id, cancellationToken);
            if (deny != null) return deny;

            var project = await _context.Projects.FirstOrDefaultAsync(p => p.Id == id && !p.IsDeleted, cancellationToken);
            if (project == null) return NotFound();

            if (AppUserId > 0)
            {
                var userExists = await _context.Users.AsNoTracking().AnyAsync(u => u.Id == AppUserId, cancellationToken);
                if (!userExists)
                {
                    ModelState.AddModelError("AppUserId", "Select a valid user from the user picker.");
                    return await EditContact(id, "primary", null, returnTo, cancellationToken);
                }
            }

            project.PrimaryContactUserId = AppUserId > 0 ? AppUserId : null;
            project.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(cancellationToken);
            TempData["SuccessMessage"] = "Primary contact updated.";
            return RedirectAfterContactChange(id, returnTo);
        }

        if (formKind == "budget")
        {
            var deny = await EnsureUserCanEditWorkAsync(id, cancellationToken);
            if (deny != null) return deny;

            var project = await _context.Projects
                .Include(p => p.BudgetOwners)
                .FirstOrDefaultAsync(p => p.Id == id && !p.IsDeleted, cancellationToken);
            if (project == null) return NotFound();

            _context.ProjectBudgetOwners.RemoveRange(project.BudgetOwners);
            if (!SameAsSro && BusinessAreaLookupId.HasValue && BusinessAreaLookupId > 0)
            {
                var areaExists = await _context.BusinessAreaLookups.AsNoTracking()
                    .AnyAsync(b => b.Id == BusinessAreaLookupId.Value && b.IsActive, cancellationToken);
                if (!areaExists)
                {
                    ModelState.AddModelError("BusinessAreaLookupId", "Select a valid business area.");
                    return await EditContact(id, "budget", null, returnTo, cancellationToken);
                }

                _context.ProjectBudgetOwners.Add(new ProjectBudgetOwner
                {
                    ProjectId = id,
                    BusinessAreaLookupId = BusinessAreaLookupId.Value,
                    CreatedAt = DateTime.UtcNow
                });
            }

            project.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(cancellationToken);
            TempData["SuccessMessage"] = "Budget owner updated.";
            return RedirectAfterContactChange(id, returnTo);
        }

        if (contactId.HasValue)
        {
            if (AppUserId <= 0)
            {
                ModelState.AddModelError("AppUserId", "Select a user.");
                return await EditContact(id, kind: null, contactId, returnTo, cancellationToken);
            }

            var deny = await EnsureUserCanEditWorkAsync(id, cancellationToken);
            if (deny != null) return deny;

            var pc = await _context.Set<ProjectContact>()
                .FirstOrDefaultAsync(c => c.Id == contactId.Value && c.ProjectId == id, cancellationToken);
            if (pc == null) return NotFound();

            var user = await _context.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == AppUserId, cancellationToken);
            if (user == null)
            {
                ModelState.AddModelError("AppUserId", "Select a valid user from the user picker.");
                return await EditContact(id, kind: null, contactId, returnTo, cancellationToken);
            }

            pc.UserId = AppUserId;
            pc.Name = user.Name ?? user.Email ?? "—";
            pc.Email = user.Email ?? "";
            pc.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(cancellationToken);
            TempData["SuccessMessage"] = "Contact updated.";
            return RedirectAfterContactChange(id, returnTo);
        }

        return NotFound();
    }

    // ─── AddContact ──────────────────────────────────────────────
    private async Task<IActionResult> AddContactViewAsync(int id, int? contactRoleTypeId, string? returnTo, string? customRoleName, CancellationToken cancellationToken)
    {
        var deny = await EnsureUserCanEditWorkAsync(id, cancellationToken);
        if (deny != null) return deny;

        var userEmail = User.Identity?.Name;
        var currentUser = await _context.Users.FirstOrDefaultAsync(u => u.Email.ToLower() == userEmail!.ToLower(), cancellationToken);
        if (currentUser == null) return Unauthorized();

        var model = new WorkItemContact
        {
            WorkItemId = id,
            ContactRoleTypeId = contactRoleTypeId
        };
        ViewBag.CustomRoleName = string.IsNullOrWhiteSpace(customRoleName) ? null : customRoleName.Trim();
        ViewBag.ContactFormKind = "Add";
        var prep = await LoadWorkContactFormViewAsync(id, "Add", model, returnTo, cancellationToken);
        if (prep != null) return prep;
        return View("~/Views/Modern/Work/AddContact.cshtml", model);
    }

    [HttpGet("{id:int}/contact/add")]
    [HttpGet("/ModernWork/AddContact/{id:int}")]
    public Task<IActionResult> AddContact(int id, int? contactRoleTypeId, string? returnTo, string? customRoleName, CancellationToken cancellationToken = default)
        => AddContactViewAsync(id, contactRoleTypeId, returnTo, customRoleName, cancellationToken);

    [HttpPost("{id:int}/contact/add")]
    [HttpPost("/ModernWork/AddContact/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddContactPost(int id, [FromForm] int? ContactRoleTypeId, [FromForm] int AppUserId, [FromForm] string? returnTo, [FromForm] string? CustomRole, CancellationToken cancellationToken = default)
    {
        var deny = await EnsureUserCanEditWorkAsync(id, cancellationToken);
        if (deny != null) return deny;

        if (AppUserId <= 0 || !ContactRoleTypeId.HasValue || ContactRoleTypeId == 0)
        {
            ModelState.AddModelError("ContactRoleTypeId", "Select a contact role.");
            ModelState.AddModelError("AppUserId", "Select a user.");
            return await AddContactViewAsync(id, ContactRoleTypeId, returnTo, ContactRoleTypeId == 5 ? CustomRole?.Trim() : null, cancellationToken);
        }

        var roleNames = new Dictionary<int, string>
        {
            { 1, "SRO" }, { 2, "Service Owner" }, { 3, "PMO Contact" }, { 4, "Reporting contact" }
        };

        string roleStr;
        if (ContactRoleTypeId.Value == 5)
        {
            var cr = (CustomRole ?? "").Trim();
            if (string.IsNullOrEmpty(cr) || cr.Length > 100)
            {
                ModelState.AddModelError("CustomRole", "Enter a custom role name of up to 100 characters.");
                return await AddContactViewAsync(id, ContactRoleTypeId, returnTo, cr, cancellationToken);
            }
            roleStr = cr;
        }
        else
        {
            if (!roleNames.TryGetValue(ContactRoleTypeId.Value, out var rn))
            {
                ModelState.AddModelError("ContactRoleTypeId", "Select a valid contact role.");
                return await AddContactViewAsync(id, ContactRoleTypeId, returnTo, null, cancellationToken);
            }
            roleStr = rn;
        }

        var user = await _context.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == AppUserId, cancellationToken);
        if (user == null)
        {
            ModelState.AddModelError("AppUserId", "Select a valid user from the user picker.");
            return await AddContactViewAsync(id, ContactRoleTypeId, returnTo, ContactRoleTypeId == 5 ? CustomRole?.Trim() : null, cancellationToken);
        }

        if (ContactRoleTypeId is >= 1 and <= 3)
        {
            await ProjectGovernanceContacts.SyncJunctionOnAddContactAsync(
                _context, id, ContactRoleTypeId.Value, user, cancellationToken);
            await _context.SaveChangesAsync(cancellationToken);
        }
        else
        {
            var isGovernanceContact = ContactRoleTypeId == 5
                && string.Equals(returnTo, "detail", StringComparison.OrdinalIgnoreCase);
            var contact = new ProjectContact
            {
                ProjectId = id,
                UserId = AppUserId,
                Role = roleStr,
                Name = user.Name ?? user.Email ?? "—",
                Email = user.Email ?? "",
                TeamStatus = isGovernanceContact ? ProjectGovernanceContacts.GovernanceTeamStatus : "current",
                SortOrder = 10,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            _context.Set<ProjectContact>().Add(contact);
            await _context.SaveChangesAsync(cancellationToken);
        }

        TempData["SuccessMessage"] = "Contact added.";
        return RedirectAfterContactChange(id, returnTo);
    }

    [HttpGet("{id:int}/change-status")]
    [HttpGet("/ModernWork/ChangeStatus/{id:int}")]
    public async Task<IActionResult> ChangeStatus(int id, CancellationToken cancellationToken = default)
    {
        var userEmail = User.Identity?.Name;
        if (string.IsNullOrEmpty(userEmail))
            return Unauthorized();

        var currentUser = await _context.Users
            .FirstOrDefaultAsync(u => u.Email.ToLower() == userEmail.ToLower(), cancellationToken);
        if (currentUser == null)
            return Unauthorized();

        var deny = await EnsureUserCanEditWorkAsync(id, cancellationToken);
        if (deny != null)
            return deny;

        var work = await _modernWork.PopulateWorkDetailAsync(
            this, id, currentUser, userEmail, "overview", null, cancellationToken);
        if (work == null)
            return NotFound();

        ViewBag.WorkChromeSubPage = true;
        ViewBag.CurrentStatus = work.Status;
        ViewBag.WorkStatusOptions = new List<SelectListItem>
        {
            new("Active", "Active"),
            new("Paused", "Paused"),
            new("Completed", "Completed"),
            new("Cancelled", "Cancelled")
        };

        return View("~/Views/Modern/Work/ChangeStatus.cshtml", work);
    }

    [HttpPost("{id:int}/change-status")]
    [HttpPost("/ModernWork/ChangeStatus/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ChangeStatus(int id, [FromForm] string status, [FromForm] string? action, CancellationToken cancellationToken = default)
    {
        var deny = await EnsureUserCanEditWorkAsync(id, cancellationToken);
        if (deny != null)
            return deny;

        var project = await _context.Projects
            .FirstOrDefaultAsync(p => p.Id == id && !p.IsDeleted, cancellationToken);
        if (project == null)
            return NotFound();

        if (string.Equals(action, "delete", StringComparison.OrdinalIgnoreCase))
        {
            project.IsDeleted = true;
            project.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(cancellationToken);
            TempData["Message"] = "Work item deleted.";
            return RedirectToAction(nameof(AllWork));
        }

        var allowed = new[] { "Active", "Paused", "Completed", "Cancelled" };
        if (!allowed.Contains(status, StringComparer.OrdinalIgnoreCase))
            return BadRequest();

        project.Status = status;
        project.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);

        TempData["Message"] = $"Status changed to {status}.";
        return RedirectToAction(nameof(Detail), new { id });
    }

    [HttpGet("{id:int}/log-issue")]
    [HttpGet("/ModernWork/LogIssue/{id:int}")]
    [ServiceFilter(typeof(Compass.Filters.RaidFeatureGateFilter))]
    public async Task<IActionResult> LogIssue(int id, CancellationToken cancellationToken = default)
    {
        var prep = await PrepareModernLogRaidPageAsync(id, cancellationToken);
        if (prep.ErrorResult != null)
            return prep.ErrorResult;

        ViewBag.WorkChromeSection = "issues";
        await _raidIssueEditorForm.PrepareIssueEditorLookupsAsync(this, null, null, cancellationToken);
        var form = new ModernRaidIssueEditorForm
        {
            AssociationKind = "work",
            ProjectId = id,
            AssuranceItems = new List<IssueAssuranceItemForm> { new IssueAssuranceItemForm() }
        };
        ViewBag.EditorTitle = "Add issue";
        SetRaidIssueEditorWorkItemContextForLogIssue(id, prep.Work!);
        return View("~/Views/Modern/Raid/IssueEditor.cshtml", form);
    }

    [HttpPost("{id:int}/log-issue")]
    [HttpPost("/ModernWork/LogIssue/{id:int}")]
    [ValidateAntiForgeryToken]
    [ServiceFilter(typeof(Compass.Filters.RaidFeatureGateFilter))]
    public async Task<IActionResult> LogIssue(int id, [FromForm] ModernRaidIssueEditorForm form, CancellationToken cancellationToken = default)
    {
        var prep = await PrepareModernLogRaidPageAsync(id, cancellationToken);
        if (prep.ErrorResult != null)
            return prep.ErrorResult;

        ViewBag.WorkChromeSection = "issues";
        form.AssociationKind = "work";
        form.ProjectId = id;
        form.PrimaryProductId = null;
        form.AssuranceItems ??= new List<IssueAssuranceItemForm>();

        await _raidIssueEditorForm.PrepareIssueEditorLookupsAsync(this, form.OwnerUserId, form.SroUserId, cancellationToken);
        ViewBag.EditorTitle = "Add issue";
        SetRaidIssueEditorWorkItemContextForLogIssue(id, prep.Work!);

        var issue = await _raidIssueEditorForm.TryCreateIssueFromEditorFormAsync(ModelState, User, form, forceWorkProjectId: id, cancellationToken);
        if (issue == null)
        {
            if (form.AssuranceItems == null || form.AssuranceItems.Count == 0)
                form.AssuranceItems = new List<IssueAssuranceItemForm> { new IssueAssuranceItemForm() };
            return View("~/Views/Modern/Raid/IssueEditor.cshtml", form);
        }

        TempData["SuccessMessage"] = "Issue created.";
        return Redirect((Url.Action(nameof(Detail), new { id, tab = "issues" }) ?? "") + "#wd-issues");
    }

    [HttpGet("{id:int}/log-risk")]
    [HttpGet("/ModernWork/LogRisk/{id:int}")]
    [ServiceFilter(typeof(Compass.Filters.RaidFeatureGateFilter))]
    public async Task<IActionResult> LogRisk(int id, CancellationToken cancellationToken = default)
    {
        var prep = await PrepareModernLogRaidPageAsync(id, cancellationToken);
        if (prep.ErrorResult != null)
            return prep.ErrorResult;

        await _raidRiskEditorForm.PrepareRiskEditorLookupsAsync(this, null, null, cancellationToken);
        RaidDateFormHelper.SplitDateParts(DateTime.UtcNow.Date, out var idd, out var idm, out var idy);
        var form = new ModernRaidRiskEditorForm
        {
            AssociationKind = "work",
            ProjectId = id,
            IdentifiedDay = idd,
            IdentifiedMonth = idm,
            IdentifiedYear = idy,
            KriItems = new List<RiskKriItemForm> { new() }
        };
        ViewBag.RiskTierOptions = (await _raidRiskEditorForm.BuildRiskCreateTierOptionsAsync(cancellationToken)).ToList();
        ViewBag.EditorTitle = "Add risk";
        SetRaidRiskEditorWorkItemContextForLogRisk(id, prep.Work!);
        return View("~/Views/Modern/Raid/RiskEditor.cshtml", form);
    }

    [HttpPost("{id:int}/log-risk")]
    [HttpPost("/ModernWork/LogRisk/{id:int}")]
    [ValidateAntiForgeryToken]
    [ServiceFilter(typeof(Compass.Filters.RaidFeatureGateFilter))]
    public async Task<IActionResult> LogRisk(int id, [FromForm] ModernRaidRiskEditorForm form, CancellationToken cancellationToken = default)
    {
        var prep = await PrepareModernLogRaidPageAsync(id, cancellationToken);
        if (prep.ErrorResult != null)
            return prep.ErrorResult;

        form.AssociationKind = "work";
        form.ProjectId = id;
        form.PrimaryProductId = null;

        await _raidRiskEditorForm.PrepareRiskEditorLookupsAsync(this, form.OwnerUserId, form.SroUserId, cancellationToken);
        ViewBag.RiskTierOptions = (await _raidRiskEditorForm.BuildRiskCreateTierOptionsAsync(cancellationToken)).ToList();
        ViewBag.EditorTitle = "Add risk";
        SetRaidRiskEditorWorkItemContextForLogRisk(id, prep.Work!);

        var risk = await _raidRiskEditorForm.TryCreateRiskFromEditorFormAsync(ModelState, User, form, forceWorkProjectId: id, cancellationToken);
        if (risk == null)
            return View("~/Views/Modern/Raid/RiskEditor.cshtml", form);

        TempData["SuccessMessage"] = "Risk created.";
        return Redirect((Url.Action(nameof(Detail), new { id, tab = "risks" }) ?? "") + "#wd-risks");
    }

    private static string Truncate(string? s, int max)
    {
        if (string.IsNullOrEmpty(s))
            return "";
        var t = s.Trim();
        return t.Length <= max ? t : t[..max];
    }

    private static string TruncateLower(string? s, int max)
    {
        if (string.IsNullOrEmpty(s))
            return "";
        var t = s.Trim().ToLowerInvariant();
        return t.Length <= max ? t : t[..max];
    }

    private static int MapLookupOrderToFive(int? selectedId, IReadOnlyList<int> orderedIds)
    {
        if (!selectedId.HasValue || orderedIds.Count == 0)
            return 3;
        var idx = -1;
        for (var i = 0; i < orderedIds.Count; i++)
        {
            if (orderedIds[i] == selectedId.Value)
            {
                idx = i;
                break;
            }
        }
        if (idx < 0)
            return 3;
        if (orderedIds.Count == 1)
            return 3;
        var scaled = 1d + (double)idx / (orderedIds.Count - 1) * 4d;
        return (int)Math.Round(Math.Clamp(scaled, 1, 5));
    }

    private async Task<int?> GetDefaultRiskStatusIdAsync(CancellationToken cancellationToken)
    {
        var id = await _context.RiskStatuses.AsNoTracking()
            .Where(x => x.IsActive && x.Code.ToLower() == "new")
            .OrderBy(x => x.SortOrder)
            .Select(x => (int?)x.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (id.HasValue)
            return id;
        return await _context.RiskStatuses.AsNoTracking()
            .Where(x => x.IsActive)
            .OrderBy(x => x.SortOrder)
            .Select(x => (int?)x.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<int?> GetDefaultIssueStatusIdAsync(CancellationToken cancellationToken)
    {
        var id = await _context.IssueStatuses.AsNoTracking()
            .Where(x => x.IsActive && x.Code.ToLower() == "open")
            .OrderBy(x => x.SortOrder)
            .Select(x => (int?)x.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (id.HasValue)
            return id;
        return await _context.IssueStatuses.AsNoTracking()
            .Where(x => x.IsActive)
            .OrderBy(x => x.SortOrder)
            .Select(x => (int?)x.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<int?> ResolveRiskTierIdAsync(string? tier, CancellationToken cancellationToken)
    {
        var t = tier?.Trim();
        if (string.IsNullOrEmpty(t))
            return null;
        return await _context.RiskTiers.AsNoTracking()
            .Where(x => x.IsActive && !x.IsProposedTier &&
                (x.Name == t || x.Code.Replace(" ", "") == t.Replace(" ", "") || x.Name.Contains(t)))
            .OrderBy(x => x.SortOrder)
            .Select(x => (int?)x.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private void SetRaidIssueEditorWorkItemContextForLogIssue(int id, WorkItem work)
    {
        var detailUrl = Url.Action(nameof(Detail), new { id }) ?? "";
        ViewBag.RaidIssueEditorWorkItemContext = new RaidIssueEditorWorkItemContext
        {
            ProjectId = id,
            WorkTitle = work.Title ?? "Work item",
            FormPostAction = Url.Action(nameof(LogIssue), new { id }) ?? "",
            CancelUrl = $"{detailUrl}#wd-issues",
            WorkDetailUrl = detailUrl
        };
    }

    private void SetRaidRiskEditorWorkItemContextForLogRisk(int id, WorkItem work)
    {
        var detailUrl = Url.Action(nameof(Detail), new { id }) ?? "";
        ViewBag.RaidRiskEditorWorkItemContext = new RaidRiskEditorWorkItemContext
        {
            ProjectId = id,
            WorkTitle = work.Title ?? "Work item",
            FormPostAction = Url.Action(nameof(LogRisk), new { id }) ?? "",
            CancelUrl = $"{detailUrl}#wd-risks",
            WorkDetailUrl = detailUrl
        };
    }

    private async Task PopulateOwnerDisplayForLogRaidAsync(int? ownerUserId, CancellationToken cancellationToken)
    {
        if (!ownerUserId.HasValue || ownerUserId.Value <= 0)
            return;
        var u = await _context.Users.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == ownerUserId.Value, cancellationToken);
        if (u != null)
        {
            ViewBag.OwnerName = u.Name;
            ViewBag.OwnerEmail = u.Email;
        }
    }

    private async Task<(IActionResult? ErrorResult, WorkItem? Work)> PrepareModernLogRaidPageAsync(int id, CancellationToken cancellationToken)
    {
        ViewBag.MainNavSection = "work";

        var userEmail = User.Identity?.Name;
        if (string.IsNullOrEmpty(userEmail))
            return (Unauthorized(), null);

        var currentUser = await _context.Users
            .FirstOrDefaultAsync(u => u.Email.ToLower() == userEmail.ToLower(), cancellationToken);
        if (currentUser == null)
            return (Unauthorized(), null);

        var projectExists = await _context.Projects.AsNoTracking()
            .AnyAsync(p => p.Id == id && !p.IsDeleted, cancellationToken);
        if (!projectExists)
            return (NotFound(), null);

        var work = await _modernWork.GetWorkItemAsync(id);
        if (work == null)
            return (NotFound(), null);

        var milestones = await _context.Milestones.AsNoTracking()
            .Where(m => m.ProjectId == id)
            .OrderBy(m => m.DueDate)
            .ToListAsync(cancellationToken);

        var directorates = await _context.Divisions.AsNoTracking()
            .Where(d => d.IsActive)
            .OrderBy(d => d.Name)
            .Select(d => new RiskIssueNamedIntOption { Id = d.Id, Name = d.Name ?? "" })
            .ToListAsync(cancellationToken);

        var project = await _context.Projects.AsNoTracking()
            .Include(p => p.PrimaryOrganizationalGroup)
            .Include(p => p.BusinessAreaLookup)
            .Include(p => p.PhaseLookup)
            .Include(p => p.DeliveryPriority)
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

        ViewBag.WorkItem = work;
        ViewBag.Milestones = milestones;
        ViewBag.DirectorateOptions = directorates;
        ViewBag.RiskTierOptions = await _context.RiskTiers.AsNoTracking()
            .Where(x => x.IsActive && !x.IsProposedTier).OrderBy(x => x.SortOrder).ThenBy(x => x.Name)
            .Select(x => new RiskIssueNamedIntOption { Id = x.Id, Name = x.Name }).ToListAsync(cancellationToken);
        ViewBag.RiskPriorityOptions = await _context.RiskPriorities.AsNoTracking()
            .Where(x => x.IsActive).OrderBy(x => x.SortOrder)
            .Select(x => new RiskIssueNamedIntOption { Id = x.Id, Name = x.Label }).ToListAsync(cancellationToken);
        ViewBag.RiskLikelihoodOptions = await _context.RiskLikelihoods.AsNoTracking()
            .Where(x => x.IsActive).OrderBy(x => x.SortOrder)
            .Select(x => new RiskIssueNamedIntOption { Id = x.Id, Name = x.Label }).ToListAsync(cancellationToken);
        ViewBag.RiskImpactLevelOptions = await _context.RiskImpactLevels.AsNoTracking()
            .Where(x => x.IsActive).OrderBy(x => x.SortOrder)
            .Select(x => new RiskIssueNamedIntOption { Id = x.Id, Name = x.Label }).ToListAsync(cancellationToken);
        ViewBag.RiskProximityOptions = await _context.RiskProximities.AsNoTracking()
            .Where(x => x.IsActive).OrderBy(x => x.SortOrder)
            .Select(x => new RiskIssueNamedIntOption { Id = x.Id, Name = x.Label }).ToListAsync(cancellationToken);
        ViewBag.RiskCategoryOptions = await _context.RiskCategories.AsNoTracking()
            .Where(x => x.IsActive).OrderBy(x => x.SortOrder)
            .Select(x => new RiskIssueNamedIntOption { Id = x.Id, Name = x.Label }).ToListAsync(cancellationToken);
        ViewBag.IssueSeverityOptions = await _context.IssueSeverities.AsNoTracking()
            .Where(x => x.IsActive).OrderBy(x => x.SortOrder)
            .Select(x => new RiskIssueNamedIntOption { Id = x.Id, Name = x.Label }).ToListAsync(cancellationToken);
        ViewBag.IssuePriorityOptions = await _context.IssuePriorities.AsNoTracking()
            .Where(x => x.IsActive).OrderBy(x => x.SortOrder)
            .Select(x => new RiskIssueNamedIntOption { Id = x.Id, Name = x.Label }).ToListAsync(cancellationToken);
        ViewBag.IssueStatusOptions = await _context.IssueStatuses.AsNoTracking()
            .Where(x => x.IsActive).OrderBy(x => x.SortOrder)
            .Select(x => new RiskIssueNamedIntOption { Id = x.Id, Name = x.Label }).ToListAsync(cancellationToken);
        ViewBag.IssueCategoryOptions = await _context.IssueCategories.AsNoTracking()
            .Where(x => x.IsActive).OrderBy(x => x.SortOrder)
            .Select(x => new RiskIssueNamedIntOption { Id = x.Id, Name = x.Label }).ToListAsync(cancellationToken);
        ViewBag.WorkChromeSection = "risks";
        ViewBag.WorkChromeSubPage = true;
        ViewBag.WorkChromeTabsAsLinks = true;
        ViewBag.WorkIdShort = work.Id.ToString("X8").ToUpperInvariant();
        if (project != null)
        {
            ViewBag.PortfolioName = ModernWorkService.ResolveProjectBusinessAreaDisplayName(project);
            ViewBag.DeliveryPhaseName = project.PhaseLookup?.Name;
            ViewBag.PriorityName = project.DeliveryPriority?.Name;
        }

        return (null, work);
    }

    /// <summary>Bridge from modern work UI to RAID risk detail (core register).</summary>
    [HttpGet("{workId:int}/risk/{id:int}")]
    [ServiceFilter(typeof(Compass.Filters.RaidFeatureGateFilter))]
    public async Task<IActionResult> RiskDetail(int workId, int id, CancellationToken cancellationToken = default)
    {
        var exists = await _context.Risks.AsNoTracking()
            .AnyAsync(r => r.Id == id && r.ProjectId == workId && !r.IsDeleted, cancellationToken);
        if (!exists)
            return NotFound();

        return RedirectToAction(nameof(ModernRaidController.RiskDetail), "ModernRaid", new { id });
    }

    /// <summary>Bridge from modern work UI to RAID issue detail (core register).</summary>
    [HttpGet("{workId:int}/issue/{id:int}")]
    [ServiceFilter(typeof(Compass.Filters.RaidFeatureGateFilter))]
    public async Task<IActionResult> IssueDetail(int workId, int id, CancellationToken cancellationToken = default)
    {
        var exists = await _context.Issues.AsNoTracking()
            .AnyAsync(i => i.Id == id && i.ProjectId == workId && !i.IsDeleted, cancellationToken);
        if (!exists)
            return NotFound();

        return RedirectToAction(nameof(ModernRaidController.IssueDetail), "ModernRaid", new { id });
    }

    [HttpGet("watching")]
    public async Task<IActionResult> Watching(
        string? search,
        int? portfolioId,
        int? directorateId,
        string? status,
        CancellationToken cancellationToken = default)
    {
        ViewBag.MainNavSection = "work";
        ViewBag.SubNavItem = "work-watching";

        var userEmail = User.Identity?.Name;
        if (string.IsNullOrEmpty(userEmail))
            return Unauthorized();

        var currentUser = await _context.Users
            .FirstOrDefaultAsync(u => u.Email.ToLower() == userEmail.ToLower(), cancellationToken);
        if (currentUser == null)
            return Unauthorized();

        var items = await _modernWork.GetWatchingWorkItemsAsync(
            currentUser, search, portfolioId, directorateId, status, cancellationToken);

        var phaseNames = await _context.PhaseLookups.AsNoTracking()
            .Where(p => p.IsActive)
            .ToDictionaryAsync(p => p.Id, p => p.Name ?? "", cancellationToken);
        var priorityNames = await _context.DeliveryPriorities.AsNoTracking()
            .ToDictionaryAsync(p => p.Id, p => p.Name ?? "", cancellationToken);

        var portfolioIds = items.Select(w => w.PortfolioId).Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToList();
        var portfolioNames = portfolioIds.Count > 0
            ? await _context.OrganizationalGroups.AsNoTracking()
                .Where(g => portfolioIds.Contains(g.Id))
                .ToDictionaryAsync(g => g.Id, g => g.Name, cancellationToken)
            : new Dictionary<int, string>();

        var orgGroups = await _context.OrganizationalGroups.AsNoTracking().Where(g => g.IsActive).OrderBy(g => g.Name).ToListAsync(cancellationToken);
        ViewBag.Portfolios = orgGroups.Select(g => new Portfolio { Id = g.Id, Name = g.Name, IsActive = true }).ToList();
        ViewBag.Directorates = await _context.Divisions.AsNoTracking().Where(d => d.IsActive).OrderBy(d => d.Name)
            .Select(d => new Directorate { Id = d.Id, Name = d.Name, IsActive = true }).ToListAsync(cancellationToken);
        ViewBag.WorkStatusOptions = new List<LookupOption>
        {
            new() { Name = "Active", Value = "Active" },
            new() { Name = "Paused", Value = "Paused" },
            new() { Name = "Completed", Value = "Completed" },
            new() { Name = "Cancelled", Value = "Cancelled" }
        };
        ViewBag.StatusFilter = status;
        ViewBag.Search = search;
        ViewBag.FilterPortfolioId = portfolioId;
        ViewBag.FilterDirectorateId = directorateId;
        ViewBag.PortfolioNames = portfolioNames;
        ViewBag.PhaseNames = phaseNames;
        ViewBag.PriorityNames = priorityNames;

        var watchItemIds = items.Select(w => w.Id).ToList();
        var primaryContactById = watchItemIds.Count > 0
            ? await _context.Projects.AsNoTracking()
                .Where(p => watchItemIds.Contains(p.Id) && p.PrimaryContactUser != null)
                .Select(p => new { p.Id, Name = p.PrimaryContactUser!.Name ?? p.PrimaryContactUser.Email })
                .ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken)
            : new Dictionary<int, string>();
        ViewBag.PrimaryContactById = primaryContactById;

        var businessAreaByProjectId = watchItemIds.Count > 0
            ? await _context.Projects.AsNoTracking()
                .Where(p => watchItemIds.Contains(p.Id))
                .Select(p => new { p.Id, Name = p.BusinessAreaLookup != null ? p.BusinessAreaLookup.Name : null })
                .ToDictionaryAsync(
                    x => x.Id,
                    x => string.IsNullOrWhiteSpace(x.Name) ? "—" : x.Name!,
                    cancellationToken)
            : new Dictionary<int, string>();
        ViewBag.BusinessAreaByProjectId = businessAreaByProjectId;

        return View("~/Views/Modern/Work/Watching.cshtml", items);
    }

    [HttpGet("bypriority")]
    public async Task<IActionResult> ByPriority(
        string? search,
        int? portfolioId,
        int? directorateId,
        int? priorityId,
        string? status,
        string? tab = null,
        CancellationToken cancellationToken = default)
    {
        ViewBag.MainNavSection = "work";
        ViewBag.SubNavItem = "work-bypriority";

        var tabKey = (tab ?? "levels").Trim().ToLowerInvariant();
        if (tabKey != "levels" && tabKey != "mission" && tabKey != "outcomes")
            tabKey = "levels";
        ViewBag.ByPriorityTab = tabKey;

        var items = await _modernWork.GetByPriorityWorkItemsAsync(
            search, portfolioId, directorateId, priorityId, status, cancellationToken);

        var portfolioIds = items.Select(w => w.PortfolioId).Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToList();
        var portfolioNames = portfolioIds.Count > 0
            ? await _context.OrganizationalGroups.AsNoTracking()
                .Where(g => portfolioIds.Contains(g.Id))
                .ToDictionaryAsync(g => g.Id, g => g.Name, cancellationToken)
            : new Dictionary<int, string>();

        var orgGroups = await _context.OrganizationalGroups.AsNoTracking().Where(g => g.IsActive).OrderBy(g => g.Name).ToListAsync(cancellationToken);
        ViewBag.Portfolios = orgGroups.Select(g => new Portfolio { Id = g.Id, Name = g.Name, IsActive = true }).ToList();
        ViewBag.Directorates = await _context.Divisions.AsNoTracking().Where(d => d.IsActive).OrderBy(d => d.Name)
            .Select(d => new Directorate { Id = d.Id, Name = d.Name, IsActive = true }).ToListAsync(cancellationToken);
        ViewBag.PriorityOptions = await _context.DeliveryPriorities.AsNoTracking().OrderBy(p => p.SortOrder)
            .Select(p => new LookupOption { Id = p.Id, Name = p.Name ?? "", Value = p.Name ?? "" }).ToListAsync(cancellationToken);
        ViewBag.WorkStatusOptions = new List<LookupOption>
        {
            new() { Name = "Active", Value = "Active" },
            new() { Name = "Paused", Value = "Paused" },
            new() { Name = "Completed", Value = "Completed" },
            new() { Name = "Cancelled", Value = "Cancelled" }
        };
        var phaseNames = await _context.PhaseLookups.AsNoTracking()
            .Where(p => p.IsActive)
            .ToDictionaryAsync(p => p.Id, p => p.Name ?? "", cancellationToken);
        var priorityNames = await _context.DeliveryPriorities.AsNoTracking()
            .ToDictionaryAsync(p => p.Id, p => p.Name ?? "", cancellationToken);
        var bpItemIds = items.Select(w => w.Id).ToList();
        var primaryContactById = bpItemIds.Count > 0
            ? await _context.Projects.AsNoTracking()
                .Where(p => bpItemIds.Contains(p.Id) && p.PrimaryContactUser != null)
                .Select(p => new { p.Id, Name = p.PrimaryContactUser!.Name ?? p.PrimaryContactUser.Email })
                .ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken)
            : new Dictionary<int, string>();

        ViewBag.Search = search;
        ViewBag.FilterPortfolioId = portfolioId;
        ViewBag.FilterDirectorateId = directorateId;
        ViewBag.PriorityFilter = priorityId;
        ViewBag.StatusFilter = status;
        ViewBag.PortfolioNames = portfolioNames;
        ViewBag.PhaseNames = phaseNames;
        ViewBag.PriorityNames = priorityNames;
        ViewBag.PrimaryContactById = primaryContactById;
        var businessAreaById = bpItemIds.Count > 0
            ? await _context.Projects.AsNoTracking()
                .Where(p => bpItemIds.Contains(p.Id))
                .Select(p => new
                {
                    p.Id,
                    Name = p.BusinessAreaLookup != null
                        ? p.BusinessAreaLookup.Name
                        : (p.PrimaryOrganizationalGroup != null ? p.PrimaryOrganizationalGroup.Name : null)
                })
                .ToDictionaryAsync(x => x.Id, x => x.Name ?? "—", cancellationToken)
            : new Dictionary<int, string>();
        ViewBag.BusinessAreaNameByProjectId = businessAreaById;

        var missionPillarList = await _context.Missions.AsNoTracking()
            .Where(m => !m.IsDeleted)
            .OrderBy(m => m.Title)
            .Select(m => new WorkLookupOption { Id = m.Id, Name = m.Title, Value = m.Title })
            .ToListAsync(cancellationToken);
        var priorityOutcomeList = await _context.Objectives.AsNoTracking()
            .Where(o => !o.IsDeleted && o.Status == "active")
            .OrderBy(o => o.Title)
            .Select(o => new WorkLookupOption { Id = o.Id, Name = o.Title, Value = o.Title })
            .ToListAsync(cancellationToken);

        ViewBag.MissionPillars = missionPillarList;
        ViewBag.PriorityOutcomes = priorityOutcomeList;
        ViewBag.WorkCountByMissionPillarId = missionPillarList.ToDictionary(
            x => x.Id,
            x => items.Count(w => w.MissionPillars.Any(mp => mp.MissionPillarId == x.Id)));
        ViewBag.WorkCountByPriorityOutcomeId = priorityOutcomeList.ToDictionary(
            x => x.Id,
            x => items.Count(w => w.PriorityOutcomes.Any(po => po.PriorityOutcomeId == x.Id)));

        return View("~/Views/Modern/Work/ByPriority.cshtml", items);
    }

    [HttpGet("flagship")]
    public IActionResult Flagship() => RedirectToActionPermanent(nameof(ByTheme));

    [HttpGet("flagship/export")]
    public IActionResult ExportFlagship() => RedirectToActionPermanent(nameof(ByTheme));

    [HttpGet("by-theme")]
    [HttpGet("/ModernWork/ByTheme")]
    public async Task<IActionResult> ByTheme(
        string? tab,
        string? search,
        string? workItems,
        int? tagId,
        int? all,
        string? themeKey = null,
        int page = 1,
        CancellationToken cancellationToken = default)
    {
        ViewBag.MainNavSection = "work";
        ViewBag.SubNavItem = "work-by-theme";
        ViewBag.Search = search;
        var activeTab = NormalizeWorkRegisterTab(tab);
        ViewBag.ActiveTab = activeTab;

        var workItemsNorm = (workItems ?? "all").Trim().ToLowerInvariant();
        if (workItemsNorm != "with" && workItemsNorm != "without")
            workItemsNorm = "all";
        ViewBag.WorkItemsFilter = workItemsNorm;

        var themeKeyRaw = string.IsNullOrWhiteSpace(themeKey?.Trim())
            ? null
            : themeKey.Trim();
        if (string.IsNullOrWhiteSpace(themeKeyRaw))
        {
            if (all == 1)
                themeKeyRaw = "all";
            else if (tagId.HasValue)
                themeKeyRaw = tagId.Value.ToString(CultureInfo.InvariantCulture);
        }

        var showAll = string.Equals(themeKeyRaw, "all", StringComparison.OrdinalIgnoreCase);
        int? explicitThemeId = null;
        if (!showAll && !string.IsNullOrWhiteSpace(themeKeyRaw)
            && int.TryParse(themeKeyRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedTheme) && parsedTheme > 0)
        {
            explicitThemeId = parsedTheme;
        }

        var navRows = await _context.WorkItemTagLookups.AsNoTracking()
            .Where(t => t.IsActive)
            .OrderBy(t => t.SortOrder)
            .ThenBy(t => t.Name)
            .ToListAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            navRows = navRows.Where(t => t.Name.Contains(s, StringComparison.OrdinalIgnoreCase)
                || (t.Description != null && t.Description.Contains(s, StringComparison.OrdinalIgnoreCase))).ToList();
        }

        var allWorkItems = await _modernWork.GetByPriorityWorkItemsAsync(
            null, null, null, null, null, cancellationToken);

        var tagLinks = await _context.ProjectWorkItemTags.AsNoTracking()
            .Where(l => l.WorkItemTagLookup != null && l.WorkItemTagLookup.IsActive)
            .Select(l => new { l.ProjectId, l.WorkItemTagLookupId })
            .ToListAsync(cancellationToken);

        var workById = allWorkItems.ToDictionary(w => w.Id);
        var workItemsByThemeId = new Dictionary<int, List<WorkItem>>();
        foreach (var link in tagLinks)
        {
            if (!workById.TryGetValue(link.ProjectId, out var wi))
                continue;
            if (!workItemsByThemeId.ContainsKey(link.WorkItemTagLookupId))
                workItemsByThemeId[link.WorkItemTagLookupId] = new List<WorkItem>();
            workItemsByThemeId[link.WorkItemTagLookupId].Add(wi);
        }

        foreach (var key in workItemsByThemeId.Keys.ToList())
        {
            workItemsByThemeId[key] = workItemsByThemeId[key]
                .GroupBy(x => x.Id).Select(g => g.First()).OrderBy(x => x.Title).ToList();
        }

        var counts = workItemsByThemeId.ToDictionary(kv => kv.Key, kv => kv.Value.Count);

        var navCandidates = navRows.Select(t => new WorkLookupOption
        {
            Id = t.Id,
            Name = t.Name,
            Value = t.Name
        }).ToList();

        List<WorkLookupOption> navThemes;
        if (workItemsNorm == "with")
            navThemes = navCandidates.Where(t => counts.TryGetValue(t.Id, out var c) && c > 0).ToList();
        else if (workItemsNorm == "without")
            navThemes = navCandidates.Where(t => !counts.TryGetValue(t.Id, out var c) || c == 0).ToList();
        else
            navThemes = navCandidates;

        ViewBag.CountBeforeWorkFilter = navCandidates.Count;

        IReadOnlyList<WorkItem> sourceList;
        int? selectedThemeId = null;
        if (showAll)
        {
            sourceList = allWorkItems.GroupBy(w => w.Id).Select(g => g.First()).OrderBy(w => w.Title).ToList();
        }
        else
        {
            if (explicitThemeId.HasValue && navThemes.Any(t => t.Id == explicitThemeId.Value))
                selectedThemeId = explicitThemeId;
            else
                selectedThemeId = navThemes.FirstOrDefault(t => counts.TryGetValue(t.Id, out var cc) && cc > 0)?.Id
                    ?? navThemes.FirstOrDefault()?.Id;

            if (selectedThemeId.HasValue)
            {
                sourceList = workItemsByThemeId.TryGetValue(selectedThemeId.Value, out var items)
                    ? items
                    : new List<WorkItem>();
            }
            else
            {
                sourceList = new List<WorkItem>();
            }
        }

        sourceList = FilterWorkItemsByRegisterTab(sourceList, activeTab);

        var pageSize = WorkGroupingRegisterPageSize;
        var total = sourceList.Count;
        var pageCount = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
        var clampedPage = Math.Max(1, Math.Min(page, pageCount));
        var paged = sourceList.Skip((clampedPage - 1) * pageSize).Take(pageSize).ToList();
        var rowStart = total == 0 ? 0 : (clampedPage - 1) * pageSize + 1;
        var rowEnd = total == 0 ? 0 : Math.Min(clampedPage * pageSize, total);

        var phaseNames = await _context.PhaseLookups.AsNoTracking()
            .Where(p => p.IsActive)
            .ToDictionaryAsync(p => p.Id, p => p.Name ?? "", cancellationToken);
        var priorityNames = await _context.DeliveryPriorities.AsNoTracking()
            .ToDictionaryAsync(p => p.Id, p => p.Name ?? "", cancellationToken);
        var allItemIds = allWorkItems.Select(w => w.Id).ToList();
        var primaryContactById = allItemIds.Count > 0
            ? await _context.Projects.AsNoTracking()
                .Where(p => allItemIds.Contains(p.Id) && p.PrimaryContactUser != null)
                .Select(p => new { p.Id, Name = p.PrimaryContactUser!.Name ?? p.PrimaryContactUser.Email })
                .ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken)
            : new Dictionary<int, string>();

        var themeProjectIds = workItemsByThemeId.Values.SelectMany(w => w).Select(x => x.Id).Distinct().ToList();
        var businessAreaById = themeProjectIds.Count > 0
            ? await _context.Projects.AsNoTracking()
                .Where(p => themeProjectIds.Contains(p.Id))
                .Select(p => new
                {
                    p.Id,
                    Name = p.BusinessAreaLookup != null
                        ? p.BusinessAreaLookup.Name
                        : (p.PrimaryOrganizationalGroup != null ? p.PrimaryOrganizationalGroup.Name : null)
                })
                .ToDictionaryAsync(x => x.Id, x => x.Name ?? "—", cancellationToken)
            : new Dictionary<int, string>();

        ViewBag.ThemesNav = navThemes;
        ViewBag.WorkCountByThemeId = counts;
        ViewBag.SelectedThemeId = selectedThemeId;
        ViewBag.ShowAllThemes = showAll;
        ViewBag.PagedWorkItems = paged;
        ViewBag.RegisterPage = clampedPage;
        ViewBag.RegisterPageCount = pageCount;
        ViewBag.RegisterTotalCount = total;
        ViewBag.RegisterDisplayRowStart = rowStart;
        ViewBag.RegisterDisplayRowEnd = rowEnd;
        ViewBag.RegisterIsPaginated = total > pageSize;
        ViewBag.PhaseNames = phaseNames;
        ViewBag.PriorityNames = priorityNames;
        ViewBag.PrimaryContactById = primaryContactById;
        ViewBag.BusinessAreaNameByProjectId = businessAreaById;

        string? filterKeyForView = themeKeyRaw;
        if (string.IsNullOrWhiteSpace(filterKeyForView))
        {
            if (showAll)
                filterKeyForView = "all";
            else if (selectedThemeId.HasValue)
                filterKeyForView = selectedThemeId.Value.ToString(CultureInfo.InvariantCulture);
        }

        ViewBag.ThemeFilterKey = filterKeyForView;

        var scopedTagIds = showAll || !selectedThemeId.HasValue
            ? null
            : new[] { selectedThemeId.Value };

        ViewBag.WorkRegisterSubNav = await BuildWorkRegisterSubNavForScopeAsync(
            businessAreaId: null,
            directorateId: null,
            search,
            ragId: null,
            priorityId: null,
            activeTab,
            nameof(ByTheme),
            tagIds: scopedTagIds,
            themeFilterKey: filterKeyForView,
            cancellationToken: cancellationToken);

        return View("~/Views/Modern/Work/ByTheme.cshtml");
    }

    [HttpGet("directorates")]
    public async Task<IActionResult> Directorates(
        string? tab,
        string? search,
        string? workItems,
        int? directorateId,
        int? all,
        string? directorateKey = null,
        int page = 1,
        CancellationToken cancellationToken = default)
    {
        ViewBag.MainNavSection = "work";
        ViewBag.SubNavItem = "work-directorates";
        ViewBag.Search = search;
        var activeTab = NormalizeWorkRegisterTab(tab);
        ViewBag.ActiveTab = activeTab;

        var workItemsNorm = (workItems ?? "all").Trim().ToLowerInvariant();
        if (workItemsNorm != "with" && workItemsNorm != "without")
            workItemsNorm = "all";
        ViewBag.WorkItemsFilter = workItemsNorm;

        var keyFromQuery = Request.Query["directorateKey"].FirstOrDefault();
        var noExplicitSelection = !directorateId.HasValue && all != 1
            && string.IsNullOrWhiteSpace(keyFromQuery)
            && string.IsNullOrWhiteSpace(search)
            && workItemsNorm == "all"
            && page <= 1;
        if (noExplicitSelection && Request.Cookies.TryGetValue(DefaultDirectorateCookieName, out var dirCookieVal)
            && int.TryParse(dirCookieVal, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cookieDirId))
        {
            var cookieOk = await _context.Divisions.AsNoTracking()
                .AnyAsync(d => d.IsActive && d.Id == cookieDirId, cancellationToken);
            if (cookieOk)
                return RedirectToAction(nameof(Directorates), new { directorateId = cookieDirId });
        }

        var directorateKeyRaw = string.IsNullOrWhiteSpace(directorateKey?.Trim())
            ? null
            : directorateKey.Trim();
        if (string.IsNullOrWhiteSpace(directorateKeyRaw))
        {
            if (all == 1)
                directorateKeyRaw = "all";
            else if (directorateId.HasValue)
                directorateKeyRaw = directorateId.Value.ToString(CultureInfo.InvariantCulture);
        }

        var showAll = string.Equals(directorateKeyRaw, "all", StringComparison.OrdinalIgnoreCase);
        int? explicitDirectorateId = null;
        if (!showAll && !string.IsNullOrWhiteSpace(directorateKeyRaw)
            && int.TryParse(directorateKeyRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedDir) && parsedDir > 0)
        {
            explicitDirectorateId = parsedDir;
        }

        var navRows = await _context.Divisions.AsNoTracking()
            .Where(d => d.IsActive)
            .OrderBy(d => d.Name)
            .ToListAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            navRows = navRows.Where(d => d.Name.Contains(s, StringComparison.OrdinalIgnoreCase)
                || (d.Description != null && d.Description.Contains(s, StringComparison.OrdinalIgnoreCase))).ToList();
        }

        var allWorkItems = await _modernWork.GetByPriorityWorkItemsAsync(
            null, null, null, null, null, cancellationToken);

        var workItemsByDirectorateId = new Dictionary<int, List<WorkItem>>();
        foreach (var wi in allWorkItems)
        {
            if (wi.Directorates == null) continue;
            foreach (var d in wi.Directorates)
            {
                if (!workItemsByDirectorateId.ContainsKey(d.DirectorateId))
                    workItemsByDirectorateId[d.DirectorateId] = new List<WorkItem>();
                workItemsByDirectorateId[d.DirectorateId].Add(wi);
            }
        }

        foreach (var key in workItemsByDirectorateId.Keys.ToList())
        {
            workItemsByDirectorateId[key] = workItemsByDirectorateId[key]
                .GroupBy(x => x.Id).Select(g => g.First()).OrderBy(x => x.Title).ToList();
        }

        var counts = workItemsByDirectorateId.ToDictionary(kv => kv.Key, kv => kv.Value.Count);

        var navCandidates = navRows.Select(d => new Directorate
        {
            Id = d.Id,
            Name = d.Name,
            Description = d.Description,
            IsActive = d.IsActive
        }).ToList();

        List<Directorate> navDirectorates;
        if (workItemsNorm == "with")
            navDirectorates = navCandidates.Where(d => counts.TryGetValue(d.Id, out var c) && c > 0).ToList();
        else if (workItemsNorm == "without")
            navDirectorates = navCandidates.Where(d => !counts.TryGetValue(d.Id, out var c) || c == 0).ToList();
        else
            navDirectorates = navCandidates;

        ViewBag.CountBeforeWorkFilter = navCandidates.Count;

        IReadOnlyList<WorkItem> sourceList;
        int? selectedDirectorateId = null;
        if (showAll)
        {
            sourceList = allWorkItems.GroupBy(w => w.Id).Select(g => g.First()).OrderBy(w => w.Title).ToList();
        }
        else
        {
            if (explicitDirectorateId.HasValue && navDirectorates.Any(d => d.Id == explicitDirectorateId.Value))
                selectedDirectorateId = explicitDirectorateId;
            else
                selectedDirectorateId = navDirectorates.FirstOrDefault(d => counts.TryGetValue(d.Id, out var cc) && cc > 0)?.Id
                    ?? navDirectorates.FirstOrDefault()?.Id;

            if (selectedDirectorateId.HasValue)
            {
                sourceList = workItemsByDirectorateId.TryGetValue(selectedDirectorateId.Value, out var items)
                    ? items
                    : new List<WorkItem>();
            }
            else
            {
                sourceList = new List<WorkItem>();
            }
        }

        sourceList = FilterWorkItemsByRegisterTab(sourceList, activeTab);

        var pageSize = WorkGroupingRegisterPageSize;
        var total = sourceList.Count;
        var pageCount = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
        var clampedPage = Math.Max(1, Math.Min(page, pageCount));
        var paged = sourceList.Skip((clampedPage - 1) * pageSize).Take(pageSize).ToList();
        var rowStart = total == 0 ? 0 : (clampedPage - 1) * pageSize + 1;
        var rowEnd = total == 0 ? 0 : Math.Min(clampedPage * pageSize, total);

        var phaseNames = await _context.PhaseLookups.AsNoTracking()
            .Where(p => p.IsActive)
            .ToDictionaryAsync(p => p.Id, p => p.Name ?? "", cancellationToken);
        var priorityNames = await _context.DeliveryPriorities.AsNoTracking()
            .ToDictionaryAsync(p => p.Id, p => p.Name ?? "", cancellationToken);
        var allItemIds = allWorkItems.Select(w => w.Id).ToList();
        var primaryContactById = allItemIds.Count > 0
            ? await _context.Projects.AsNoTracking()
                .Where(p => allItemIds.Contains(p.Id) && p.PrimaryContactUser != null)
                .Select(p => new { p.Id, Name = p.PrimaryContactUser!.Name ?? p.PrimaryContactUser.Email })
                .ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken)
            : new Dictionary<int, string>();

        var dirProjectIds = workItemsByDirectorateId.Values.SelectMany(w => w).Select(x => x.Id).Distinct().ToList();
        var businessAreaByIdDir = dirProjectIds.Count > 0
            ? await _context.Projects.AsNoTracking()
                .Where(p => dirProjectIds.Contains(p.Id))
                .Select(p => new
                {
                    p.Id,
                    Name = p.BusinessAreaLookup != null
                        ? p.BusinessAreaLookup.Name
                        : (p.PrimaryOrganizationalGroup != null ? p.PrimaryOrganizationalGroup.Name : null)
                })
                .ToDictionaryAsync(x => x.Id, x => x.Name ?? "—", cancellationToken)
            : new Dictionary<int, string>();

        ViewBag.DirectoratesNav = navDirectorates;
        ViewBag.WorkCountByDirectorateId = counts;
        ViewBag.SelectedDirectorateId = selectedDirectorateId;
        ViewBag.ShowAllDirectorates = showAll;
        ViewBag.PagedWorkItems = paged;
        ViewBag.RegisterPage = clampedPage;
        ViewBag.RegisterPageCount = pageCount;
        ViewBag.RegisterTotalCount = total;
        ViewBag.RegisterDisplayRowStart = rowStart;
        ViewBag.RegisterDisplayRowEnd = rowEnd;
        ViewBag.RegisterIsPaginated = total > pageSize;
        ViewBag.PhaseNames = phaseNames;
        ViewBag.PriorityNames = priorityNames;
        ViewBag.PrimaryContactById = primaryContactById;
        ViewBag.BusinessAreaNameByProjectId = businessAreaByIdDir;
        ViewBag.CanSetDefaultDirectorateView = selectedDirectorateId.HasValue && !showAll;

        string? filterKeyForView = directorateKeyRaw;
        if (string.IsNullOrWhiteSpace(filterKeyForView))
        {
            if (showAll)
                filterKeyForView = "all";
            else if (selectedDirectorateId.HasValue)
                filterKeyForView = selectedDirectorateId.Value.ToString(CultureInfo.InvariantCulture);
        }

        ViewBag.DirectorateFilterKey = filterKeyForView;

        int? cookieDefaultDir = null;
        if (Request.Cookies.TryGetValue(DefaultDirectorateCookieName, out var cookieDirStr)
            && int.TryParse(cookieDirStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cookieDirParsed))
        {
            cookieDefaultDir = cookieDirParsed;
        }

        ViewBag.SavedDefaultDirectorateId = cookieDefaultDir;
        ViewBag.IsCurrentViewSavedDefault = cookieDefaultDir.HasValue
            && cookieDefaultDir.Value == selectedDirectorateId
            && selectedDirectorateId.HasValue
            && !showAll;

        ViewBag.WorkRegisterSubNav = await BuildWorkRegisterSubNavForScopeAsync(
            businessAreaId: null,
            directorateId: showAll ? null : selectedDirectorateId,
            search,
            ragId: null,
            priorityId: null,
            activeTab,
            nameof(Directorates),
            directorateFilterKey: filterKeyForView,
            cancellationToken: cancellationToken);

        return View("~/Views/Modern/Work/Directorates.cshtml");
    }

    [HttpPost("directorates/default-view")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetDefaultDirectorateView([FromForm] int directorateId, [FromForm] string? returnUrl, CancellationToken cancellationToken = default)
    {
        var exists = await _context.Divisions.AsNoTracking()
            .AnyAsync(d => d.IsActive && d.Id == directorateId, cancellationToken);
        if (!exists)
            return NotFound();

        Response.Cookies.Append(DefaultDirectorateCookieName, directorateId.ToString(CultureInfo.InvariantCulture), new CookieOptions
        {
            Path = "/",
            IsEssential = true,
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = Request.IsHttps,
            MaxAge = TimeSpan.FromDays(365)
        });

        TempData["SuccessMessage"] = "Your default directorate view has been saved.";
        if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
            return LocalRedirect(returnUrl);
        return RedirectToAction(nameof(Directorates), new { directorateId });
    }

    [HttpPost("directorates/default-view/clear")]
    [ValidateAntiForgeryToken]
    public IActionResult ClearDefaultDirectorateView()
    {
        Response.Cookies.Delete(DefaultDirectorateCookieName, new CookieOptions
        {
            Path = "/",
            HttpOnly = true,
            Secure = Request.IsHttps,
            SameSite = SameSiteMode.Lax
        });
        return RedirectToAction(nameof(Directorates), new { directorateKey = "all" });
    }

    [HttpGet("business-areas")]
    public async Task<IActionResult> BusinessAreas(
        string? tab,
        string? search,
        int? businessAreaId,
        bool unassigned = false,
        int? all = null,
        string? businessAreaKey = null,
        string? status = null,
        int? ragStatusId = null,
        int? priorityId = null,
        bool showCancelled = false,
        int page = 1,
        CancellationToken cancellationToken = default)
    {
        ViewBag.MainNavSection = "work";
        ViewBag.SubNavItem = "work-business-areas";
        ViewBag.Search = search;
        var activeTab = NormalizeWorkRegisterTab(tab);
        ViewBag.ActiveTab = activeTab;

        var statusFilter = string.IsNullOrWhiteSpace(status) ? null : status.Trim();
        if (statusFilter != null && !WorkRegisterStatusFilterValues.Contains(statusFilter))
            statusFilter = null;

        var hasItemFilters = statusFilter != null || ragStatusId.HasValue || priorityId.HasValue;

        var keyFromQuery = Request.Query["businessAreaKey"].FirstOrDefault();
        var noExplicitSelection = !businessAreaId.HasValue && !unassigned && all != 1
            && string.IsNullOrWhiteSpace(keyFromQuery)
            && string.IsNullOrWhiteSpace(search)
            && !hasItemFilters
            && !showCancelled
            && page <= 1;
        if (noExplicitSelection && Request.Cookies.TryGetValue(DefaultBusinessAreaCookieName, out var cookieVal)
            && int.TryParse(cookieVal, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cookieBaId))
        {
            var cookieOk = await _context.BusinessAreaLookups.AsNoTracking()
                .AnyAsync(b => b.IsActive && b.Id == cookieBaId, cancellationToken);
            if (cookieOk)
                return RedirectToAction(nameof(BusinessAreas), new { businessAreaId = cookieBaId });
        }

        var businessAreaKeyRaw = string.IsNullOrWhiteSpace(businessAreaKey?.Trim())
            ? null
            : businessAreaKey.Trim();
        if (string.IsNullOrWhiteSpace(businessAreaKeyRaw))
        {
            if (all == 1)
                businessAreaKeyRaw = "all";
            else if (unassigned)
                businessAreaKeyRaw = "unassigned";
            else if (businessAreaId.HasValue)
                businessAreaKeyRaw = businessAreaId.Value.ToString(CultureInfo.InvariantCulture);
        }

        var showAll = string.Equals(businessAreaKeyRaw, "all", StringComparison.OrdinalIgnoreCase);
        var showUnassignedSelected = string.Equals(businessAreaKeyRaw, "unassigned", StringComparison.OrdinalIgnoreCase);
        int? explicitBusinessAreaId = null;
        if (!showAll && !showUnassignedSelected && !string.IsNullOrWhiteSpace(businessAreaKeyRaw)
            && int.TryParse(businessAreaKeyRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedBa) && parsedBa > 0)
        {
            explicitBusinessAreaId = parsedBa;
        }

        var navRows = await _context.BusinessAreaLookups.AsNoTracking()
            .Where(b => b.IsActive)
            .OrderBy(b => b.SortOrder)
            .ThenBy(b => b.Name)
            .ToListAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            navRows = navRows.Where(b => b.Name.Contains(s, StringComparison.OrdinalIgnoreCase)
                || (b.Description != null && b.Description.Contains(s, StringComparison.OrdinalIgnoreCase))).ToList();
        }

        var allWorkItems = await _modernWork.GetByPriorityWorkItemsAsync(
            null, null, null, null, null, cancellationToken);

        IEnumerable<WorkItem> workItemsForGrouping = allWorkItems;
        if (statusFilter != null)
            workItemsForGrouping = workItemsForGrouping.Where(w =>
                string.Equals(w.Status, statusFilter, StringComparison.OrdinalIgnoreCase));
        if (priorityId.HasValue)
            workItemsForGrouping = workItemsForGrouping.Where(w => w.PriorityId == priorityId.Value);
        if (ragStatusId.HasValue)
            workItemsForGrouping = workItemsForGrouping.Where(w =>
                BusinessAreaWorkItemMatchesRag(w, ragStatusId.Value));
        if (hasItemFilters && !showCancelled
            && !string.Equals(statusFilter, "Cancelled", StringComparison.OrdinalIgnoreCase))
            workItemsForGrouping = workItemsForGrouping.Where(w =>
                !string.Equals(w.Status, "Cancelled", StringComparison.OrdinalIgnoreCase));

        var filteredWorkItems = workItemsForGrouping.ToList();

        var allItemIds = filteredWorkItems.Select(w => w.Id).ToList();
        var businessAreaIdByProjectId = allItemIds.Count == 0
            ? new Dictionary<int, int?>()
            : await _context.Projects.AsNoTracking()
                .Where(p => allItemIds.Contains(p.Id))
                .Select(p => new { p.Id, p.BusinessAreaId })
                .ToDictionaryAsync(x => x.Id, x => x.BusinessAreaId, cancellationToken);

        var workItemsByBusinessAreaId = new Dictionary<int, List<WorkItem>>();
        var unassignedItems = new List<WorkItem>();
        foreach (var wi in filteredWorkItems)
        {
            if (!businessAreaIdByProjectId.TryGetValue(wi.Id, out var baId) || !baId.HasValue)
            {
                unassignedItems.Add(wi);
                continue;
            }

            var id = baId.Value;
            if (!workItemsByBusinessAreaId.TryGetValue(id, out var list))
            {
                list = new List<WorkItem>();
                workItemsByBusinessAreaId[id] = list;
            }

            list.Add(wi);
        }

        foreach (var key in workItemsByBusinessAreaId.Keys.ToList())
        {
            workItemsByBusinessAreaId[key] = workItemsByBusinessAreaId[key]
                .GroupBy(x => x.Id).Select(g => g.First()).OrderBy(x => x.Title).ToList();
        }

        unassignedItems = unassignedItems.GroupBy(x => x.Id).Select(g => g.First()).OrderBy(x => x.Title).ToList();

        var counts = workItemsByBusinessAreaId.ToDictionary(kv => kv.Key, kv => kv.Value.Count);
        var unassignedCount = unassignedItems.Count;

        var navCandidates = navRows.ToList();
        ViewBag.CountBeforeWorkFilter = navCandidates.Count;

        var navBusinessAreas = navCandidates;

        var showUnassignedNav = unassignedCount > 0;

        IReadOnlyList<WorkItem> sourceList;
        int? selectedBusinessAreaId = null;
        if (showAll)
        {
            sourceList = filteredWorkItems.GroupBy(w => w.Id).Select(g => g.First()).OrderBy(w => w.Title).ToList();
        }
        else if (showUnassignedSelected)
        {
            sourceList = unassignedItems;
        }
        else
        {
            if (explicitBusinessAreaId.HasValue && navBusinessAreas.Any(b => b.Id == explicitBusinessAreaId.Value))
                selectedBusinessAreaId = explicitBusinessAreaId;
            else
                selectedBusinessAreaId = navBusinessAreas.FirstOrDefault(b => counts.TryGetValue(b.Id, out var cc) && cc > 0)?.Id
                    ?? navBusinessAreas.FirstOrDefault()?.Id;

            if (selectedBusinessAreaId.HasValue)
            {
                sourceList = workItemsByBusinessAreaId.TryGetValue(selectedBusinessAreaId.Value, out var items)
                    ? items
                    : new List<WorkItem>();
            }
            else
            {
                sourceList = new List<WorkItem>();
            }
        }

        sourceList = FilterWorkItemsByRegisterTab(sourceList, activeTab);

        var pageSize = WorkGroupingRegisterPageSize;
        var total = sourceList.Count;
        var pageCount = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
        var clampedPage = Math.Max(1, Math.Min(page, pageCount));
        var paged = sourceList.Skip((clampedPage - 1) * pageSize).Take(pageSize).ToList();
        var rowStart = total == 0 ? 0 : (clampedPage - 1) * pageSize + 1;
        var rowEnd = total == 0 ? 0 : Math.Min(clampedPage * pageSize, total);

        var phaseNames = await _context.PhaseLookups.AsNoTracking()
            .Where(p => p.IsActive)
            .ToDictionaryAsync(p => p.Id, p => p.Name ?? "", cancellationToken);
        var priorityNames = await _context.DeliveryPriorities.AsNoTracking()
            .ToDictionaryAsync(p => p.Id, p => p.Name ?? "", cancellationToken);
        var primaryContactById = allItemIds.Count > 0
            ? await _context.Projects.AsNoTracking()
                .Where(p => allItemIds.Contains(p.Id) && p.PrimaryContactUser != null)
                .Select(p => new { p.Id, Name = p.PrimaryContactUser!.Name ?? p.PrimaryContactUser.Email })
                .ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken)
            : new Dictionary<int, string>();

        var baProjectIds = workItemsByBusinessAreaId.Values.SelectMany(w => w).Select(x => x.Id)
            .Concat(unassignedItems.Select(x => x.Id)).Distinct().ToList();
        var businessAreaByIdFromProjects = baProjectIds.Count > 0
            ? await _context.Projects.AsNoTracking()
                .Where(p => baProjectIds.Contains(p.Id))
                .Select(p => new
                {
                    p.Id,
                    Name = p.BusinessAreaLookup != null
                        ? p.BusinessAreaLookup.Name
                        : (p.PrimaryOrganizationalGroup != null ? p.PrimaryOrganizationalGroup.Name : null)
                })
                .ToDictionaryAsync(x => x.Id, x => x.Name ?? "—", cancellationToken)
            : new Dictionary<int, string>();

        ViewBag.BusinessAreasNav = navBusinessAreas;
        ViewBag.WorkCountByBusinessAreaId = counts;
        ViewBag.UnassignedCount = unassignedCount;
        ViewBag.ShowUnassignedNav = showUnassignedNav;
        ViewBag.SelectedBusinessAreaId = selectedBusinessAreaId;
        ViewBag.ShowUnassignedSelected = showUnassignedSelected;
        ViewBag.ShowAllBusinessAreas = showAll;
        ViewBag.PagedWorkItems = paged;
        ViewBag.RegisterPage = clampedPage;
        ViewBag.RegisterPageCount = pageCount;
        ViewBag.RegisterTotalCount = total;
        ViewBag.RegisterDisplayRowStart = rowStart;
        ViewBag.RegisterDisplayRowEnd = rowEnd;
        ViewBag.RegisterIsPaginated = total > pageSize;
        ViewBag.PhaseNames = phaseNames;
        ViewBag.PriorityNames = priorityNames;
        ViewBag.PrimaryContactById = primaryContactById;
        ViewBag.BusinessAreaNameByProjectId = businessAreaByIdFromProjects;
        ViewBag.CanSetDefaultBusinessAreaView = selectedBusinessAreaId.HasValue && !showUnassignedSelected && !showAll;

        string? filterKeyForView = businessAreaKeyRaw;
        if (string.IsNullOrWhiteSpace(filterKeyForView))
        {
            if (showAll)
                filterKeyForView = "all";
            else if (showUnassignedSelected)
                filterKeyForView = "unassigned";
            else if (selectedBusinessAreaId.HasValue)
                filterKeyForView = selectedBusinessAreaId.Value.ToString(CultureInfo.InvariantCulture);
        }

        ViewBag.BusinessAreaFilterKey = filterKeyForView;

        var ragStatusOptions = await _context.RagStatusLookups.AsNoTracking()
            .Where(r => r.IsActive)
            .OrderBy(r => r.SortOrder)
            .Select(r => new RagStatusLookupOption { Id = r.Id, Name = r.Name ?? "" })
            .ToListAsync(cancellationToken);

        var priorityOptions = await _context.DeliveryPriorities.AsNoTracking()
            .OrderBy(p => p.SortOrder)
            .Select(p => new WorkLookupOption { Id = p.Id, Name = p.Name ?? "", Value = p.Name ?? "" })
            .ToListAsync(cancellationToken);

        ViewBag.StatusFilter = statusFilter;
        ViewBag.RagStatusId = ragStatusId;
        ViewBag.PriorityId = priorityId;
        ViewBag.ShowCancelled = showCancelled;
        ViewBag.HasItemFilters = hasItemFilters;
        ViewBag.RagStatusOptions = ragStatusOptions;
        ViewBag.PriorityOptions = priorityOptions;

        if (hasItemFilters)
        {
            var toggleRd = new RouteValueDictionary
            {
                ["tab"] = activeTab,
                ["businessAreaKey"] = filterKeyForView ?? "all",
                ["page"] = clampedPage
            };
            if (!string.IsNullOrWhiteSpace(search)) toggleRd["search"] = search;
            if (statusFilter != null) toggleRd["status"] = statusFilter;
            if (ragStatusId.HasValue) toggleRd["ragStatusId"] = ragStatusId.Value;
            if (priorityId.HasValue) toggleRd["priorityId"] = priorityId.Value;
            if (!showCancelled)
                toggleRd["showCancelled"] = true;
            ViewBag.CancelledToggleUrl = Url.Action(nameof(BusinessAreas), "ModernWork", toggleRd);
        }

        int? cookieDefaultBa = null;
        if (Request.Cookies.TryGetValue(DefaultBusinessAreaCookieName, out var cookieBaStr)
            && int.TryParse(cookieBaStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cookieBaParsed))
        {
            cookieDefaultBa = cookieBaParsed;
        }

        ViewBag.SavedDefaultBusinessAreaId = cookieDefaultBa;
        ViewBag.IsCurrentViewSavedDefault = cookieDefaultBa.HasValue
            && cookieDefaultBa.Value == selectedBusinessAreaId
            && selectedBusinessAreaId.HasValue
            && !showUnassignedSelected
            && !showAll;

        ViewBag.WorkRegisterSubNav = await BuildWorkRegisterSubNavForScopeAsync(
            businessAreaId: showAll || showUnassignedSelected ? null : selectedBusinessAreaId,
            directorateId: null,
            search,
            ragId: ragStatusId,
            priorityId,
            activeTab,
            nameof(BusinessAreas),
            businessAreaFilterKey: filterKeyForView,
            cancellationToken: cancellationToken);

        return View("~/Views/Modern/Work/BusinessAreas.cshtml");
    }

    [HttpPost("business-areas/default-view")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetDefaultBusinessAreaView([FromForm] int businessAreaId, [FromForm] string? returnUrl, CancellationToken cancellationToken = default)
    {
        var exists = await _context.BusinessAreaLookups.AsNoTracking()
            .AnyAsync(b => b.IsActive && b.Id == businessAreaId, cancellationToken);
        if (!exists)
            return NotFound();

        Response.Cookies.Append(DefaultBusinessAreaCookieName, businessAreaId.ToString(CultureInfo.InvariantCulture), new CookieOptions
        {
            Path = "/",
            IsEssential = true,
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = Request.IsHttps,
            MaxAge = TimeSpan.FromDays(365)
        });

        TempData["SuccessMessage"] = "Your default business area view has been saved.";
        if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
            return LocalRedirect(returnUrl);
        return RedirectToAction(nameof(BusinessAreas), new { businessAreaId });
    }

    [HttpPost("business-areas/default-view/clear")]
    [ValidateAntiForgeryToken]
    public IActionResult ClearDefaultBusinessAreaView()
    {
        Response.Cookies.Delete(DefaultBusinessAreaCookieName, new CookieOptions
        {
            Path = "/",
            HttpOnly = true,
            Secure = Request.IsHttps,
            SameSite = SameSiteMode.Lax
        });
        return RedirectToAction(nameof(BusinessAreas), new { businessAreaKey = "all" });
    }

    [HttpGet("{id:int}/multi-dept/edit")]
    [HttpGet("/ModernWork/EditMultiDeptCooperation/{id:int}")]
    public async Task<IActionResult> EditMultiDeptCooperation(int id, CancellationToken cancellationToken = default)
    {
        ViewBag.MainNavSection = "work";

        var userEmail = User.Identity?.Name;
        if (string.IsNullOrEmpty(userEmail))
            return Unauthorized();

        var currentUser = await _context.Users
            .FirstOrDefaultAsync(u => u.Email.ToLower() == userEmail.ToLower(), cancellationToken);
        if (currentUser == null)
            return Unauthorized();

        var deny = await EnsureUserCanEditWorkAsync(id, cancellationToken);
        if (deny != null)
            return deny;

        var work = await _modernWork.PopulateWorkDetailAsync(
            this, id, currentUser, userEmail, "strategicalignment", null, cancellationToken);
        if (work == null)
            return NotFound();

        ViewBag.WorkItem = work;
        ViewBag.WorkChromeSubPage = true;
        ViewBag.WorkChromeSection = "strategicalignment";
        return View("~/Views/Modern/Work/EditMultiDeptCooperation.cshtml", work);
    }

    [HttpPost("{id:int}/multi-dept/edit")]
    [HttpPost("/ModernWork/EditMultiDeptCooperation/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditMultiDeptCooperationPost(int id, [FromForm] int[]? GovernmentDepartmentIds, CancellationToken cancellationToken = default)
    {
        var deny = await EnsureUserCanEditWorkAsync(id, cancellationToken);
        if (deny != null)
            return deny;

        var project = await _context.Projects
            .FirstOrDefaultAsync(p => p.Id == id && !p.IsDeleted, cancellationToken);
        if (project == null)
            return NotFound();

        if (GovernmentDepartmentIds != null && GovernmentDepartmentIds.Length > 0)
        {
            var validIds = await _context.GovernmentDepartments.AsNoTracking()
                .Where(g => GovernmentDepartmentIds.Contains(g.Id))
                .Select(g => g.Id)
                .ToArrayAsync(cancellationToken);
            project.OtherDepartments = JsonSerializer.Serialize(validIds);
            project.IsMultiDepartmentProject = validIds.Length > 0;
        }
        else
        {
            project.OtherDepartments = null;
            project.IsMultiDepartmentProject = false;
        }

        await _context.SaveChangesAsync(cancellationToken);
        TempData["Message"] = "Multi-department cooperation updated.";
        var detailUrl = Url.Action(nameof(Detail), new { id });
        return string.IsNullOrEmpty(detailUrl)
            ? RedirectToAction(nameof(Detail), new { id })
            : LocalRedirect(detailUrl + "#wd-strategic-alignment");
    }

    /// <summary>
    /// Modern work forms bind business area to <see cref="WorkItem.PortfolioId"/>.
    /// <see cref="Project.BusinessAreaId"/> is a <see cref="BusinessAreaLookup"/> FK;
    /// <see cref="Project.PrimaryOrganizationalGroupId"/> is an <see cref="OrganizationalGroup"/> FK.
    /// </summary>
    private async Task<(bool ok, string? error)> ApplyPortfolioSelectionAsync(
        Project project,
        int? portfolioId,
        CancellationToken cancellationToken)
    {
        if (!portfolioId.HasValue)
        {
            project.BusinessAreaId = null;
            project.PrimaryOrganizationalGroupId = null;
            return (true, null);
        }

        var id = portfolioId.Value;

        var isBusinessArea = await _context.BusinessAreaLookups.AsNoTracking()
            .AnyAsync(b => b.Id == id && b.IsActive, cancellationToken);
        if (isBusinessArea)
        {
            project.BusinessAreaId = id;
            project.PrimaryOrganizationalGroupId = null;
            return (true, null);
        }

        var isOrgGroup = await _context.OrganizationalGroups.AsNoTracking()
            .AnyAsync(g => g.Id == id && g.IsActive, cancellationToken);
        if (isOrgGroup)
        {
            project.PrimaryOrganizationalGroupId = id;
            return (true, null);
        }

        return (false, "Select a valid business area.");
    }

    [HttpGet("search-government-departments")]
    [HttpGet("/ModernWork/SearchGovernmentDepartments")]
    public async Task<IActionResult> SearchGovernmentDepartments([FromQuery] string? q, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(q) || q.Length < 2)
            return Json(Array.Empty<object>());

        var term = q.Trim().ToLower();
        var results = await _context.GovernmentDepartments.AsNoTracking()
            .Where(g => g.Title.ToLower().Contains(term)
                     || (g.Abbreviation != null && g.Abbreviation.ToLower().Contains(term)))
            .OrderBy(g => g.Title)
            .Take(20)
            .Select(g => new { id = g.Id, name = g.Title })
            .ToListAsync(cancellationToken);

        return Json(results);
    }
}
