using Compass.Attributes;
using Compass.Data;
using Compass.Models;
using Compass.Models.DemandPipeline;
using Compass.Models.Fips;
using Compass.Services;
using Compass.Services.Api;
using Compass.Services.DemandPipeline;
using Compass.Services.Fips;
using Compass.ViewModels.Modern;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Compass.Controllers.Modern;

/// <summary>Modern admin hub at <c>/modern/admin/*</c>, aligned with Compass2 admin (sidebar + hub panels).</summary>
[Authorize]
[RequireAdmin]
[Route("modern/admin")]
public partial class ModernAdminController : Controller
{
    private readonly CompassDbContext _context;
    private readonly IDemandScoringFrameworkService _demandScoringFramework;
    private readonly IProductsApiService _productsApi;
    private readonly IApiTokenService _apiTokenService;
    private readonly IFipsBusinessAreaLookupSyncService _fipsBusinessAreaLookupSync;
    private readonly IGlobalFeatureToggleService _globalFeatureToggle;
    private readonly IWeeklyUpdateService _weeklyUpdateService;

    private static readonly HashSet<string> AdminHubValidPanels = new(StringComparer.OrdinalIgnoreCase)
    {
        "hub",
        "business-areas", "directorates", "phases", "priorities", "risk-tiers",
        "risk-categories", "issue-categories",
        "product-types", "rag-defns", "departments", "compliance",
        "scoring-fw", "perf-cycles", "assess-cycles",
        "groups", "api-tokens", "api-token-requests", "cms-access-products", "business-area-admins", "business-area-leadership", "directorate-leadership",
        "migration", "environment-sync", "feature-settings", "universal-barriers",
        "activity-types", "work-tagging", "resource-bands", "mission-pillars", "priority-outcomes",
        "fips-channels", "fips-types", "fips-business-areas", "fips-user-groups", "fips-contact-roles", "fips-categorisation",
        "std-categories", "std-subcategories", "std-functional",
        "risk-statuses", "risk-priorities", "risk-likelihoods", "risk-impact-levels", "risk-proximities",
        "risk-treatments",
        "risk-appetites",
        "action-statuses", "action-priorities", "action-types", "action-categories",
        "action-impact-levels", "action-reminder-frequencies", "action-escalation-thresholds",
        "issue-statuses", "issue-priorities", "issue-severities",
        "decision-statuses", "decision-priorities", "decision-outcomes", "decision-implementation-statuses",
        "raid-evidence-types", "governance-boards", "demand-request-statuses", "triage-outcome-stages",
        "assumption-statuses", "assumption-criticalities", "dependency-criticalities", "dependency-link-types",
        "near-miss-types", "near-miss-seriousness", "near-miss-statuses",
        "raid-register-table-view"
    };

    public ModernAdminController(
        CompassDbContext context,
        IDemandScoringFrameworkService demandScoringFramework,
        IProductsApiService productsApi,
        IApiTokenService apiTokenService,
        IFipsBusinessAreaLookupSyncService fipsBusinessAreaLookupSync,
        IGlobalFeatureToggleService globalFeatureToggle,
        IWeeklyUpdateService weeklyUpdateService)
    {
        _context = context;
        _demandScoringFramework = demandScoringFramework;
        _productsApi = productsApi;
        _apiTokenService = apiTokenService;
        _fipsBusinessAreaLookupSync = fipsBusinessAreaLookupSync;
        _globalFeatureToggle = globalFeatureToggle;
        _weeklyUpdateService = weeklyUpdateService;
    }

    private void SetAdminChrome(string subNavItem)
    {
        ViewBag.MainNavSection = "admin";
        ViewBag.SubNavItem = subNavItem;
    }

    [HttpGet("")]
    [HttpGet("index")]
    public async Task<IActionResult> Index(string? panel = null, string? entity = null)
    {
        SetAdminChrome("admin-index");

        var normalized = string.IsNullOrWhiteSpace(panel) ? "hub" : panel.Trim().ToLowerInvariant();
        if (string.Equals(normalized, "reporting-cycles", StringComparison.OrdinalIgnoreCase))
            return RedirectToAction(nameof(WorkReporting));
        if (string.Equals(normalized, "audit", StringComparison.OrdinalIgnoreCase))
            return RedirectToAction(nameof(AuditExplorer));
        if (string.Equals(normalized, "risk-cats", StringComparison.OrdinalIgnoreCase))
            return RedirectToAction(nameof(Index), new { panel = "risk-categories" });
        if (string.Equals(normalized, "risk-origins", StringComparison.OrdinalIgnoreCase))
            return RedirectToAction(nameof(Index), new { panel = "hub" });
        if (string.Equals(normalized, "issue-types", StringComparison.OrdinalIgnoreCase))
            return RedirectToAction(nameof(Index), new { panel = "issue-categories" });
        if (!AdminHubValidPanels.Contains(normalized))
            normalized = "hub";

        if (normalized.StartsWith("fips-", StringComparison.OrdinalIgnoreCase))
        {
            var fipsGuard = await RequireFipsDatabaseAdminAsync();
            if (fipsGuard != null)
                return fipsGuard;
        }

        var vm = new AdminHubViewModel { Panel = normalized };

        switch (normalized)
        {
            case "hub":
                break;

            case "business-areas":
                vm.BusinessAreas = await _context.BusinessAreaLookups.AsNoTracking()
                    .OrderBy(x => x.SortOrder).ThenBy(x => x.Name)
                    .Select(x => new AdminLookupRow { Id = x.Id, Name = x.Name, Description = x.Description, SortOrder = x.SortOrder, IsActive = x.IsActive })
                    .ToListAsync();
                break;

            case "phases":
                vm.Phases = await _context.PhaseLookups.AsNoTracking()
                    .OrderBy(x => x.SortOrder).ThenBy(x => x.Name)
                    .Select(x => new AdminLookupRow { Id = x.Id, Name = x.Name, Description = x.Description, SortOrder = x.SortOrder, IsActive = x.IsActive })
                    .ToListAsync();
                break;

            case "directorates":
                vm.Directorates = await _context.DirectorateLookups.AsNoTracking()
                    .OrderBy(x => x.SortOrder).ThenBy(x => x.Name)
                    .Select(x => new AdminLookupRow { Id = x.Id, Name = x.Name, Description = x.Description, SortOrder = x.SortOrder, IsActive = x.IsActive })
                    .ToListAsync();
                break;

            case "priorities":
                vm.Priorities = await _context.DeliveryPriorities.AsNoTracking()
                    .OrderBy(x => x.SortOrder).ThenBy(x => x.Name)
                    .Select(x => new AdminPriorityRow { Id = x.Id, Name = x.Name, Summary = x.Summary, Description = x.Description, CssClass = x.CssClass, SortOrder = x.SortOrder, IsActive = x.IsActive })
                    .ToListAsync();
                break;

            case "rag-defns":
                vm.RagDefinitions = await _context.RagStatusLookups.AsNoTracking()
                    .OrderBy(x => x.SortOrder).ThenBy(x => x.Name)
                    .Select(x => new AdminRagRow { Id = x.Id, Name = x.Name, Description = x.Description, CssClass = x.CssClass, SortOrder = x.SortOrder, IsActive = x.IsActive })
                    .ToListAsync();
                break;

            case "universal-barriers":
                vm.UniversalBarriers = await _context.UniversalBarrierLookups.AsNoTracking()
                    .OrderBy(x => x.SortOrder).ThenBy(x => x.Name)
                    .Select(x => new AdminLookupRow { Id = x.Id, Name = x.Name, Description = x.Description, GuidanceUrl = x.GuidanceUrl, SortOrder = x.SortOrder, IsActive = x.IsActive })
                    .ToListAsync();
                break;

            case "activity-types":
                vm.ActivityTypes = await _context.ActivityTypeLookups.AsNoTracking()
                    .OrderBy(x => x.SortOrder).ThenBy(x => x.Name)
                    .Select(x => new AdminLookupRow { Id = x.Id, Name = x.Name, Description = x.Description, SortOrder = x.SortOrder, IsActive = x.IsActive })
                    .ToListAsync();
                break;

            case "work-tagging":
                vm.WorkItemTags = await _context.WorkItemTagLookups.AsNoTracking()
                    .OrderBy(x => x.SortOrder).ThenBy(x => x.Name)
                    .Select(x => new AdminLookupRow { Id = x.Id, Name = x.Name, Description = x.Description, SortOrder = x.SortOrder, IsActive = x.IsActive })
                    .ToListAsync();
                break;

            case "resource-bands":
                vm.ResourceBands = await _context.ResourceBandLookups.AsNoTracking()
                    .OrderBy(x => x.SortOrder).ThenBy(x => x.Name)
                    .Select(x => new AdminResourceBandRow
                    {
                        Id = x.Id,
                        Name = x.Name,
                        Description = x.Description,
                        MinFte = x.MinFte,
                        MaxFte = x.MaxFte,
                        CssClass = x.CssClass,
                        SortOrder = x.SortOrder,
                        IsActive = x.IsActive
                    })
                    .ToListAsync();
                break;

            case "mission-pillars":
                vm.MissionPillars = await _context.Missions.AsNoTracking()
                    .OrderBy(m => m.IsDeleted).ThenBy(m => m.Title)
                    .Select(m => new AdminLookupRow { Id = m.Id, Name = m.Title, Description = m.Description, SortOrder = m.Id, IsActive = !m.IsDeleted })
                    .ToListAsync();
                break;

            case "priority-outcomes":
                vm.PriorityOutcomes = await _context.Objectives.AsNoTracking()
                    .Include(o => o.OwnerUser)
                    .Include(o => o.ThemeSroUser)
                    .Include(o => o.OutcomeSroUser)
                    .Include(o => o.Mission)
                    .OrderBy(o => o.IsDeleted).ThenBy(o => o.Title)
                    .Select(o => new AdminPriorityOutcomeRow
                    {
                        Id = o.Id,
                        Title = o.Title,
                        Theme = o.Theme,
                        Description = o.Description,
                        OwnerName = o.OwnerUser != null ? o.OwnerUser.Name : null,
                        ThemeSroName = o.ThemeSroUser != null ? o.ThemeSroUser.Name : null,
                        OutcomeSroName = o.OutcomeSroUser != null ? o.OutcomeSroUser.Name : null,
                        MissionPillar = o.Mission != null ? o.Mission.Title : null,
                        Status = o.Status,
                        IsDeleted = o.IsDeleted
                    })
                    .ToListAsync();
                break;

            case "departments":
                vm.GovernmentDepartmentRows = await _context.GovernmentDepartments.AsNoTracking()
                    .Include(d => d.ParentDepartment)
                    .Include(d => d.ChildDepartments.Where(c => !c.IsDeleted))
                    .Where(d => !d.IsDeleted)
                    .OrderBy(d => d.Title)
                    .Select(d => new AdminGovDeptRow
                    {
                        Id = d.Id,
                        Title = d.Title,
                        Abbreviation = d.Abbreviation,
                        Format = d.Format,
                        GovukStatus = d.GovukStatus,
                        IsActive = d.ClosedAt == null,
                        ChildCount = d.ChildDepartments.Count(c => !c.IsDeleted),
                        LastSyncedAt = d.LastSyncedAt,
                        ParentTitle = d.ParentDepartment != null ? d.ParentDepartment.Title : null
                    })
                    .ToListAsync();
                break;

            case "risk-tiers":
                vm.RiskTiers = await _context.RiskTiers.AsNoTracking()
                    .OrderBy(x => x.SortOrder).ThenBy(x => x.Name)
                    .Select(x => new AdminLookupRow
                    {
                        Id = x.Id,
                        Name = x.Name + (x.IsProposedTier ? " (proposed)" : ""),
                        Description = (x.GovernanceLevel > 0 ? $"Level {x.GovernanceLevel}. " : "") + (x.Description ?? ""),
                        Code = x.Code,
                        SortOrder = x.SortOrder,
                        IsActive = x.IsActive
                    })
                    .ToListAsync();
                break;

            case "raid-register-table-view":
                await PopulateRaidRegisterTableViewPanelAsync(vm, entity, HttpContext.RequestAborted);
                break;

            case "risk-statuses":
            case "risk-categories":
            case "risk-priorities":
            case "risk-likelihoods":
            case "risk-impact-levels":
            case "risk-proximities":
            case "risk-treatments":
            case "risk-appetites":
            case "action-statuses":
            case "action-priorities":
            case "action-types":
            case "action-categories":
            case "action-impact-levels":
            case "action-reminder-frequencies":
            case "action-escalation-thresholds":
            case "issue-statuses":
            case "issue-categories":
            case "issue-priorities":
            case "issue-severities":
            case "decision-statuses":
            case "decision-priorities":
            case "decision-outcomes":
            case "decision-implementation-statuses":
            case "raid-evidence-types":
            case "governance-boards":
            case "demand-request-statuses":
            case "triage-outcome-stages":
            case "assumption-statuses":
            case "assumption-criticalities":
            case "dependency-criticalities":
            case "dependency-link-types":
            case "near-miss-types":
            case "near-miss-seriousness":
            case "near-miss-statuses":
                var raidDef = FindRaidDef(normalized);
                if (raidDef != null)
                {
                    vm.RaidPanelKey = raidDef.Key;
                    vm.RaidPanelLabel = raidDef.Label;
                    vm.RaidPanelDescription = raidDef.Description;
                    vm.RaidCanSeedDefaults = Data.RaidLookupSeedData.Definitions.ContainsKey(raidDef.Key);
                    vm.RaidLookupRows = await raidDef.Query(_context).AsNoTracking()
                        .OrderBy(x => x.SortOrder).ThenBy(x => x.Label)
                        .Select(x => new AdminLookupRow { Id = x.Id, Name = x.Label, Description = x.Description, Code = x.Code, SortOrder = x.SortOrder, IsActive = x.IsActive })
                        .ToListAsync();
                }
                else if (string.Equals(normalized, "risk-appetites", StringComparison.OrdinalIgnoreCase))
                {
                    vm.RaidPanelKey = "risk-appetites";
                    vm.RaidPanelLabel = "Risk appetites";
                    vm.RaidPanelDescription = "Work-level risk appetite scale used across dashboards and detail views.";
                    vm.RaidCanSeedDefaults = Data.RaidLookupSeedData.Definitions.ContainsKey("risk-appetites");
                    vm.RaidLookupRows = await _context.RiskAppetiteLookups.AsNoTracking()
                        .OrderBy(x => x.SortOrder).ThenBy(x => x.Name)
                        .Select(x => new AdminLookupRow
                        {
                            Id = x.Id,
                            Name = x.Name,
                            Description = x.Description,
                            SortOrder = x.SortOrder,
                            IsActive = x.IsActive
                        })
                        .ToListAsync();
                }
                break;

            case "business-area-admins":
                vm.BusinessAreaAdminMemberRows = await _context.BusinessAreaAdminMembers.AsNoTracking()
                    .OrderBy(m => m.BusinessAreaLookup.Name)
                    .ThenBy(m => m.User.Email)
                    .Select(m => new AdminBusinessAreaAdminMemberRow
                    {
                        MembershipId = m.Id,
                        BusinessAreaName = m.BusinessAreaLookup.Name,
                        UserEmail = m.User.Email,
                        UserDisplayName = m.User.Name ?? m.User.Email
                    })
                    .ToListAsync();
                if (TempData["AdminError"] is string baAdmErr)
                    ViewBag.AdminError = baAdmErr;
                break;

            case "business-area-leadership":
                vm.BusinessAreaLeadershipMemberRows = await _context.BusinessAreaLeadershipMembers.AsNoTracking()
                    .OrderBy(m => m.BusinessAreaLookup.Name)
                    .ThenBy(m => m.User.Email)
                    .Select(m => new AdminBusinessAreaLeadershipMemberRow
                    {
                        MembershipId = m.Id,
                        BusinessAreaName = m.BusinessAreaLookup.Name,
                        UserEmail = m.User.Email,
                        UserDisplayName = m.User.Name ?? m.User.Email
                    })
                    .ToListAsync();
                if (TempData["AdminError"] is string baLdrErr)
                    ViewBag.AdminError = baLdrErr;
                break;

            case "directorate-leadership":
                vm.DirectorateLeadershipMemberRows = await _context.DivisionUsers.AsNoTracking()
                    .OrderBy(m => m.Division.Name)
                    .ThenBy(m => m.User.Email)
                    .Select(m => new AdminDirectorateLeadershipMemberRow
                    {
                        MembershipId = m.Id,
                        DirectorateName = m.Division.Name,
                        UserEmail = m.User.Email,
                        UserDisplayName = m.User.Name ?? m.User.Email
                    })
                    .ToListAsync();
                if (TempData["AdminError"] is string dirLdrErr)
                    ViewBag.AdminError = dirLdrErr;
                break;

            case "groups":
                var groups = await _context.Groups.AsNoTracking()
                    .Include(g => g.UserGroups)
                    .Include(g => g.GroupFeaturePermissions)
                    .OrderBy(g => g.Name)
                    .ToListAsync();
                vm.GroupRows = groups.Select(g => new AdminGroupRow
                {
                    Id = g.Id,
                    Name = g.Name,
                    Description = g.Description,
                    MemberCount = g.UserGroups.Count,
                    FeatureCount = g.GroupFeaturePermissions.Select(p => p.FeatureId).Distinct().Count(),
                    IsActive = g.IsActive,
                    IsSystemGroup = g.IsSystemGroup
                }).ToList();
                if (TempData["AdminError"] is string groupErr)
                    ViewBag.AdminError = groupErr;
                break;

            case "api-tokens":
                vm.ApiTokenRows = await _context.ApiTokens.AsNoTracking()
                    .OrderByDescending(t => t.CreatedAt)
                    .Select(t => new AdminApiTokenRow
                    {
                        Id = t.Id,
                        Name = t.Name,
                        Description = t.Description,
                        CreatedAt = t.CreatedAt,
                        ExpiresAt = t.ExpiresAt,
                        IsActive = t.IsActive,
                        Environment = t.Environment,
                        AccessTier = t.AccessTier,
                        OwnerEmail = t.OwnerEmail,
                        IsSelfService = t.IsSelfService
                    })
                    .ToListAsync();
                break;

            case "api-token-requests":
                ViewBag.PendingApiTokenRequests = await _context.ApiTokenRequests
                    .AsNoTracking()
                    .Where(r => r.Status == ApiTokenRequestStatus.Pending)
                    .OrderBy(r => r.CreatedAt)
                    .ToListAsync();
                break;

            case "cms-access-products":
                vm.CmsAccessProductRows = await _context.CmsAccessRequestProducts.AsNoTracking()
                    .OrderBy(x => x.SortOrder).ThenBy(x => x.Name)
                    .Select(x => new AdminCmsAccessProductRow
                    {
                        Id = x.Id,
                        Name = x.Name,
                        SignInPageUrl = x.SignInPageUrl,
                        SortOrder = x.SortOrder,
                        IsActive = x.IsActive
                    })
                    .ToListAsync();
                if (TempData["AdminMessage"] is string cmsMsg)
                    ViewBag.AdminMessage = cmsMsg;
                if (TempData["AdminError"] is string cmsErr)
                    ViewBag.AdminError = cmsErr;
                break;

            case "feature-settings":
                await PopulateFeatureSettingsRowsAsync(vm);
                break;

            case "environment-sync":
                vm.EnvironmentSync = await BuildEnvironmentSyncPanelAsync(HttpContext.RequestAborted);
                if (TempData["AdminMessage"] is string syncMsg)
                    ViewBag.AdminMessage = syncMsg;
                if (TempData["AdminError"] is string syncErr)
                    ViewBag.AdminError = syncErr;
                break;

            case "fips-channels":
                vm.FipsChannels = await _context.FipsChannels.AsNoTracking()
                    .OrderBy(x => x.DisplayOrder).ThenBy(x => x.Name)
                    .Select(x => new AdminLookupRow { Id = x.Id, Name = x.Name, Description = x.Description, SortOrder = x.DisplayOrder, IsActive = x.Active })
                    .ToListAsync();
                break;

            case "fips-types":
                vm.FipsTypes = await _context.FipsTypes.AsNoTracking()
                    .OrderBy(x => x.DisplayOrder).ThenBy(x => x.Name)
                    .Select(x => new AdminLookupRow { Id = x.Id, Name = x.Name, Description = x.Description, SortOrder = x.DisplayOrder, IsActive = x.Active })
                    .ToListAsync();
                break;

            case "fips-business-areas":
                await _fipsBusinessAreaLookupSync.SyncFromBusinessAreaLookupsAsync();
                vm.FipsBusinessAreas = await _context.FipsBusinessAreas.AsNoTracking()
                    .OrderBy(x => x.DisplayOrder).ThenBy(x => x.Name)
                    .Select(x => new AdminLookupRow { Id = x.Id, Name = x.Name, Description = x.Description, SortOrder = x.DisplayOrder, IsActive = x.Active })
                    .ToListAsync();
                break;

            case "fips-user-groups":
            {
                var allUserGroups = await _context.FipsUserGroups.AsNoTracking()
                    .Include(g => g.Synonyms)
                    .ToListAsync();
                vm.FipsUserGroups = AdminFipsUserGroupTreeHelper.BuildFlatTree(allUserGroups);
                vm.FipsUserGroupParentOptions = AdminFipsUserGroupTreeHelper.BuildParentOptions(vm.FipsUserGroups);
                break;
            }

            case "fips-contact-roles":
                vm.FipsContactRoles = await _context.FipsContactRoles.AsNoTracking()
                    .OrderBy(x => x.DisplayOrder)
                    .Select(x => new AdminFipsContactRoleRow
                    {
                        Id = x.Id,
                        Name = x.Name,
                        Description = x.Description,
                        AllowMultiple = x.AllowMultiple,
                        DisplayOrder = x.DisplayOrder,
                        Active = x.Active
                    })
                    .ToListAsync();
                break;

            case "fips-categorisation":
                var catGroups = await _context.FipsCategorisationGroups.AsNoTracking()
                    .Include(g => g.Items)
                    .OrderBy(g => g.DisplayOrder).ThenBy(g => g.Name)
                    .ToListAsync();
                vm.FipsCategorisationGroups = catGroups.Select(g => new AdminFipsCategorisationGroupRow
                {
                    Id = g.Id,
                    Name = g.Name,
                    Description = g.Description,
                    DisplayOrder = g.DisplayOrder,
                    Active = g.Active,
                    Items = g.Items
                        .OrderBy(i => i.DisplayOrder).ThenBy(i => i.Name)
                        .Select(i => new AdminLookupRow
                        {
                            Id = i.Id,
                            Name = i.Name,
                            Description = i.Description,
                            SortOrder = i.DisplayOrder,
                            IsActive = i.Active
                        })
                        .ToList()
                }).ToList();
                break;

            case "std-categories":
                vm.StdCategories = await _context.StandardCategories.AsNoTracking()
                    .OrderBy(x => x.SortOrder).ThenBy(x => x.Name)
                    .Select(x => new AdminLookupRow { Id = x.Id, Name = x.Name, Description = x.Description, SortOrder = x.SortOrder, IsActive = x.IsActive })
                    .ToListAsync();
                break;

            case "std-subcategories":
                vm.StdSubCategories = await _context.StandardSubCategories.AsNoTracking()
                    .Include(x => x.Category)
                    .OrderBy(x => x.Category.Name).ThenBy(x => x.SortOrder).ThenBy(x => x.Name)
                    .Select(x => new AdminStdSubCategoryRow
                    {
                        Id = x.Id,
                        Name = x.Name,
                        Description = x.Description,
                        CategoryName = x.Category.Name,
                        SortOrder = x.SortOrder,
                        IsActive = x.IsActive
                    })
                    .ToListAsync();
                break;

            case "std-functional":
                vm.StdFunctional = await _context.FunctionalStandards.AsNoTracking()
                    .Include(f => f.Themes)
                    .OrderBy(f => f.Title)
                    .Select(f => new AdminFunctionalStandardRow
                    {
                        Id = f.Id,
                        Title = f.Title,
                        Description = f.Description,
                        ThemeCount = f.Themes.Count,
                        PublishedDate = f.PublishedDate
                    })
                    .ToListAsync();
                break;
        }

        if (TempData["AdminMessage"] is string msg)
            ViewBag.AdminMessage = msg;

        return View("~/Views/Modern/Admin/Index.cshtml", vm);
    }

    [HttpPost("feature-settings")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> FeatureSettingsSave([FromForm] List<FeatureToggleSubmitItem>? featureToggles)
    {
        var byCode = (featureToggles ?? new List<FeatureToggleSubmitItem>())
            .Where(r => !string.IsNullOrWhiteSpace(r.Code)
                && ApplicationFeatureToggleDefinition.AllowedCodes.Contains(r.Code!))
            .ToDictionary(r => r.Code, StringComparer.OrdinalIgnoreCase);

        foreach (var def in ApplicationFeatureToggleDefinition.All)
        {
            if (!byCode.TryGetValue(def.Code, out var row))
                continue;

            if (!Enum.IsDefined(typeof(FeatureAccessMode), row.AccessMode))
                continue;
            var mode = (FeatureAccessMode)row.AccessMode;

            var userIds = (row.AllowedUserIds ?? new List<int>())
                .Where(id => id > 0)
                .Distinct()
                .ToList();
            var groupIds = (row.AllowedGroupIds ?? new List<int>())
                .Where(id => id > 0)
                .Distinct()
                .ToList();

            if (mode == FeatureAccessMode.OnForSome
                && !userIds.Any()
                && !groupIds.Any())
            {
                TempData["AdminMessage"] =
                    $"“{def.Label}”: add at least one person or one group when you choose “On for some”, or change the setting to Off or On for all.";
                return RedirectToAction(nameof(Index), new { panel = "feature-settings" });
            }

            var entity = await _context.Features
                .Include(f => f.UserAllows)
                .Include(f => f.GroupAllows)
                .FirstOrDefaultAsync(f => f.Code == def.Code);

            if (entity == null)
            {
                entity = new Feature
                {
                    Code = def.Code,
                    Name = def.Name,
                    Description = def.Hint,
                    AccessMode = mode,
                    IsActive = mode != FeatureAccessMode.Off,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                _context.Features.Add(entity);
            }
            else
            {
                entity.AccessMode = mode;
                entity.IsActive = mode != FeatureAccessMode.Off;
                entity.UpdatedAt = DateTime.UtcNow;
                if (!string.IsNullOrWhiteSpace(def.Name)) entity.Name = def.Name;
                if (def.Hint != null) entity.Description = def.Hint;
            }

            if (entity.UserAllows is { Count: > 0 })
                _context.FeatureUserAllows.RemoveRange(entity.UserAllows);
            if (entity.GroupAllows is { Count: > 0 })
                _context.FeatureGroupAllows.RemoveRange(entity.GroupAllows);

            if (mode == FeatureAccessMode.OnForSome)
            {
                foreach (var uid in userIds)
                {
                    if (await _context.Users.AnyAsync(u => u.Id == uid))
                    {
                        entity.UserAllows.Add(new FeatureUserAllow
                        {
                            UserId = uid,
                            CreatedAt = DateTime.UtcNow
                        });
                    }
                }
                foreach (var gid in groupIds)
                {
                    if (await _context.Groups.AnyAsync(g => g.Id == gid && g.IsActive))
                    {
                        entity.GroupAllows.Add(new FeatureGroupAllow
                        {
                            GroupId = gid,
                            CreatedAt = DateTime.UtcNow
                        });
                    }
                }
            }
        }

        await _context.SaveChangesAsync();

        TempData["AdminMessage"] = "Feature settings saved.";

        return RedirectToAction(nameof(Index), new { panel = "feature-settings" });
    }

    private const int FeatureSettingsUserAllowSlots = 24;
    private const int FeatureSettingsGroupAllowSlots = 12;

    private async Task PopulateFeatureSettingsRowsAsync(AdminHubViewModel vm)
    {
        vm.FeatureSettingsRows.Clear();
        vm.FeatureSettingsGroupOptions = await _context.Groups.AsNoTracking()
            .Where(g => g.IsActive)
            .OrderBy(g => g.Name)
            .Select(g => new AdminFeatureSettingsGroupOption { Id = g.Id, Name = g.Name })
            .ToListAsync();
        foreach (var def in ApplicationFeatureToggleDefinition.All)
        {
            var entity = await _context.Features
                .AsNoTracking()
                .Include(f => f.UserAllows)
                .Include(f => f.GroupAllows)
                .FirstOrDefaultAsync(f => f.Code == def.Code);

            FeatureAccessMode mode;
            if (entity == null)
                mode = def.DefaultEnabled ? FeatureAccessMode.OnForAll : FeatureAccessMode.Off;
            else
                mode = entity.AccessMode;

            var row = new AdminFeatureToggleRow
            {
                Code = def.Code,
                Label = def.Label,
                Hint = def.Hint,
                AccessMode = mode
            };

            var idList = entity?.UserAllows.Select(a => a.UserId).ToList() ?? new List<int>();
            var uids = idList.Where(x => x > 0).Distinct().Take(FeatureSettingsUserAllowSlots).ToList();
            if (uids.Count > 0)
            {
                var map = await _context.Users.AsNoTracking()
                    .Where(u => uids.Contains(u.Id))
                    .ToDictionaryAsync(u => u.Id, u => u);
                for (var i = 0; i < FeatureSettingsUserAllowSlots; i++)
                {
                    if (i < uids.Count && map.TryGetValue(uids[i], out var u))
                    {
                        row.AllowSlots.Add(new AdminFeatureAllowUserSlot
                        {
                            UserId = u.Id, Name = u.Name, Email = u.Email
                        });
                    }
                    else
                    {
                        row.AllowSlots.Add(new AdminFeatureAllowUserSlot());
                    }
                }
            }
            else
            {
                for (var i = 0; i < FeatureSettingsUserAllowSlots; i++)
                    row.AllowSlots.Add(new AdminFeatureAllowUserSlot());
            }

            var gIdList = entity?.GroupAllows.Select(a => a.GroupId).ToList() ?? new List<int>();
            var gids = gIdList.Where(x => x > 0).Distinct().Take(FeatureSettingsGroupAllowSlots).ToList();
            if (gids.Count > 0)
            {
                var gMap = await _context.Groups.AsNoTracking()
                    .Where(g => gids.Contains(g.Id))
                    .ToDictionaryAsync(g => g.Id, g => g);
                for (var g = 0; g < FeatureSettingsGroupAllowSlots; g++)
                {
                    if (g < gids.Count && gMap.TryGetValue(gids[g], out var grp))
                    {
                        row.GroupSlots.Add(new AdminFeatureAllowGroupSlot
                        {
                            GroupId = grp.Id,
                            Name = grp.Name
                        });
                    }
                    else
                    {
                        row.GroupSlots.Add(new AdminFeatureAllowGroupSlot());
                    }
                }
            }
            else
            {
                for (var g = 0; g < FeatureSettingsGroupAllowSlots; g++)
                    row.GroupSlots.Add(new AdminFeatureAllowGroupSlot());
            }

            vm.FeatureSettingsRows.Add(row);
        }
    }

    // ── Business Areas CRUD ──

    [HttpPost("business-area/create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> BusinessAreaCreate(string name, string? description, int sortOrder)
    {
        name = (name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            TempData["AdminMessage"] = "Name is required.";
            return RedirectToAction(nameof(Index), new { panel = "business-areas" });
        }
        var entity = new BusinessAreaLookup { Name = name, Description = description?.Trim(), SortOrder = sortOrder };
        _context.BusinessAreaLookups.Add(entity);
        await _context.SaveChangesAsync();
        TempData["AdminMessage"] = $"Business area \"{name}\" added.";
        return RedirectToAction(nameof(LookupEdit), new { panel = "business-areas", id = entity.Id });
    }

    [HttpPost("business-area/{id:int}/edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> BusinessAreaEdit(int id, string name, string? description, int sortOrder)
    {
        var entity = await _context.BusinessAreaLookups.FindAsync(id);
        if (entity == null) return NotFound();
        name = (name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            TempData["AdminMessage"] = "Name is required.";
            return RedirectToAction(nameof(LookupEdit), new { panel = "business-areas", id });
        }
        entity.Name = name;
        entity.Description = description?.Trim();
        entity.SortOrder = sortOrder;
        entity.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        TempData["AdminMessage"] = $"Business area \"{name}\" updated.";
        return RedirectToAction(nameof(Index), new { panel = "business-areas" });
    }

    [HttpPost("business-area/{id:int}/toggle")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> BusinessAreaToggle(int id)
    {
        var entity = await _context.BusinessAreaLookups.FindAsync(id);
        if (entity == null) return NotFound();
        entity.IsActive = !entity.IsActive;
        entity.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        TempData["AdminMessage"] = $"\"{entity.Name}\" is now {(entity.IsActive ? "active" : "inactive")}.";
        return RedirectToAction(nameof(LookupEdit), new { panel = "business-areas", id });
    }

    // ── Universal barriers (demand Explore — inclusive design) ──

    [HttpPost("universal-barrier/create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UniversalBarrierCreate(string name, string? description, string? guidanceUrl, int sortOrder)
    {
        name = (name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            TempData["AdminMessage"] = "Name is required.";
            return RedirectToAction(nameof(Index), new { panel = "universal-barriers" });
        }
        var u = string.IsNullOrWhiteSpace(guidanceUrl) ? null : guidanceUrl.Trim();
        if (u != null && u.Length > 500) u = u[..500];
        var entity = new UniversalBarrierLookup
        {
            Name = name,
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            GuidanceUrl = u,
            SortOrder = sortOrder,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _context.UniversalBarrierLookups.Add(entity);
        await _context.SaveChangesAsync();
        TempData["AdminMessage"] = $"Universal barrier \"{name}\" added.";
        return RedirectToAction(nameof(LookupEdit), new { panel = "universal-barriers", id = entity.Id });
    }

    [HttpPost("universal-barrier/{id:int}/edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UniversalBarrierEdit(int id, string name, string? description, string? guidanceUrl, int sortOrder)
    {
        var entity = await _context.UniversalBarrierLookups.FindAsync(id);
        if (entity == null) return NotFound();
        name = (name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            TempData["AdminMessage"] = "Name is required.";
            return RedirectToAction(nameof(LookupEdit), new { panel = "universal-barriers", id });
        }
        var u = string.IsNullOrWhiteSpace(guidanceUrl) ? null : guidanceUrl.Trim();
        if (u != null && u.Length > 500) u = u[..500];
        entity.Name = name;
        entity.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        entity.GuidanceUrl = u;
        entity.SortOrder = sortOrder;
        entity.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        TempData["AdminMessage"] = $"Universal barrier \"{name}\" updated.";
        return RedirectToAction(nameof(Index), new { panel = "universal-barriers" });
    }

    [HttpPost("universal-barrier/{id:int}/toggle")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UniversalBarrierToggle(int id)
    {
        var entity = await _context.UniversalBarrierLookups.FindAsync(id);
        if (entity == null) return NotFound();
        entity.IsActive = !entity.IsActive;
        entity.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        TempData["AdminMessage"] = $"\"{entity.Name}\" is now {(entity.IsActive ? "active" : "inactive")}.";
        return RedirectToAction(nameof(LookupEdit), new { panel = "universal-barriers", id });
    }

    // ── Phases CRUD ──

    [HttpPost("phase/create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PhaseCreate(string name, string? description, int sortOrder)
    {
        name = (name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            TempData["AdminMessage"] = "Name is required.";
            return RedirectToAction(nameof(Index), new { panel = "phases" });
        }
        var entity = new PhaseLookup { Name = name, Description = description?.Trim(), SortOrder = sortOrder };
        _context.PhaseLookups.Add(entity);
        await _context.SaveChangesAsync();
        TempData["AdminMessage"] = $"Phase \"{name}\" added.";
        return RedirectToAction(nameof(LookupEdit), new { panel = "phases", id = entity.Id });
    }

    [HttpPost("phase/{id:int}/edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PhaseEdit(int id, string name, string? description, int sortOrder)
    {
        var entity = await _context.PhaseLookups.FindAsync(id);
        if (entity == null) return NotFound();
        name = (name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            TempData["AdminMessage"] = "Name is required.";
            return RedirectToAction(nameof(LookupEdit), new { panel = "phases", id });
        }
        entity.Name = name;
        entity.Description = description?.Trim();
        entity.SortOrder = sortOrder;
        entity.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        TempData["AdminMessage"] = $"Phase \"{name}\" updated.";
        return RedirectToAction(nameof(Index), new { panel = "phases" });
    }

    [HttpPost("phase/{id:int}/toggle")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PhaseToggle(int id)
    {
        var entity = await _context.PhaseLookups.FindAsync(id);
        if (entity == null) return NotFound();
        entity.IsActive = !entity.IsActive;
        entity.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        TempData["AdminMessage"] = $"\"{entity.Name}\" is now {(entity.IsActive ? "active" : "inactive")}.";
        return RedirectToAction(nameof(LookupEdit), new { panel = "phases", id });
    }

    // ── Directorates CRUD ──

    [HttpPost("directorate/create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DirectorateCreate(string name, string? description, int sortOrder)
    {
        name = (name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            TempData["AdminMessage"] = "Name is required.";
            return RedirectToAction(nameof(Index), new { panel = "directorates" });
        }
        var entity = new DirectorateLookup { Name = name, Description = description?.Trim(), SortOrder = sortOrder };
        _context.DirectorateLookups.Add(entity);
        await _context.SaveChangesAsync();
        TempData["AdminMessage"] = $"Directorate \"{name}\" added.";
        return RedirectToAction(nameof(LookupEdit), new { panel = "directorates", id = entity.Id });
    }

    [HttpPost("directorate/{id:int}/edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DirectorateEdit(int id, string name, string? description, int sortOrder)
    {
        var entity = await _context.DirectorateLookups.FindAsync(id);
        if (entity == null) return NotFound();
        name = (name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            TempData["AdminMessage"] = "Name is required.";
            return RedirectToAction(nameof(LookupEdit), new { panel = "directorates", id });
        }
        entity.Name = name;
        entity.Description = description?.Trim();
        entity.SortOrder = sortOrder;
        entity.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        TempData["AdminMessage"] = $"Directorate \"{name}\" updated.";
        return RedirectToAction(nameof(Index), new { panel = "directorates" });
    }

    [HttpPost("directorate/{id:int}/toggle")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DirectorateToggle(int id)
    {
        var entity = await _context.DirectorateLookups.FindAsync(id);
        if (entity == null) return NotFound();
        entity.IsActive = !entity.IsActive;
        entity.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        TempData["AdminMessage"] = $"\"{entity.Name}\" is now {(entity.IsActive ? "active" : "inactive")}.";
        return RedirectToAction(nameof(LookupEdit), new { panel = "directorates", id });
    }

    // ── Priorities CRUD ──

    [HttpPost("priority/create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PriorityCreate(string name, string? summary, string? description, string? cssClass, int sortOrder)
    {
        name = (name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            TempData["AdminMessage"] = "Name is required.";
            return RedirectToAction(nameof(Index), new { panel = "priorities" });
        }
        var entity = new DeliveryPriority { Name = name, Summary = summary?.Trim(), Description = description?.Trim(), CssClass = cssClass?.Trim(), SortOrder = sortOrder };
        _context.DeliveryPriorities.Add(entity);
        await _context.SaveChangesAsync();
        TempData["AdminMessage"] = $"Priority \"{name}\" added.";
        return RedirectToAction(nameof(LookupEdit), new { panel = "priorities", id = entity.Id });
    }

    [HttpPost("priority/{id:int}/edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PriorityEdit(int id, string name, string? summary, string? description, string? cssClass, int sortOrder)
    {
        var entity = await _context.DeliveryPriorities.FindAsync(id);
        if (entity == null) return NotFound();
        name = (name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            TempData["AdminMessage"] = "Name is required.";
            return RedirectToAction(nameof(LookupEdit), new { panel = "priorities", id });
        }
        entity.Name = name;
        entity.Summary = summary?.Trim();
        entity.Description = description?.Trim();
        entity.CssClass = cssClass?.Trim();
        entity.SortOrder = sortOrder;
        entity.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        TempData["AdminMessage"] = $"Priority \"{name}\" updated.";
        return RedirectToAction(nameof(Index), new { panel = "priorities" });
    }

    [HttpPost("priority/{id:int}/toggle")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PriorityToggle(int id)
    {
        var entity = await _context.DeliveryPriorities.FindAsync(id);
        if (entity == null) return NotFound();
        entity.IsActive = !entity.IsActive;
        entity.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        TempData["AdminMessage"] = $"\"{entity.Name}\" is now {(entity.IsActive ? "active" : "inactive")}.";
        return RedirectToAction(nameof(LookupEdit), new { panel = "priorities", id });
    }

    // ── RAG Definitions CRUD ──

    [HttpPost("rag/create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RagCreate(string name, string? description, string? cssClass, int sortOrder)
    {
        name = (name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            TempData["AdminMessage"] = "Name is required.";
            return RedirectToAction(nameof(Index), new { panel = "rag-defns" });
        }
        var entity = new RagStatusLookup { Name = name, Description = description?.Trim(), CssClass = cssClass?.Trim(), SortOrder = sortOrder };
        _context.RagStatusLookups.Add(entity);
        await _context.SaveChangesAsync();
        TempData["AdminMessage"] = $"RAG status \"{name}\" added.";
        return RedirectToAction(nameof(LookupEdit), new { panel = "rag-defns", id = entity.Id });
    }

    [HttpPost("rag/{id:int}/edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RagEdit(int id, string name, string? description, string? cssClass, int sortOrder)
    {
        var entity = await _context.RagStatusLookups.FindAsync(id);
        if (entity == null) return NotFound();
        name = (name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            TempData["AdminMessage"] = "Name is required.";
            return RedirectToAction(nameof(LookupEdit), new { panel = "rag-defns", id });
        }
        entity.Name = name;
        entity.Description = description?.Trim();
        entity.CssClass = cssClass?.Trim();
        entity.SortOrder = sortOrder;
        entity.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        TempData["AdminMessage"] = $"RAG status \"{name}\" updated.";
        return RedirectToAction(nameof(Index), new { panel = "rag-defns" });
    }

    [HttpPost("rag/{id:int}/toggle")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RagToggle(int id)
    {
        var entity = await _context.RagStatusLookups.FindAsync(id);
        if (entity == null) return NotFound();
        entity.IsActive = !entity.IsActive;
        entity.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        TempData["AdminMessage"] = $"\"{entity.Name}\" is now {(entity.IsActive ? "active" : "inactive")}.";
        return RedirectToAction(nameof(LookupEdit), new { panel = "rag-defns", id });
    }

    [HttpGet("groups/create")]
    public IActionResult GroupCreate()
    {
        SetAdminChrome("admin-roles");
        if (TempData["AdminError"] is string err)
            ViewBag.AdminError = err;
        return View("~/Views/Modern/Admin/GroupCreate.cshtml", new AdminGroupFormViewModel());
    }

    [HttpGet("groups/{id:int}/edit")]
    public async Task<IActionResult> GroupEdit(int id)
    {
        SetAdminChrome("admin-roles");
        var g = await _context.Groups.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (g == null) return NotFound();
        if (TempData["AdminMessage"] is string msg)
            ViewBag.AdminMessage = msg;
        if (TempData["AdminError"] is string err)
            ViewBag.AdminError = err;
        var vm = new AdminGroupFormViewModel
        {
            Id = g.Id,
            Name = g.Name,
            Description = g.Description,
            IsActive = g.IsActive,
            IsSystemGroup = g.IsSystemGroup
        };
        return View("~/Views/Modern/Admin/GroupEdit.cshtml", vm);
    }

    [HttpGet("groups")]
    public IActionResult Groups()
    {
        return RedirectToAction(nameof(Index), new { panel = "groups" });
    }

    [HttpPost("groups/create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> GroupCreate(string name, string? description)
    {
        name = (name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            TempData["AdminError"] = "Enter a group name.";
            return RedirectToAction(nameof(GroupCreate));
        }
        var userEmail = User.Identity?.Name
            ?? User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value
            ?? User.FindFirst("preferred_username")?.Value ?? "";
        var entity = new Group
        {
            Name = name,
            Description = description?.Trim(),
            CreatedBy = userEmail,
            UpdatedBy = userEmail
        };
        _context.Groups.Add(entity);
        await _context.SaveChangesAsync();
        TempData["AdminMessage"] = $"Group \"{name}\" created. Add members and permissions below.";
        return RedirectToAction(nameof(GroupDetail), new { id = entity.Id });
    }

    [HttpPost("groups/{id:int}/edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> GroupEdit(int id, string name, string? description, bool isActive)
    {
        var group = await _context.Groups.FindAsync(id);
        if (group == null) return NotFound();
        name = (name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            TempData["AdminError"] = "Enter a group name.";
            return RedirectToAction(nameof(GroupEdit), new { id });
        }
        var userEmail = User.Identity?.Name
            ?? User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value
            ?? User.FindFirst("preferred_username")?.Value ?? "";
        if (!group.IsSystemGroup)
        {
            group.Name = name;
            group.IsActive = isActive;
        }
        group.Description = description?.Trim();
        group.UpdatedBy = userEmail;
        group.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        TempData["AdminMessage"] = $"Group \"{group.Name}\" updated.";
        return RedirectToAction(nameof(Groups));
    }

    [HttpPost("groups/{id:int}/toggle")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> GroupToggle(int id)
    {
        var group = await _context.Groups.FindAsync(id);
        if (group == null) return NotFound();
        if (group.IsSystemGroup)
        {
            TempData["AdminError"] = "System groups cannot be deactivated from this screen.";
            return RedirectToAction(nameof(GroupEdit), new { id });
        }
        group.IsActive = !group.IsActive;
        group.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        TempData["AdminMessage"] = $"\"{group.Name}\" is now {(group.IsActive ? "active" : "inactive")}.";
        return RedirectToAction(nameof(GroupEdit), new { id });
    }

    [HttpGet("groups/{id:int}")]
    public async Task<IActionResult> GroupDetail(int id)
    {
        SetAdminChrome("admin-roles");

        var group = await _context.Groups.AsNoTracking()
            .Include(g => g.UserGroups).ThenInclude(ug => ug.User)
            .Include(g => g.GroupFeaturePermissions).ThenInclude(gfp => gfp.Feature)
            .FirstOrDefaultAsync(g => g.Id == id);
        if (group == null) return NotFound();

        var existingFeatureIds = group.GroupFeaturePermissions.Select(p => p.FeatureId).ToHashSet();
        var availableFeatures = await _context.Features.AsNoTracking()
            .Where(f => f.IsActive)
            .OrderBy(f => f.Name)
            .Select(f => new AdminAvailableFeature { Id = f.Id, Name = f.Name, Code = f.Code })
            .ToListAsync();

        var vm = new AdminGroupDetailViewModel
        {
            Id = group.Id,
            Name = group.Name,
            Description = group.Description,
            IsActive = group.IsActive,
            IsSystemGroup = group.IsSystemGroup,
            Members = group.UserGroups.OrderBy(ug => ug.User.Name).Select(ug => new AdminGroupMemberRow
            {
                UserId = ug.UserId,
                Name = ug.User.Name,
                Email = ug.User.Email,
                Role = ug.User.Role.ToString(),
                AssignedAt = ug.AssignedAt
            }).ToList(),
            Features = group.GroupFeaturePermissions.OrderBy(p => p.Feature.Name).Select(p => new AdminGroupFeatureRow
            {
                PermissionId = p.Id,
                FeatureId = p.FeatureId,
                FeatureName = p.Feature.Name,
                FeatureCode = p.Feature.Code,
                Permission = p.Permission.ToString()
            }).ToList(),
            AvailableFeatures = availableFeatures,
            AvailableUsers = new List<AdminUserOption>()
        };

        if (TempData["AdminMessage"] is string msg)
            ViewBag.AdminMessage = msg;
        if (TempData["AdminError"] is string err)
            ViewBag.AdminError = err;

        return View("~/Views/Modern/Admin/GroupDetail.cshtml", vm);
    }

    [HttpPost("groups/{id:int}/add-member")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> GroupAddMember(int id, int userId)
    {
        var group = await _context.Groups.FindAsync(id);
        if (group == null) return NotFound();

        if (userId <= 0)
        {
            TempData["AdminError"] = "Search for a user and select them from the directory before adding.";
            return RedirectToAction(nameof(GroupDetail), new { id });
        }

        if (await _context.UserGroups.AnyAsync(ug => ug.GroupId == id && ug.UserId == userId))
        {
            TempData["AdminError"] = "That user is already in this group.";
            return RedirectToAction(nameof(GroupDetail), new { id });
        }

        var userEmail = User.Identity?.Name
            ?? User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value
            ?? User.FindFirst("preferred_username")?.Value ?? "";

        _context.UserGroups.Add(new UserGroup
        {
            GroupId = id,
            UserId = userId,
            AssignedAt = DateTime.UtcNow,
            AssignedBy = userEmail
        });
        await _context.SaveChangesAsync();

        var addedUser = await _context.Users.FindAsync(userId);
        TempData["AdminMessage"] = $"{addedUser?.Name} added to \"{group.Name}\".";
        return RedirectToAction(nameof(GroupDetail), new { id });
    }

    [HttpPost("groups/{id:int}/remove-member")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> GroupRemoveMember(int id, int userId)
    {
        var ug = await _context.UserGroups
            .Include(x => x.User)
            .FirstOrDefaultAsync(x => x.GroupId == id && x.UserId == userId);
        if (ug == null) return NotFound();

        var userName = ug.User.Name;
        _context.UserGroups.Remove(ug);
        await _context.SaveChangesAsync();

        TempData["AdminMessage"] = $"{userName} removed from the group.";
        return RedirectToAction(nameof(GroupDetail), new { id });
    }

    [HttpPost("groups/{id:int}/add-permission")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> GroupAddPermission(int id, int featureId, PermissionType permission)
    {
        var group = await _context.Groups.FindAsync(id);
        if (group == null) return NotFound();

        if (await _context.GroupFeaturePermissions.AnyAsync(p => p.GroupId == id && p.FeatureId == featureId && p.Permission == permission))
        {
            TempData["AdminError"] = "This permission is already assigned to the group.";
            return RedirectToAction(nameof(GroupDetail), new { id });
        }

        var userEmail = User.Identity?.Name
            ?? User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value
            ?? User.FindFirst("preferred_username")?.Value ?? "";

        _context.GroupFeaturePermissions.Add(new GroupFeaturePermission
        {
            GroupId = id,
            FeatureId = featureId,
            Permission = permission,
            CreatedBy = userEmail
        });
        await _context.SaveChangesAsync();

        var feature = await _context.Features.FindAsync(featureId);
        TempData["AdminMessage"] = $"{permission} permission on \"{feature?.Name}\" added.";
        return RedirectToAction(nameof(GroupDetail), new { id });
    }

    [HttpPost("groups/{id:int}/remove-permission")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> GroupRemovePermission(int id, int permissionId)
    {
        var perm = await _context.GroupFeaturePermissions
            .Include(p => p.Feature)
            .FirstOrDefaultAsync(p => p.Id == permissionId && p.GroupId == id);
        if (perm == null) return NotFound();

        var featureName = perm.Feature.Name;
        _context.GroupFeaturePermissions.Remove(perm);
        await _context.SaveChangesAsync();

        TempData["AdminMessage"] = $"Permission on \"{featureName}\" removed.";
        return RedirectToAction(nameof(GroupDetail), new { id });
    }


    private static DateTime NormalizeUtcDate(DateTime d) =>
        DateTime.SpecifyKind(d.Kind == DateTimeKind.Utc ? d.Date : d.Date, DateTimeKind.Utc);

    /// <summary>Parses GOV.UK date input fields (day / month / year) as a UTC calendar date.</summary>
    private static bool TryParseUtcDateParts(int? day, int? month, int? year, out DateTime utcDate)
    {
        utcDate = default;
        if (day is null || month is null || year is null)
            return false;
        try
        {
            utcDate = DateTime.SpecifyKind(new DateTime(year.Value, month.Value, day.Value), DateTimeKind.Utc).Date;
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private async Task<WorkReportingCycle> EnsureMonthlyWorkReportingCycleAsync()
    {
        var cycle = await _context.WorkReportingCycles
            .FirstOrDefaultAsync(c => c.Code == WorkReportingMonthlyCycleCodes.MonthlyWorkUpdates);
        if (cycle != null)
            return cycle;

        cycle = new WorkReportingCycle
        {
            Code = WorkReportingMonthlyCycleCodes.MonthlyWorkUpdates,
            Name = "Monthly work reporting",
            Description = "Monthly delivery confidence updates for work items.",
            PeriodType = "Monthly",
            IsActive = true,
            DisplayOrder = 0
        };
        _context.WorkReportingCycles.Add(cycle);
        await _context.SaveChangesAsync();
        return cycle;
    }

    [HttpGet("work-reporting/create")]
    public IActionResult WorkReportingCreate()
    {
        SetAdminChrome("admin-work-reporting");
        var vm = new WorkReportingPeriodFormViewModel
        {
            Id = 0,
            PeriodLabel = "",
            PeriodStart = null,
            PeriodEnd = null,
            SubmissionOpens = null,
            SubmissionCloses = null,
            IsActive = false
        };
        if (TempData["AdminError"] is string err)
            ViewBag.AdminError = err;
        return View("~/Views/Modern/Admin/WorkReportingCreate.cshtml", vm);
    }

    [HttpGet("work-reporting/period/{id:int}/edit")]
    public async Task<IActionResult> WorkReportingPeriodEdit(int id)
    {
        SetAdminChrome("admin-work-reporting");

        var row = await _context.WorkReportingCyclePeriods.AsNoTracking()
            .Include(p => p.ReportingCycle)
            .FirstOrDefaultAsync(p =>
                p.Id == id &&
                p.ReportingCycle.Code == WorkReportingMonthlyCycleCodes.MonthlyWorkUpdates);
        if (row == null)
            return NotFound();

        var vm = new WorkReportingPeriodFormViewModel
        {
            Id = row.Id,
            PeriodLabel = row.PeriodLabel,
            PeriodStart = row.PeriodStart,
            PeriodEnd = row.PeriodEnd,
            SubmissionOpens = row.SubmissionOpens,
            SubmissionCloses = row.SubmissionCloses,
            IsActive = row.IsActive
        };
        if (TempData["AdminMessage"] is string msg)
            ViewBag.AdminMessage = msg;
        if (TempData["AdminError"] is string err)
            ViewBag.AdminError = err;
        return View("~/Views/Modern/Admin/WorkReportingPeriodEdit.cshtml", vm);
    }

    [HttpGet("work-reporting")]
    public async Task<IActionResult> WorkReporting()
    {
        SetAdminChrome("admin-work-reporting");

        var cycle = await _context.WorkReportingCycles.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Code == WorkReportingMonthlyCycleCodes.MonthlyWorkUpdates);

        var rows = new List<WorkReportingPeriodRow>();
        var today = DateTime.UtcNow.Date;

        if (cycle != null)
        {
            var periods = await _context.WorkReportingCyclePeriods.AsNoTracking()
                .Where(p => p.ReportingCycleId == cycle.Id)
                .OrderByDescending(p => p.PeriodStart)
                .ThenByDescending(p => p.DisplayOrder)
                .ToListAsync();

            foreach (var p in periods)
            {
                var live = p.IsActive &&
                           today >= p.SubmissionOpens.Date &&
                           today <= p.SubmissionCloses.Date;
                rows.Add(new WorkReportingPeriodRow
                {
                    Id = p.Id,
                    PeriodKey = p.PeriodKey,
                    PeriodLabel = p.PeriodLabel,
                    PeriodStart = p.PeriodStart,
                    PeriodEnd = p.PeriodEnd,
                    SubmissionOpens = p.SubmissionOpens,
                    SubmissionCloses = p.SubmissionCloses,
                    IsActive = p.IsActive,
                    IsSubmissionWindowLive = live
                });
            }
        }

        var vm = new WorkReportingViewModel { ReportingPeriods = rows };

        var weeklyConfig = await _weeklyUpdateService.GetOrCreateConfigAsync();
        vm.WeeklyConfig = new WeeklyWorkReportingConfigFormViewModel
        {
            PeriodStartDayOfWeek = weeklyConfig.PeriodStartDayOfWeek,
            PeriodEndDayOfWeek = weeklyConfig.PeriodEndDayOfWeek,
            DueDayOfWeek = weeklyConfig.DueDayOfWeek,
            DueWeekOffset = weeklyConfig.DueWeekOffset,
            FirstReportingPeriodStart = weeklyConfig.FirstReportingPeriodStart,
            IsActive = weeklyConfig.IsActive
        };

        var scopeRows = await _context.WeeklyWorkReportingScopeProjects.AsNoTracking()
            .Include(x => x.Project).ThenInclude(p => p.BusinessAreaLookup)
            .Include(x => x.Project).ThenInclude(p => p.PrimaryOrganizationalGroup)
            .OrderBy(x => x.Project.Title)
            .ToListAsync();
        vm.WeeklyScopeProjects = scopeRows.Select(x => new WeeklyWorkReportingScopeRow
        {
            ScopeId = x.Id,
            ProjectId = x.ProjectId,
            Title = x.Project.Title ?? "",
            Status = x.Project.Status ?? "",
            BusinessArea = Services.Modern.ModernWorkService.ResolveProjectBusinessAreaDisplayName(x.Project)
        }).ToList();

        var scopedIds = scopeRows.Select(x => x.ProjectId).ToHashSet();
        var candidateBase = _context.Projects.AsNoTracking()
            .Where(p => !p.IsDeleted && (p.Status == "Active" || p.Status == "Paused") && !scopedIds.Contains(p.Id));

        var scopeSearch = (Request.Query["scopeSearch"].ToString() ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(scopeSearch))
        {
            candidateBase = candidateBase.Where(p =>
                p.Title.Contains(scopeSearch) ||
                (p.Aim != null && p.Aim.Contains(scopeSearch)));
        }

        var candidateProjects = await candidateBase
            .Include(p => p.BusinessAreaLookup)
            .Include(p => p.PrimaryOrganizationalGroup)
            .OrderBy(p => p.Title)
            .Take(50)
            .ToListAsync();

        vm.WeeklyScopeCandidates = candidateProjects.Select(p => new WeeklyWorkReportingScopeCandidateRow
        {
            ProjectId = p.Id,
            Title = p.Title ?? "",
            Status = p.Status ?? "",
            BusinessArea = Services.Modern.ModernWorkService.ResolveProjectBusinessAreaDisplayName(p)
        }).ToList();

        if (TempData["AdminMessage"] is string amsg)
            ViewBag.AdminMessage = amsg;
        else if (TempData["Message"] is string msg)
            ViewBag.AdminMessage = msg;
        if (TempData["AdminError"] is string err)
            ViewBag.AdminError = err;

        return View("~/Views/Modern/Admin/WorkReporting.cshtml", vm);
    }

    [HttpPost("work-reporting/period/create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> WorkReportingPeriodCreate(
        string periodLabel,
        int? periodStartDay,
        int? periodStartMonth,
        int? periodStartYear,
        int? periodEndDay,
        int? periodEndMonth,
        int? periodEndYear,
        int? submissionOpensDay,
        int? submissionOpensMonth,
        int? submissionOpensYear,
        int? submissionClosesDay,
        int? submissionClosesMonth,
        int? submissionClosesYear,
        bool isActive)
    {
        periodLabel = (periodLabel ?? "").Trim();
        if (string.IsNullOrWhiteSpace(periodLabel))
        {
            TempData["AdminError"] = "Name is required.";
            return RedirectToAction(nameof(WorkReportingCreate));
        }

        if (!TryParseUtcDateParts(periodStartDay, periodStartMonth, periodStartYear, out var ps) ||
            !TryParseUtcDateParts(periodEndDay, periodEndMonth, periodEndYear, out var pe) ||
            !TryParseUtcDateParts(submissionOpensDay, submissionOpensMonth, submissionOpensYear, out var so) ||
            !TryParseUtcDateParts(submissionClosesDay, submissionClosesMonth, submissionClosesYear, out var sc))
        {
            TempData["AdminError"] =
                "Enter a complete valid date for period start, period end, submission opens, and submission closes (day, month and year).";
            return RedirectToAction(nameof(WorkReportingCreate));
        }

        ps = NormalizeUtcDate(ps);
        pe = NormalizeUtcDate(pe);
        so = NormalizeUtcDate(so);
        sc = NormalizeUtcDate(sc);

        if (pe.Date < ps.Date)
        {
            TempData["AdminError"] = "Period end must be on or after period start.";
            return RedirectToAction(nameof(WorkReportingCreate));
        }

        if (sc.Date < so.Date)
        {
            TempData["AdminError"] = "Submission closes must be on or after submission opens.";
            return RedirectToAction(nameof(WorkReportingCreate));
        }

        var cycle = await EnsureMonthlyWorkReportingCycleAsync();
        var periodKey = $"{ps.Year}-{ps.Month}";
        var periodKeyAlt = $"{ps.Year}-{ps.Month:D2}";

        var duplicate = await _context.WorkReportingCyclePeriods.AsNoTracking()
            .AnyAsync(p =>
                p.ReportingCycleId == cycle.Id &&
                (p.PeriodKey == periodKey || p.PeriodKey == periodKeyAlt));
        if (duplicate)
        {
            TempData["AdminError"] =
                $"A reporting period for this calendar month already exists ({periodKey}). Edit the existing row instead.";
            return RedirectToAction(nameof(WorkReportingCreate));
        }

        var maxOrder = await _context.WorkReportingCyclePeriods
            .Where(p => p.ReportingCycleId == cycle.Id)
            .Select(p => (int?)p.DisplayOrder)
            .MaxAsync() ?? -1;

        var entity = new WorkReportingCyclePeriod
        {
            ReportingCycleId = cycle.Id,
            PeriodKey = periodKey,
            PeriodLabel = periodLabel,
            PeriodStart = ps,
            PeriodEnd = pe,
            SubmissionOpens = so,
            SubmissionCloses = sc,
            DueDate = sc,
            IsActive = isActive,
            DisplayOrder = maxOrder + 1
        };

        _context.WorkReportingCyclePeriods.Add(entity);
        await _context.SaveChangesAsync();

        TempData["AdminMessage"] = $"Monthly reporting period \"{periodLabel}\" created.";
        return RedirectToAction(nameof(WorkReporting));
    }

    [HttpPost("work-reporting/period/{id:int}/edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> WorkReportingPeriodEditPost(
        int id,
        string periodLabel,
        int? periodStartDay,
        int? periodStartMonth,
        int? periodStartYear,
        int? periodEndDay,
        int? periodEndMonth,
        int? periodEndYear,
        int? submissionOpensDay,
        int? submissionOpensMonth,
        int? submissionOpensYear,
        int? submissionClosesDay,
        int? submissionClosesMonth,
        int? submissionClosesYear,
        bool isActive)
    {
        var entity = await _context.WorkReportingCyclePeriods
            .Include(p => p.ReportingCycle)
            .FirstOrDefaultAsync(p =>
                p.Id == id &&
                p.ReportingCycle.Code == WorkReportingMonthlyCycleCodes.MonthlyWorkUpdates);
        if (entity == null)
            return NotFound();

        periodLabel = (periodLabel ?? "").Trim();
        if (string.IsNullOrWhiteSpace(periodLabel))
        {
            TempData["AdminError"] = "Name is required.";
            return RedirectToAction(nameof(WorkReportingPeriodEdit), new { id });
        }

        if (!TryParseUtcDateParts(periodStartDay, periodStartMonth, periodStartYear, out var periodStart) ||
            !TryParseUtcDateParts(periodEndDay, periodEndMonth, periodEndYear, out var periodEnd) ||
            !TryParseUtcDateParts(submissionOpensDay, submissionOpensMonth, submissionOpensYear, out var submissionOpens) ||
            !TryParseUtcDateParts(submissionClosesDay, submissionClosesMonth, submissionClosesYear, out var submissionCloses))
        {
            TempData["AdminError"] =
                "Enter a complete valid date for period start, period end, submission opens, and submission closes (day, month and year).";
            return RedirectToAction(nameof(WorkReportingPeriodEdit), new { id });
        }

        periodStart = NormalizeUtcDate(periodStart);
        periodEnd = NormalizeUtcDate(periodEnd);
        submissionOpens = NormalizeUtcDate(submissionOpens);
        submissionCloses = NormalizeUtcDate(submissionCloses);

        if (periodEnd.Date < periodStart.Date)
        {
            TempData["AdminError"] = "Period end must be on or after period start.";
            return RedirectToAction(nameof(WorkReportingPeriodEdit), new { id });
        }

        if (submissionCloses.Date < submissionOpens.Date)
        {
            TempData["AdminError"] = "Submission closes must be on or after submission opens.";
            return RedirectToAction(nameof(WorkReportingPeriodEdit), new { id });
        }

        var periodKey = $"{periodStart.Year}-{periodStart.Month}";
        var periodKeyAlt = $"{periodStart.Year}-{periodStart.Month:D2}";
        var duplicate = await _context.WorkReportingCyclePeriods.AsNoTracking()
            .AnyAsync(p =>
                p.ReportingCycleId == entity.ReportingCycleId &&
                p.Id != id &&
                (p.PeriodKey == periodKey || p.PeriodKey == periodKeyAlt));
        if (duplicate)
        {
            TempData["AdminError"] =
                $"Another reporting period already uses month key {periodKey}. Change period start or edit the other row.";
            return RedirectToAction(nameof(WorkReportingPeriodEdit), new { id });
        }

        entity.PeriodKey = periodKey;
        entity.PeriodLabel = periodLabel;
        entity.PeriodStart = periodStart;
        entity.PeriodEnd = periodEnd;
        entity.SubmissionOpens = submissionOpens;
        entity.SubmissionCloses = submissionCloses;
        entity.DueDate = submissionCloses;
        entity.IsActive = isActive;

        await _context.SaveChangesAsync();

        TempData["AdminMessage"] = $"Monthly reporting period \"{periodLabel}\" updated.";
        return RedirectToAction(nameof(WorkReporting));
    }

    [HttpPost("work-reporting/weekly-config")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> WorkReportingWeeklyConfigSave(
        int periodStartDayOfWeek,
        int periodEndDayOfWeek,
        int dueDayOfWeek,
        WeeklyWorkReportingDueWeekOffset dueWeekOffset,
        int? firstReportingPeriodStartDay,
        int? firstReportingPeriodStartMonth,
        int? firstReportingPeriodStartYear,
        bool isActive)
    {
        if (!Enum.IsDefined(typeof(DayOfWeek), periodStartDayOfWeek) ||
            !Enum.IsDefined(typeof(DayOfWeek), periodEndDayOfWeek) ||
            !Enum.IsDefined(typeof(DayOfWeek), dueDayOfWeek))
        {
            TempData["AdminError"] = "Select valid days of the week for the reporting period and due date.";
            return RedirectToAction(nameof(WorkReporting));
        }

        if (!TryParseUtcDateParts(
                firstReportingPeriodStartDay,
                firstReportingPeriodStartMonth,
                firstReportingPeriodStartYear,
                out var firstReportingPeriodStart))
        {
            TempData["AdminError"] = "Enter a complete valid date for the first reporting period start (day, month and year).";
            return RedirectToAction(nameof(WorkReporting));
        }

        firstReportingPeriodStart = NormalizeUtcDate(firstReportingPeriodStart);

        var start = (DayOfWeek)periodStartDayOfWeek;
        var end = (DayOfWeek)periodEndDayOfWeek;
        if ((int)end < (int)start)
        {
            TempData["AdminError"] = "Period end day must be on or after period start day within the working week.";
            return RedirectToAction(nameof(WorkReporting));
        }

        if (firstReportingPeriodStart.DayOfWeek != start)
        {
            TempData["AdminError"] = $"First reporting period start must fall on a {start} (the same day as period starts).";
            return RedirectToAction(nameof(WorkReporting));
        }

        var config = await _weeklyUpdateService.GetOrCreateConfigAsync();
        config.PeriodStartDayOfWeek = start;
        config.PeriodEndDayOfWeek = end;
        config.DueDayOfWeek = (DayOfWeek)dueDayOfWeek;
        config.DueWeekOffset = dueWeekOffset;
        config.FirstReportingPeriodStart = firstReportingPeriodStart;
        config.IsActive = isActive;
        config.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        TempData["AdminMessage"] = "Weekly reporting settings saved.";
        return RedirectToAction(nameof(WorkReporting));
    }

    [HttpPost("work-reporting/weekly-scope/add")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> WorkReportingWeeklyScopeAdd(int projectId)
    {
        var project = await _context.Projects.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == projectId && !p.IsDeleted);
        if (project == null)
        {
            TempData["AdminError"] = "Work item not found.";
            return RedirectToAction(nameof(WorkReporting));
        }

        if (project.Status is not ("Active" or "Paused"))
        {
            TempData["AdminError"] = "Only Active or Paused work items can be added to weekly reporting scope.";
            return RedirectToAction(nameof(WorkReporting));
        }

        var exists = await _context.WeeklyWorkReportingScopeProjects
            .AnyAsync(x => x.ProjectId == projectId);
        if (exists)
        {
            TempData["AdminMessage"] = "That work item is already in weekly reporting scope.";
            return RedirectToAction(nameof(WorkReporting));
        }

        _context.WeeklyWorkReportingScopeProjects.Add(new WeeklyWorkReportingScopeProject
        {
            ProjectId = projectId,
            AddedAt = DateTime.UtcNow,
            AddedByEmail = User.Identity?.Name
        });
        await _context.SaveChangesAsync();

        TempData["AdminMessage"] = $"Added \"{project.Title}\" to weekly reporting scope.";
        return RedirectToAction(nameof(WorkReporting));
    }

    [HttpPost("work-reporting/weekly-scope/{scopeId:int}/remove")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> WorkReportingWeeklyScopeRemove(int scopeId)
    {
        var row = await _context.WeeklyWorkReportingScopeProjects
            .Include(x => x.Project)
            .FirstOrDefaultAsync(x => x.Id == scopeId);
        if (row == null)
        {
            TempData["AdminError"] = "Scope entry not found.";
            return RedirectToAction(nameof(WorkReporting));
        }

        var title = row.Project?.Title ?? "Work item";
        _context.WeeklyWorkReportingScopeProjects.Remove(row);
        await _context.SaveChangesAsync();

        TempData["AdminMessage"] = $"Removed \"{title}\" from weekly reporting scope.";
        return RedirectToAction(nameof(WorkReporting));
    }

    private static readonly HashSet<string> PerfValidPanels = new(StringComparer.OrdinalIgnoreCase)
    {
        "metrics", "commissions", "due-overrides", "product-exclusions"
    };

    [HttpGet("performance-reporting")]
    public async Task<IActionResult> PerformanceReporting(string? panel = null, string? tab = null, int? editId = null)
    {
        SetAdminChrome("admin-perf-reporting");
        if (string.IsNullOrWhiteSpace(panel) && !string.IsNullOrWhiteSpace(tab))
        {
            panel = tab.Trim().ToLowerInvariant() switch
            {
                "product-rules" => "commissions",
                _ => panel
            };
        }
        var normalized = string.IsNullOrWhiteSpace(panel) ? "metrics" : panel.Trim().ToLowerInvariant();
        if (!PerfValidPanels.Contains(normalized)) normalized = "metrics";

        var vm = new PerfReportingHubViewModel { Panel = normalized, EditId = editId };

        switch (normalized)
        {
            case "metrics":
                vm.Metrics = await _context.PerformanceMetrics.AsNoTracking()
                    .OrderBy(m => m.Identifier)
                    .Select(m => new PerfMetricRow
                    {
                        Id = m.Id,
                        Identifier = m.Identifier,
                        Title = m.Title,
                        Description = m.Description,
                        ValueType = m.ValueType.ToString(),
                        ValueTypeInt = (int)m.ValueType,
                        ValidFromYear = m.ValidFromYear,
                        ValidFromMonth = m.ValidFromMonth,
                        ValidFrom = $"{m.ValidFromYear}-{m.ValidFromMonth:D2}",
                        ApplicablePhases = m.ApplicablePhases,
                        ApplicableTypes = m.ApplicableTypes,
                        IsDisabled = m.IsDisabled
                    }).ToListAsync();
                break;
            case "commissions":
                vm.Commissions = await _context.Commissions.AsNoTracking()
                    .Include(c => c.Submissions)
                    .OrderByDescending(c => c.StartDate)
                    .Select(c => new PerfCommissionRow
                    {
                        Id = c.Id,
                        Name = c.Name,
                        Description = c.Description,
                        Quarter = c.Quarter,
                        StartDate = c.StartDate,
                        EndDate = c.EndDate,
                        OpenDate = c.OpenDate,
                        DueDate = c.DueDate,
                        IsActive = c.IsActive,
                        SubmissionCount = c.Submissions.Count
                    }).ToListAsync();
                break;
            case "due-overrides":
                vm.DueDateOverrides = await _context.PerformanceReportingDueDateOverrides.AsNoTracking()
                    .OrderByDescending(d => d.ReportingYear).ThenByDescending(d => d.ReportingMonth)
                    .Select(d => new PerfDueDateOverrideRow
                    {
                        Id = d.Id,
                        ReportingYear = d.ReportingYear,
                        ReportingMonth = d.ReportingMonth,
                        DueDate = d.DueDate,
                        Reason = d.Reason,
                        IsActive = d.IsActive
                    }).ToListAsync();
                break;
            case "product-exclusions":
                vm.ProductExclusions = await _context.PerformanceReportingProductExclusions.AsNoTracking()
                    .OrderBy(p => p.ProductName ?? p.ProductDocumentId)
                    .Select(p => new PerfProductExclusionRow
                    {
                        Id = p.Id,
                        ProductDocumentId = p.ProductDocumentId,
                        ProductName = p.ProductName,
                        ExclusionReason = p.ExclusionReason,
                        ExclusionFrom = $"{p.ExclusionFromYear}-{p.ExclusionFromMonth:D2}",
                        ExclusionUntil = p.ExclusionUntilYear != null ? $"{p.ExclusionUntilYear}-{p.ExclusionUntilMonth:D2}" : null,
                        ExclusionFromYear = p.ExclusionFromYear,
                        ExclusionFromMonth = p.ExclusionFromMonth,
                        ExclusionUntilYear = p.ExclusionUntilYear,
                        ExclusionUntilMonth = p.ExclusionUntilMonth,
                        IsActive = p.IsActive
                    }).ToListAsync();
                var ensureDocIds = vm.ProductExclusions.Select(p => p.ProductDocumentId).ToList();
                if (editId.HasValue)
                {
                    var editing = vm.ProductExclusions.FirstOrDefault(p => p.Id == editId.Value);
                    if (editing != null && !ensureDocIds.Contains(editing.ProductDocumentId, StringComparer.OrdinalIgnoreCase))
                        ensureDocIds.Add(editing.ProductDocumentId);
                }
                vm.ServiceRegisterProductOptions = await PerfProductExclusionOptionsBuilder.BuildActiveServiceRegisterOptionsAsync(
                    _context, _productsApi, ensureDocIds);
                break;
        }

        if (TempData["PerfMessage"] is string msg)
            ViewBag.AdminMessage = msg;
        if (TempData["PerfError"] is string perr)
            ViewBag.PerfError = perr;

        return View("~/Views/Modern/Admin/PerformanceReporting.cshtml", vm);
    }

    [HttpGet("performance-reporting/commission/create")]
    public async Task<IActionResult> PerfCommissionCreateForm()
    {
        SetAdminChrome("admin-perf-reporting");
        var today = DateTime.UtcNow.Date;
        var vm = new PerfCommissionRow
        {
            Id = 0,
            Name = "",
            Description = "",
            Quarter = "",
            StartDate = today,
            EndDate = today,
            OpenDate = today,
            DueDate = today,
            IsActive = true,
            SubmissionCount = 0
        };
        await PopulatePerfCommissionScopeOptionsAsync(vm, vm.EndDate);
        if (TempData["PerfError"] is string err)
            ViewBag.PerfError = err;
        return View("~/Views/Modern/Admin/PerfCommissionCreate.cshtml", vm);
    }

    [HttpGet("performance-reporting/commission/{id:int}/edit")]
    public async Task<IActionResult> PerfCommissionEditForm(int id)
    {
        SetAdminChrome("admin-perf-reporting");
        var c = await _context.Commissions.AsNoTracking()
            .Include(x => x.Submissions)
            .FirstOrDefaultAsync(x => x.Id == id);
        if (c == null) return NotFound();
        var vm = new PerfCommissionRow
        {
            Id = c.Id,
            Name = c.Name,
            Description = c.Description,
            Quarter = c.Quarter,
            StartDate = c.StartDate,
            EndDate = c.EndDate,
            OpenDate = c.OpenDate,
            DueDate = c.DueDate,
            IsActive = c.IsActive,
            SubmissionCount = c.Submissions.Count,
            InScopePhases = c.InScopePhases,
            InScopeTypes = c.InScopeTypes,
            IncludedPerformanceMetricIds = c.IncludedPerformanceMetricIds
        };
        await PopulatePerfCommissionScopeOptionsAsync(vm, c.EndDate);
        if (TempData["PerfMessage"] is string msg)
            ViewBag.AdminMessage = msg;
        if (TempData["PerfError"] is string err)
            ViewBag.PerfError = err;
        return View("~/Views/Modern/Admin/PerfCommissionEdit.cshtml", vm);
    }

    private static string? JoinCommissionCsv(IEnumerable<string>? values)
    {
        if (values == null)
            return null;
        var list = values
            .Select(v => v?.Trim())
            .Where(v => !string.IsNullOrEmpty(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return list.Count == 0 ? null : string.Join(", ", list);
    }

    private async Task<string?> NormalizeIncludedMetricIdsForSaveAsync(int[]? includedMetricIds, DateTime commissionEndDate)
    {
        if (includedMetricIds == null || includedMetricIds.Length == 0)
            return null;

        var year = commissionEndDate.Year;
        var month = commissionEndDate.Month;
        var validIds = await _context.PerformanceMetrics.AsNoTracking()
            .Where(m => !m.IsDisabled &&
                        (m.ValidFromYear < year ||
                         (m.ValidFromYear == year && m.ValidFromMonth <= month)))
            .Select(m => m.Id)
            .ToListAsync();

        var distinct = includedMetricIds.Distinct().Where(id => validIds.Contains(id)).Order().ToList();
        if (distinct.Count == 0)
            return null;
        if (distinct.Count >= validIds.Count)
            return null;

        return string.Join(",", distinct);
    }

    private async Task PopulatePerfCommissionScopeOptionsAsync(PerfCommissionRow vm, DateTime commissionEndDate)
    {
        // Source options from admin lookup configuration so they are available regardless of user catalogue access.
        vm.CataloguePhaseOptions = await _context.PhaseLookups.AsNoTracking()
            .Where(p => p.IsActive && p.Name != null && p.Name != "")
            .OrderBy(p => p.SortOrder)
            .ThenBy(p => p.Name)
            .Select(p => p.Name)
            .Distinct()
            .ToListAsync();

        vm.CatalogueTypeOptions = await _context.FipsTypes.AsNoTracking()
            .Where(t => t.Active && t.Name != null && t.Name != "")
            .OrderBy(t => t.DisplayOrder)
            .ThenBy(t => t.Name)
            .Select(t => t.Name)
            .Distinct()
            .ToListAsync();

        var year = commissionEndDate.Year;
        var month = commissionEndDate.Month;
        var metrics = await _context.PerformanceMetrics.AsNoTracking()
            .Where(m => !m.IsDisabled &&
                        (m.ValidFromYear < year ||
                         (m.ValidFromYear == year && m.ValidFromMonth <= month)))
            .OrderBy(m => m.Identifier)
            .ToListAsync();

        var selected = CommissionReportingMetricsHelper.ParseIncludedMetricIds(vm.IncludedPerformanceMetricIds);
        vm.MetricOptions = metrics.Select(m => new PerfCommissionMetricPickRow
        {
            Id = m.Id,
            Identifier = m.Identifier,
            Title = m.Title,
            Included = selected == null || selected.Contains(m.Id)
        }).ToList();
    }

    private string GetUserEmail() =>
        User.Identity?.Name
        ?? User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value
        ?? User.FindFirst("preferred_username")?.Value ?? "";

    // ── Performance Metrics CRUD ──

    private async Task PopulatePerfMetricFormOptionsAsync(PerfMetricRow vm, int? excludeMetricId = null)
    {
        vm.CataloguePhaseOptions = await _context.PhaseLookups.AsNoTracking()
            .Where(p => p.IsActive && p.Name != null && p.Name != "")
            .OrderBy(p => p.SortOrder)
            .ThenBy(p => p.Name)
            .Select(p => p.Name)
            .Distinct()
            .ToListAsync();

        vm.CatalogueTypeOptions = await _context.FipsTypes.AsNoTracking()
            .Where(t => t.Active && t.Name != null && t.Name != "")
            .OrderBy(t => t.DisplayOrder)
            .ThenBy(t => t.Name)
            .Select(t => t.Name)
            .Distinct()
            .ToListAsync();

        var metricsQuery = _context.PerformanceMetrics.AsNoTracking().Where(m => !m.IsDisabled);
        if (excludeMetricId.HasValue)
            metricsQuery = metricsQuery.Where(m => m.Id != excludeMetricId.Value);

        vm.ConditionalMetricOptions = await metricsQuery
            .OrderBy(m => m.Title)
            .Select(m => new PerfMetricConditionalOption { Id = m.Id, Title = m.Title })
            .ToListAsync();
    }

    private static readonly JsonSerializerOptions ValidationRulesJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static string? TryParseValidationRulesJson(string? validationRulesJson)
    {
        if (string.IsNullOrWhiteSpace(validationRulesJson))
            return "{}";

        try
        {
            JsonSerializer.Deserialize<ValidationRules>(validationRulesJson, ValidationRulesJsonOptions);
            return validationRulesJson;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string JoinSelectedLookups(List<string>? selected) =>
        selected != null && selected.Count > 0 ? string.Join(",", selected) : string.Empty;

    [HttpGet("performance-reporting/metric/create")]
    public async Task<IActionResult> PerfMetricCreateForm()
    {
        SetAdminChrome("admin-perf-reporting");
        var vm = new PerfMetricRow
        {
            ValidFromYear = DateTime.UtcNow.Year,
            ValidFromMonth = DateTime.UtcNow.Month,
            ValidationRules = "{\"required\": true, \"allowNull\": false}"
        };
        await PopulatePerfMetricFormOptionsAsync(vm);
        if (TempData["PerfError"] is string err)
            ViewBag.PerfError = err;
        return View("~/Views/Modern/Admin/PerfMetricCreate.cshtml", vm);
    }

    [HttpGet("performance-reporting/metric/{id:int}/edit")]
    public async Task<IActionResult> PerfMetricEditForm(int id)
    {
        SetAdminChrome("admin-perf-reporting");
        var e = await _context.PerformanceMetrics.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id);
        if (e == null) return NotFound();
        var vm = new PerfMetricRow
        {
            Id = e.Id,
            Identifier = e.Identifier,
            Title = e.Title,
            Description = e.Description,
            ValueType = e.ValueType.ToString(),
            ValueTypeInt = (int)e.ValueType,
            ValidFromYear = e.ValidFromYear,
            ValidFromMonth = e.ValidFromMonth,
            ValidFrom = $"{e.ValidFromYear}-{e.ValidFromMonth:D2}",
            ApplicablePhases = e.ApplicablePhases,
            ApplicableTypes = e.ApplicableTypes,
            ValidationRules = string.IsNullOrWhiteSpace(e.ValidationRules) ? "{}" : e.ValidationRules,
            ConditionalOnMetricId = e.ConditionalOnMetricId,
            IsDisabled = e.IsDisabled
        };
        await PopulatePerfMetricFormOptionsAsync(vm, id);
        if (TempData["PerfMessage"] is string msg)
            ViewBag.AdminMessage = msg;
        if (TempData["PerfError"] is string err)
            ViewBag.PerfError = err;
        return View("~/Views/Modern/Admin/PerfMetricEdit.cshtml", vm);
    }

    [HttpPost("performance-reporting/metric/create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PerfMetricCreate(
        string identifier,
        string title,
        string? description,
        int valueType,
        int validFromYear,
        int validFromMonth,
        string? validationRulesJson,
        List<string>? selectedPhases,
        List<string>? selectedTypes,
        int? conditionalOnMetricId)
    {
        identifier = (identifier ?? "").Trim();
        title = (title ?? "").Trim();
        if (string.IsNullOrWhiteSpace(identifier) || string.IsNullOrWhiteSpace(title))
        {
            TempData["PerfError"] = "Identifier and title are required.";
            return RedirectToAction(nameof(PerfMetricCreateForm));
        }

        var parsedRules = TryParseValidationRulesJson(validationRulesJson);
        if (parsedRules == null)
        {
            TempData["PerfError"] = "Validation rules must be valid JSON.";
            return RedirectToAction(nameof(PerfMetricCreateForm));
        }

        _context.PerformanceMetrics.Add(new PerformanceMetric
        {
            Identifier = identifier,
            Title = title,
            Description = description?.Trim() ?? "",
            ValueType = (Models.ValueType)valueType,
            ValidFromYear = validFromYear,
            ValidFromMonth = validFromMonth,
            ValidationRules = parsedRules,
            ApplicablePhases = JoinSelectedLookups(selectedPhases),
            ApplicableTypes = JoinSelectedLookups(selectedTypes),
            ConditionalOnMetricId = conditionalOnMetricId > 0 ? conditionalOnMetricId : null
        });
        await _context.SaveChangesAsync();
        TempData["PerfMessage"] = $"Metric \"{identifier}\" created.";
        return RedirectToAction(nameof(PerformanceReporting), new { panel = "metrics" });
    }

    [HttpPost("performance-reporting/metric/{id:int}/edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PerfMetricEdit(
        int id,
        string identifier,
        string title,
        string? description,
        int valueType,
        int validFromYear,
        int validFromMonth,
        string? validationRulesJson,
        List<string>? selectedPhases,
        List<string>? selectedTypes,
        int? conditionalOnMetricId)
    {
        var e = await _context.PerformanceMetrics.FindAsync(id);
        if (e == null) return NotFound();
        identifier = (identifier ?? "").Trim();
        title = (title ?? "").Trim();
        if (string.IsNullOrWhiteSpace(identifier) || string.IsNullOrWhiteSpace(title))
        {
            TempData["PerfError"] = "Identifier and title are required.";
            return RedirectToAction(nameof(PerfMetricEditForm), new { id });
        }

        var parsedRules = TryParseValidationRulesJson(validationRulesJson);
        if (parsedRules == null)
        {
            TempData["PerfError"] = "Validation rules must be valid JSON.";
            return RedirectToAction(nameof(PerfMetricEditForm), new { id });
        }

        e.Identifier = identifier;
        e.Title = title;
        e.Description = description?.Trim() ?? "";
        e.ValueType = (Models.ValueType)valueType;
        e.ValidFromYear = validFromYear;
        e.ValidFromMonth = validFromMonth;
        e.ValidationRules = parsedRules;
        e.ApplicablePhases = JoinSelectedLookups(selectedPhases);
        e.ApplicableTypes = JoinSelectedLookups(selectedTypes);
        e.ConditionalOnMetricId = conditionalOnMetricId > 0 ? conditionalOnMetricId : null;
        e.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        TempData["PerfMessage"] = $"Metric \"{identifier}\" updated.";
        return RedirectToAction(nameof(PerfMetricEditForm), new { id });
    }

    [HttpPost("performance-reporting/metric/{id:int}/toggle")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PerfMetricToggle(int id)
    {
        var e = await _context.PerformanceMetrics.FindAsync(id);
        if (e == null) return NotFound();
        e.IsDisabled = !e.IsDisabled; e.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        TempData["PerfMessage"] = $"\"{e.Identifier}\" is now {(e.IsDisabled ? "disabled" : "enabled")}.";
        return RedirectToAction(nameof(PerfMetricEditForm), new { id });
    }

    // ── Commissions CRUD ──

    [HttpPost("performance-reporting/commission/create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PerfCommissionCreate(
        string name,
        string? description,
        string? quarter,
        DateTime startDate,
        DateTime endDate,
        DateTime openDate,
        DateTime dueDate,
        string[]? inScopePhases,
        string[]? inScopeTypes,
        int[]? includedMetricIds)
    {
        name = (name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            TempData["PerfError"] = "Commission name is required.";
            return RedirectToAction(nameof(PerfCommissionCreateForm));
        }

        var includedIds = await NormalizeIncludedMetricIdsForSaveAsync(includedMetricIds, endDate);

        var entity = new Commission
        {
            Name = name,
            Description = description?.Trim(),
            Quarter = quarter?.Trim(),
            StartDate = startDate,
            EndDate = endDate,
            OpenDate = openDate,
            DueDate = dueDate,
            IsActive = true,
            InScopePhases = JoinCommissionCsv(inScopePhases),
            InScopeTypes = JoinCommissionCsv(inScopeTypes),
            IncludedPerformanceMetricIds = includedIds,
            CreatedBy = GetUserEmail(),
            UpdatedBy = GetUserEmail()
        };
        _context.Commissions.Add(entity);
        await _context.SaveChangesAsync();
        TempData["PerfMessage"] = $"Commission \"{name}\" created. Configure product rules and metrics below if needed.";
        return RedirectToAction(nameof(PerfCommissionEditForm), new { id = entity.Id });
    }

    [HttpPost("performance-reporting/commission/{id:int}/edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PerfCommissionEdit(
        int id,
        string name,
        string? description,
        string? quarter,
        DateTime startDate,
        DateTime endDate,
        DateTime openDate,
        DateTime dueDate,
        bool isActive,
        string[]? inScopePhases,
        string[]? inScopeTypes,
        int[]? includedMetricIds)
    {
        var e = await _context.Commissions.FindAsync(id);
        if (e == null) return NotFound();
        name = (name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            TempData["PerfError"] = "Commission name is required.";
            return RedirectToAction(nameof(PerfCommissionEditForm), new { id });
        }

        var includedIds = await NormalizeIncludedMetricIdsForSaveAsync(includedMetricIds, endDate);

        e.Name = name;
        e.Description = description?.Trim();
        e.Quarter = quarter?.Trim();
        e.StartDate = startDate;
        e.EndDate = endDate;
        e.OpenDate = openDate;
        e.DueDate = dueDate;
        e.IsActive = isActive;
        e.InScopePhases = JoinCommissionCsv(inScopePhases);
        e.InScopeTypes = JoinCommissionCsv(inScopeTypes);
        e.IncludedPerformanceMetricIds = includedIds;
        e.UpdatedBy = GetUserEmail();
        e.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        TempData["PerfMessage"] = $"Commission \"{name}\" saved.";
        return RedirectToAction(nameof(PerfCommissionEditForm), new { id });
    }

    // ── Due Date Overrides CRUD ──

    [HttpPost("performance-reporting/due-override/create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PerfDueOverrideCreate(int reportingYear, int reportingMonth, DateTime dueDate, string? reason)
    {
        _context.PerformanceReportingDueDateOverrides.Add(new PerformanceReportingDueDateOverride
        {
            ReportingYear = reportingYear,
            ReportingMonth = reportingMonth,
            DueDate = dueDate,
            Reason = reason?.Trim(),
            CreatedBy = GetUserEmail(),
            UpdatedBy = GetUserEmail()
        });
        await _context.SaveChangesAsync();
        TempData["PerfMessage"] = $"Due date override for {reportingYear}-{reportingMonth:D2} created.";
        return RedirectToAction(nameof(PerformanceReporting), new { panel = "due-overrides" });
    }

    [HttpPost("performance-reporting/due-override/{id:int}/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PerfDueOverrideDelete(int id)
    {
        var e = await _context.PerformanceReportingDueDateOverrides.FindAsync(id);
        if (e == null) return NotFound();
        _context.PerformanceReportingDueDateOverrides.Remove(e);
        await _context.SaveChangesAsync();
        TempData["PerfMessage"] = "Due date override removed.";
        return RedirectToAction(nameof(PerformanceReporting), new { panel = "due-overrides" });
    }

    // ── Product Exclusions CRUD ──

    [HttpPost("performance-reporting/product-exclusion/create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PerfProductExclusionCreate(string productDocumentId, string exclusionReason, int exclusionFromYear, int exclusionFromMonth, int? exclusionUntilYear, int? exclusionUntilMonth)
    {
        productDocumentId = (productDocumentId ?? "").Trim();
        if (string.IsNullOrWhiteSpace(productDocumentId))
        { TempData["PerfError"] = "Select a product from the Service Register."; return RedirectToAction(nameof(PerformanceReporting), new { panel = "product-exclusions" }); }

        var product = await PerfProductExclusionOptionsBuilder.ResolveByDocumentIdAsync(_context, _productsApi, productDocumentId);
        if (product == null)
        {
            TempData["PerfError"] = "Selected product is not a valid active Service Register entry.";
            return RedirectToAction(nameof(PerformanceReporting), new { panel = "product-exclusions" });
        }

        if (exclusionUntilYear.HasValue != exclusionUntilMonth.HasValue)
        {
            TempData["PerfError"] = "Provide both end year and month, or leave both blank for an open-ended exclusion.";
            return RedirectToAction(nameof(PerformanceReporting), new { panel = "product-exclusions" });
        }

        var duplicate = await _context.PerformanceReportingProductExclusions
            .AnyAsync(e => e.IsActive && e.ProductDocumentId == product.ProductDocumentId);
        if (duplicate)
        {
            TempData["PerfError"] = $"An active exclusion already exists for {product.DisplayName}.";
            return RedirectToAction(nameof(PerformanceReporting), new { panel = "product-exclusions" });
        }

        _context.PerformanceReportingProductExclusions.Add(new PerformanceReportingProductExclusion
        {
            ProductDocumentId = product.ProductDocumentId,
            FipsId = product.FipsId,
            ProductName = product.DisplayName,
            ExclusionReason = (exclusionReason ?? "").Trim(),
            ExclusionFromYear = exclusionFromYear,
            ExclusionFromMonth = exclusionFromMonth,
            ExclusionUntilYear = exclusionUntilYear,
            ExclusionUntilMonth = exclusionUntilMonth,
            CreatedBy = GetUserEmail(),
            UpdatedBy = GetUserEmail()
        });
        await _context.SaveChangesAsync();
        TempData["PerfMessage"] = $"Product exclusion for \"{product.DisplayName}\" created.";
        return RedirectToAction(nameof(PerformanceReporting), new { panel = "product-exclusions" });
    }

    [HttpPost("performance-reporting/product-exclusion/{id:int}/edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PerfProductExclusionEdit(int id, string productDocumentId, string exclusionReason, int exclusionFromYear, int exclusionFromMonth, int? exclusionUntilYear, int? exclusionUntilMonth)
    {
        productDocumentId = (productDocumentId ?? "").Trim();
        if (string.IsNullOrWhiteSpace(productDocumentId))
        {
            TempData["PerfError"] = "Select a product from the Service Register.";
            return RedirectToAction(nameof(PerformanceReporting), new { panel = "product-exclusions", editId = id });
        }

        var product = await PerfProductExclusionOptionsBuilder.ResolveByDocumentIdAsync(_context, _productsApi, productDocumentId);
        if (product == null)
        {
            TempData["PerfError"] = "Selected product is not a valid active Service Register entry.";
            return RedirectToAction(nameof(PerformanceReporting), new { panel = "product-exclusions", editId = id });
        }

        exclusionReason = (exclusionReason ?? "").Trim();
        if (string.IsNullOrWhiteSpace(exclusionReason))
        {
            TempData["PerfError"] = "Exclusion reason is required.";
            return RedirectToAction(nameof(PerformanceReporting), new { panel = "product-exclusions", editId = id });
        }

        if (exclusionUntilYear.HasValue != exclusionUntilMonth.HasValue)
        {
            TempData["PerfError"] = "Provide both end year and month, or leave both blank for an open-ended exclusion.";
            return RedirectToAction(nameof(PerformanceReporting), new { panel = "product-exclusions", editId = id });
        }

        var e = await _context.PerformanceReportingProductExclusions.FindAsync(id);
        if (e == null) return NotFound();

        var duplicate = await _context.PerformanceReportingProductExclusions
            .AnyAsync(x => x.IsActive && x.Id != id && x.ProductDocumentId == product.ProductDocumentId);
        if (duplicate)
        {
            TempData["PerfError"] = $"An active exclusion already exists for {product.DisplayName}.";
            return RedirectToAction(nameof(PerformanceReporting), new { panel = "product-exclusions", editId = id });
        }

        e.ProductDocumentId = product.ProductDocumentId;
        e.FipsId = product.FipsId;
        e.ProductName = product.DisplayName;
        e.ExclusionReason = exclusionReason;
        e.ExclusionFromYear = exclusionFromYear;
        e.ExclusionFromMonth = exclusionFromMonth;
        e.ExclusionUntilYear = exclusionUntilYear;
        e.ExclusionUntilMonth = exclusionUntilMonth;
        e.UpdatedAt = DateTime.UtcNow;
        e.UpdatedBy = GetUserEmail();
        await _context.SaveChangesAsync();
        TempData["PerfMessage"] = "Product exclusion updated.";
        return RedirectToAction(nameof(PerformanceReporting), new { panel = "product-exclusions" });
    }

    [HttpPost("performance-reporting/product-exclusion/{id:int}/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PerfProductExclusionDelete(int id)
    {
        var e = await _context.PerformanceReportingProductExclusions.FindAsync(id);
        if (e == null) return NotFound();
        _context.PerformanceReportingProductExclusions.Remove(e);
        await _context.SaveChangesAsync();
        TempData["PerfMessage"] = "Product exclusion removed.";
        return RedirectToAction(nameof(PerformanceReporting), new { panel = "product-exclusions" });
    }

    [HttpPost("performance-reporting/product-exclusion/{id:int}/toggle")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PerfProductExclusionToggle(int id)
    {
        var e = await _context.PerformanceReportingProductExclusions.FindAsync(id);
        if (e == null) return NotFound();
        e.IsActive = !e.IsActive; e.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        TempData["PerfMessage"] = $"Exclusion is now {(e.IsActive ? "active" : "inactive")}.";
        return RedirectToAction(nameof(PerformanceReporting), new { panel = "product-exclusions" });
    }

    // ── Demand prioritisation scoring framework (configures demand scorecard) ──

    private IActionResult RedirectToDemandScoringFramework(string tab, int? highlightSectionId = null)
    {
        if (highlightSectionId.HasValue)
            return RedirectToAction(nameof(DemandScoringFramework), new { tab, highlightSection = highlightSectionId.Value });
        return RedirectToAction(nameof(DemandScoringFramework), new { tab });
    }

    [HttpGet("demand-scoring-framework")]
    public async Task<IActionResult> DemandScoringFramework(string? tab = null, int? highlightSection = null)
    {
        SetAdminChrome("admin-index");
        await _demandScoringFramework.EnsureDefaultFrameworkSeededAsync();
        var bands = await _context.DemandScoringBandDefinitions.AsNoTracking().OrderBy(b => b.SortOrder).ToListAsync();
        var sections = await _context.DemandScoringFrameworkSections.AsNoTracking()
            .OrderBy(s => s.SortOrder)
            .Include(s => s.Questions)
            .ThenInclude(q => q.Options)
            .ToListAsync();
        foreach (var s in sections)
        {
            s.Questions = s.Questions.OrderBy(q => q.SortOrder).ToList();
            foreach (var q in s.Questions)
                q.Options = q.Options.OrderBy(o => o.SortOrder).ToList();
        }

        var tabNorm = (tab ?? "").Trim().ToLowerInvariant();
        if (tabNorm != "bands" && tabNorm != "sections")
            tabNorm = "sections";

        ViewBag.DemandScoringBands = bands;
        ViewBag.DemandScoringSections = sections;
        ViewBag.ScoringTab = tabNorm;
        ViewBag.HighlightSectionId = highlightSection;
        ViewBag.TotalSections = sections.Count;
        ViewBag.ActiveSectionCount = sections.Count(s => s.IsActive);
        ViewBag.TotalQuestions = sections.Sum(s => s.Questions.Count);
        ViewBag.TotalRawMax = sections.Sum(s => s.MaxPoints);
        if (TempData["AdminMessage"] is string msg)
            ViewBag.AdminMessage = msg;

        return View("~/Views/Modern/Admin/DemandScoringFramework.cshtml");
    }

    [HttpPost("demand-scoring-framework/band/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DemandScoringFrameworkBandSave(int id, string label, int minScaledInclusive, int maxScaledInclusive, int sortOrder, bool isActive)
    {
        var b = await _context.DemandScoringBandDefinitions.FindAsync(id);
        if (b == null) return NotFound();
        b.Label = (label ?? "").Trim();
        b.MinScaledInclusive = minScaledInclusive;
        b.MaxScaledInclusive = maxScaledInclusive;
        b.SortOrder = sortOrder;
        b.IsActive = isActive;
        await _context.SaveChangesAsync();
        TempData["AdminMessage"] = "Band updated.";
        return RedirectToDemandScoringFramework("bands");
    }

    [HttpPost("demand-scoring-framework/section/save")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DemandScoringFrameworkSectionSave(
        int? id, string key, string title, string? description, int maxPoints, int sortOrder, string? legacyColumn, bool isActive)
    {
        key = (key ?? "").Trim();
        title = (title ?? "").Trim();
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(title))
        {
            TempData["AdminMessage"] = "Section key and title are required.";
            return RedirectToDemandScoringFramework("sections");
        }

        legacyColumn = string.IsNullOrWhiteSpace(legacyColumn) ? null : legacyColumn.Trim();
        if (legacyColumn != null && legacyColumn.Length > 20)
            legacyColumn = legacyColumn[..20];

        DemandScoringFrameworkSection section;
        if (id.HasValue && id.Value > 0)
        {
            section = await _context.DemandScoringFrameworkSections.FirstOrDefaultAsync(s => s.Id == id.Value);
            if (section == null) return NotFound();
        }
        else
        {
            section = new DemandScoringFrameworkSection();
            _context.DemandScoringFrameworkSections.Add(section);
        }

        section.Key = key;
        section.Title = title;
        section.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        section.MaxPoints = maxPoints;
        section.SortOrder = sortOrder;
        section.LegacyColumn = legacyColumn;
        section.IsActive = isActive;
        await _context.SaveChangesAsync();
        TempData["AdminMessage"] = "Section saved.";
        return RedirectToDemandScoringFramework("sections", section.Id);
    }

    [HttpPost("demand-scoring-framework/section/{id:int}/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DemandScoringFrameworkSectionDelete(int id)
    {
        var section = await _context.DemandScoringFrameworkSections
            .Include(s => s.Questions)
            .ThenInclude(q => q.Options)
            .FirstOrDefaultAsync(s => s.Id == id);
        if (section == null) return NotFound();
        _context.DemandScoringFrameworkSections.Remove(section);
        await _context.SaveChangesAsync();
        TempData["AdminMessage"] = "Section removed.";
        return RedirectToDemandScoringFramework("sections");
    }

    [HttpPost("demand-scoring-framework/question/save")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DemandScoringFrameworkQuestionSave(
        int? id,
        int sectionId,
        string code,
        string prompt,
        string? hint,
        string questionType,
        bool isScored,
        int sortOrder,
        int? numberMin,
        int? numberMax,
        string? contextKey)
    {
        code = (code ?? "").Trim();
        prompt = (prompt ?? "").Trim();
        questionType = (questionType ?? "Radio").Trim();
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(prompt))
        {
            TempData["AdminMessage"] = "Question code and prompt are required.";
            return RedirectToDemandScoringFramework("sections", sectionId);
        }

        var exists = await _context.DemandScoringFrameworkQuestions.AnyAsync(q => q.Code == code && (!id.HasValue || q.Id != id.Value));
        if (exists)
        {
            TempData["AdminMessage"] = "Another question already uses that code.";
            return RedirectToDemandScoringFramework("sections", sectionId);
        }

        DemandScoringFrameworkQuestion qn;
        if (id.HasValue && id.Value > 0)
        {
            qn = await _context.DemandScoringFrameworkQuestions.FirstOrDefaultAsync(q => q.Id == id.Value);
            if (qn == null) return NotFound();
        }
        else
        {
            qn = new DemandScoringFrameworkQuestion { SectionId = sectionId };
            _context.DemandScoringFrameworkQuestions.Add(qn);
        }

        qn.SectionId = sectionId;
        qn.Code = code;
        qn.Prompt = prompt;
        qn.Hint = string.IsNullOrWhiteSpace(hint) ? null : hint.Trim();
        qn.QuestionType = questionType;
        qn.IsScored = isScored;
        qn.SortOrder = sortOrder;
        qn.NumberMin = numberMin;
        qn.NumberMax = numberMax;
        qn.ContextKey = string.IsNullOrWhiteSpace(contextKey) ? null : contextKey.Trim();
        await _context.SaveChangesAsync();
        TempData["AdminMessage"] = "Question saved.";
        return RedirectToDemandScoringFramework("sections", sectionId);
    }

    [HttpPost("demand-scoring-framework/question/{id:int}/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DemandScoringFrameworkQuestionDelete(int id)
    {
        var q = await _context.DemandScoringFrameworkQuestions
            .Include(x => x.Options)
            .FirstOrDefaultAsync(x => x.Id == id);
        if (q == null) return NotFound();
        var sectionId = q.SectionId;
        _context.DemandScoringFrameworkQuestions.Remove(q);
        await _context.SaveChangesAsync();
        TempData["AdminMessage"] = "Question removed.";
        return RedirectToDemandScoringFramework("sections", sectionId);
    }

    [HttpPost("demand-scoring-framework/option/save")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DemandScoringFrameworkOptionSave(int? id, int questionId, string label, int points, int sortOrder)
    {
        label = (label ?? "").Trim();
        if (string.IsNullOrWhiteSpace(label))
        {
            TempData["AdminMessage"] = "Option label is required.";
            var qErr = await _context.DemandScoringFrameworkQuestions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == questionId);
            return RedirectToDemandScoringFramework("sections", qErr?.SectionId);
        }

        DemandScoringFrameworkOption opt;
        if (id.HasValue && id.Value > 0)
        {
            opt = await _context.DemandScoringFrameworkOptions.FirstOrDefaultAsync(o => o.Id == id.Value);
            if (opt == null) return NotFound();
        }
        else
        {
            opt = new DemandScoringFrameworkOption { QuestionId = questionId };
            _context.DemandScoringFrameworkOptions.Add(opt);
        }

        opt.QuestionId = questionId;
        opt.Label = label;
        opt.Points = points;
        opt.SortOrder = sortOrder;
        await _context.SaveChangesAsync();
        TempData["AdminMessage"] = "Option saved.";
        var qn = await _context.DemandScoringFrameworkQuestions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == questionId);
        return RedirectToDemandScoringFramework("sections", qn?.SectionId);
    }

    [HttpPost("demand-scoring-framework/option/{id:int}/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DemandScoringFrameworkOptionDelete(int id)
    {
        var o = await _context.DemandScoringFrameworkOptions
            .Include(x => x.Question)
            .FirstOrDefaultAsync(x => x.Id == id);
        if (o == null) return NotFound();
        var sectionId = o.Question.SectionId;
        _context.DemandScoringFrameworkOptions.Remove(o);
        await _context.SaveChangesAsync();
        TempData["AdminMessage"] = "Option removed.";
        return RedirectToDemandScoringFramework("sections", sectionId);
    }

    // ── Standards configuration ──────────────────────────────────────────────

    [HttpGet("standards")]
    public IActionResult StandardsConfig(string? tab)
    {
        var panel = tab switch
        {
            "subcategories" => "std-subcategories",
            "functional" => "std-functional",
            _ => "std-categories"
        };
        return RedirectToAction("Index", new { panel });
    }

    [HttpPost("std-category/create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> StdCategoryCreate(string name, string? description, int sortOrder)
    {
        name = (name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            TempData["AdminMessage"] = "Name is required.";
            return RedirectToAction(nameof(Index), new { panel = "std-categories" });
        }
        var entity = new StandardCategory { Name = name, Description = description?.Trim(), SortOrder = sortOrder };
        _context.StandardCategories.Add(entity);
        await _context.SaveChangesAsync();
        TempData["AdminMessage"] = $"Standard category \"{name}\" added.";
        return RedirectToAction(nameof(LookupEdit), new { panel = "std-categories", id = entity.Id });
    }

    [HttpPost("std-category/{id:int}/edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> StdCategoryEdit(int id, string name, string? description, int sortOrder)
    {
        var entity = await _context.StandardCategories.FindAsync(id);
        if (entity == null) return NotFound();
        name = (name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            TempData["AdminMessage"] = "Name is required.";
            return RedirectToAction(nameof(LookupEdit), new { panel = "std-categories", id });
        }
        entity.Name = name;
        entity.Description = description?.Trim();
        entity.SortOrder = sortOrder;
        entity.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        TempData["AdminMessage"] = $"Standard category \"{name}\" updated.";
        return RedirectToAction(nameof(Index), new { panel = "std-categories" });
    }

    [HttpPost("std-category/{id:int}/toggle")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> StdCategoryToggle(int id)
    {
        var entity = await _context.StandardCategories.FindAsync(id);
        if (entity == null) return NotFound();
        entity.IsActive = !entity.IsActive;
        entity.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        TempData["AdminMessage"] = $"\"{entity.Name}\" is now {(entity.IsActive ? "active" : "inactive")}.";
        return RedirectToAction(nameof(LookupEdit), new { panel = "std-categories", id });
    }

    [HttpPost("std-subcategory/create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> StdSubCategoryCreate(string name, string? description, int categoryId, int sortOrder)
    {
        name = (name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            TempData["AdminMessage"] = "Name is required.";
            return RedirectToAction(nameof(Index), new { panel = "std-subcategories" });
        }
        var entity = new StandardSubCategory { Name = name, Description = description?.Trim(), CategoryId = categoryId, SortOrder = sortOrder };
        _context.StandardSubCategories.Add(entity);
        await _context.SaveChangesAsync();
        TempData["AdminMessage"] = $"Sub-category \"{name}\" added.";
        return RedirectToAction(nameof(LookupEdit), new { panel = "std-subcategories", id = entity.Id });
    }

    [HttpPost("std-subcategory/{id:int}/edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> StdSubCategoryEdit(int id, string name, string? description, int categoryId, int sortOrder)
    {
        var entity = await _context.StandardSubCategories.FindAsync(id);
        if (entity == null) return NotFound();
        name = (name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            TempData["AdminMessage"] = "Name is required.";
            return RedirectToAction(nameof(LookupEdit), new { panel = "std-subcategories", id });
        }
        entity.Name = name;
        entity.Description = description?.Trim();
        entity.CategoryId = categoryId;
        entity.SortOrder = sortOrder;
        entity.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        TempData["AdminMessage"] = $"Sub-category \"{name}\" updated.";
        return RedirectToAction(nameof(Index), new { panel = "std-subcategories" });
    }

    [HttpPost("std-subcategory/{id:int}/toggle")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> StdSubCategoryToggle(int id)
    {
        var entity = await _context.StandardSubCategories.FindAsync(id);
        if (entity == null) return NotFound();
        entity.IsActive = !entity.IsActive;
        entity.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        TempData["AdminMessage"] = $"\"{entity.Name}\" is now {(entity.IsActive ? "active" : "inactive")}.";
        return RedirectToAction(nameof(LookupEdit), new { panel = "std-subcategories", id });
    }

    // ── FIPS configuration ──────────────────────────────────────────────────

    private async Task<IActionResult?> RequireFipsDatabaseAdminAsync()
    {
        if (!await _globalFeatureToggle.IsFeatureEnabledForPrincipalAsync(FeatureCodes.Fips, User))
        {
            TempData["AdminMessage"] =
                "FIPS database configuration is turned off. Enable **FIPS service register** under Feature settings to manage synced CMDB lists; when it stays off, use the CMS for FIPS product data.";
            return RedirectToAction(nameof(Index), new { panel = "feature-settings" });
        }

        return null;
    }

    [HttpGet("fips")]
    public async Task<IActionResult> FipsConfig(string? tab)
    {
        var guard = await RequireFipsDatabaseAdminAsync();
        if (guard != null)
            return guard;

        var panel = tab switch
        {
            "types" => "fips-types",
            "businessareas" => "fips-business-areas",
            "usergroups" => "fips-user-groups",
            "contactroles" => "fips-contact-roles",
            "categorisation" => "fips-categorisation",
            _ => "fips-channels"
        };
        return RedirectToAction("Index", new { panel });
    }

    [HttpPost("fips/channel/add")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> FipsAddChannel(string name, string? description, int displayOrder)
    {
        var guard = await RequireFipsDatabaseAdminAsync();
        if (guard != null)
            return guard;

        _context.FipsChannels.Add(new FipsChannel { Name = name.Trim(), Description = description?.Trim(), DisplayOrder = displayOrder });
        await _context.SaveChangesAsync();
        TempData["AdminMessage"] = "Channel added.";
        return RedirectToAction("Index", new { panel = "fips-channels" });
    }

    [HttpPost("fips/channel/{id:int}/toggle")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> FipsToggleChannel(int id)
    {
        var guard = await RequireFipsDatabaseAdminAsync();
        if (guard != null)
            return guard;

        var e = await _context.FipsChannels.FindAsync(id);
        if (e != null) { e.Active = !e.Active; await _context.SaveChangesAsync(); }
        return RedirectToAction("Index", new { panel = "fips-channels" });
    }

    [HttpPost("fips/type/add")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> FipsAddType(string name, string? description, int displayOrder)
    {
        var guard = await RequireFipsDatabaseAdminAsync();
        if (guard != null)
            return guard;

        _context.FipsTypes.Add(new FipsType { Name = name.Trim(), Description = description?.Trim(), DisplayOrder = displayOrder });
        await _context.SaveChangesAsync();
        TempData["AdminMessage"] = "Type added.";
        return RedirectToAction("Index", new { panel = "fips-types" });
    }

    [HttpPost("fips/type/{id:int}/toggle")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> FipsToggleType(int id)
    {
        var guard = await RequireFipsDatabaseAdminAsync();
        if (guard != null)
            return guard;

        var e = await _context.FipsTypes.FindAsync(id);
        if (e != null) { e.Active = !e.Active; await _context.SaveChangesAsync(); }
        return RedirectToAction("Index", new { panel = "fips-types" });
    }

    [HttpPost("fips/business-area/add")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> FipsAddBusinessArea()
    {
        var guard = await RequireFipsDatabaseAdminAsync();
        if (guard != null)
            return guard;

        TempData["AdminMessage"] =
            "Business areas are edited under Admin → Business areas. The FIPS list mirrors that data automatically.";
        return RedirectToAction("Index", new { panel = "business-areas" });
    }

    [HttpPost("fips/business-area/{id:int}/toggle")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> FipsToggleBusinessArea(int id)
    {
        var guard = await RequireFipsDatabaseAdminAsync();
        if (guard != null)
            return guard;

        TempData["AdminMessage"] =
            "Activate or deactivate business areas under Admin → Business areas. The FIPS list mirrors that data automatically.";
        return RedirectToAction("Index", new { panel = "business-areas" });
    }

    [HttpPost("fips/contact-role/add")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> FipsAddContactRole(string name, string? description, bool allowMultiple, int displayOrder)
    {
        var guard = await RequireFipsDatabaseAdminAsync();
        if (guard != null)
            return guard;

        _context.FipsContactRoles.Add(new FipsContactRole { Name = name.Trim(), Description = description?.Trim(), AllowMultiple = allowMultiple, DisplayOrder = displayOrder });
        await _context.SaveChangesAsync();
        TempData["AdminMessage"] = "Contact role added.";
        return RedirectToAction("Index", new { panel = "fips-contact-roles" });
    }

    [HttpPost("fips/contact-role/{id:int}/toggle")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> FipsToggleContactRole(int id)
    {
        var guard = await RequireFipsDatabaseAdminAsync();
        if (guard != null)
            return guard;

        var e = await _context.FipsContactRoles.FindAsync(id);
        if (e != null) { e.Active = !e.Active; await _context.SaveChangesAsync(); }
        return RedirectToAction("Index", new { panel = "fips-contact-roles" });
    }

    [HttpPost("fips/categorisation-group/add")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> FipsAddCategorisationGroup(string name, string? description, int displayOrder)
    {
        var guard = await RequireFipsDatabaseAdminAsync();
        if (guard != null)
            return guard;

        _context.FipsCategorisationGroups.Add(new FipsCategorisationGroup
        {
            Name = name.Trim(),
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            DisplayOrder = displayOrder,
            Active = true
        });
        await _context.SaveChangesAsync();
        TempData["AdminMessage"] = "Categorisation group added. Add values below.";
        return RedirectToAction("Index", new { panel = "fips-categorisation" });
    }

    [HttpPost("fips/categorisation-group/{id:int}/toggle")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> FipsToggleCategorisationGroup(int id)
    {
        var guard = await RequireFipsDatabaseAdminAsync();
        if (guard != null)
            return guard;

        var e = await _context.FipsCategorisationGroups.FindAsync(id);
        if (e != null)
        {
            e.Active = !e.Active;
            await _context.SaveChangesAsync();
        }

        return RedirectToAction("Index", new { panel = "fips-categorisation" });
    }

    [HttpPost("fips/categorisation-item/add")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> FipsAddCategorisationItem(int groupId, string name, string? description, int displayOrder)
    {
        var guard = await RequireFipsDatabaseAdminAsync();
        if (guard != null)
            return guard;

        var hasGroup = await _context.FipsCategorisationGroups.AnyAsync(g => g.Id == groupId);
        if (!hasGroup)
            return NotFound();

        _context.FipsCategorisationItems.Add(new FipsCategorisationItem
        {
            FipsCategorisationGroupId = groupId,
            Name = name.Trim(),
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            DisplayOrder = displayOrder,
            Active = true
        });
        await _context.SaveChangesAsync();
        TempData["AdminMessage"] = "Categorisation value added.";
        return RedirectToAction("Index", new { panel = "fips-categorisation" });
    }

    [HttpPost("fips/categorisation-item/{id:int}/toggle")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> FipsToggleCategorisationItem(int id)
    {
        var guard = await RequireFipsDatabaseAdminAsync();
        if (guard != null)
            return guard;

        var e = await _context.FipsCategorisationItems.FindAsync(id);
        if (e != null)
        {
            e.Active = !e.Active;
            await _context.SaveChangesAsync();
        }

        return RedirectToAction("Index", new { panel = "fips-categorisation" });
    }

    // ════════════════════════════════════════════════════════════
    //  RAID Settings (generic RaidLookupBase admin)
    // ════════════════════════════════════════════════════════════

    private sealed record RaidLookupDef(
        string Key,
        string Label,
        string? Description,
        Func<CompassDbContext, IQueryable<RaidLookupBase>> Query,
        Func<RaidLookupBase> Factory);

    private static readonly IReadOnlyList<RaidLookupDef> _raidDefs = new List<RaidLookupDef>
    {
        Rd<RiskStatus>("risk-statuses", "Risk statuses", "Core risk workflow states."),
        Rd<RiskPriority>("risk-priorities", "Risk priorities", "Priority scale applied to risks."),
        Rd<RiskLikelihood>("risk-likelihoods", "Risk likelihoods", "Likelihood scale used to calculate scores."),
        Rd<RiskImpactLevel>("risk-impact-levels", "Risk impact levels", "Impact scale for risks."),
        Rd<RiskProximity>("risk-proximities", "Risk proximities", "Timeline bands for when a risk may materialise."),
        Rd<RiskTreatment>("risk-treatments", "Risk treatments", "Primary treatment strategies for managing risks."),
        Rd<RiskCategory>("risk-categories", "Risk categories", "Categorisation for risk libraries."),
        Rd<IssueStatus>("issue-statuses", "Issue statuses", "Issue workflow states."),
        Rd<IssuePriority>("issue-priorities", "Issue priorities", "Priority options for issues."),
        Rd<IssueSeverity>("issue-severities", "Issue severities", "Severity scale mapped to RAID reporting."),
        Rd<IssueCategory>("issue-categories", "Issue categories", "Issue categorisation used in dashboards."),
        Rd<ActionStatus>("action-statuses", "Action statuses", "Workflow states shown on every action."),
        Rd<ActionPriority>("action-priorities", "Action priorities", "Priority options shared across action listings."),
        Rd<ActionType>("action-types", "Action types", "Helps teams categorise actions for reporting."),
        Rd<ActionCategory>("action-categories", "Action categories", "Used to slice actions by category."),
        Rd<ActionImpactLevel>("action-impact-levels", "Action impact levels", "Impact level choices aligned with RAID reporting."),
        Rd<ActionReminderFrequency>("action-reminder-frequencies", "Action reminder frequencies", "Determines how often reminders fire for actions."),
        Rd<ActionEscalationThreshold>("action-escalation-thresholds", "Action escalation thresholds", "Number of days before escalation is triggered."),
        Rd<DecisionStatus>("decision-statuses", "Decision statuses", "Status values for decisions."),
        Rd<DecisionPriority>("decision-priorities", "Decision priorities", "Decision priority labels."),
        Rd<DecisionOutcome>("decision-outcomes", "Decision outcomes", "Possible outcomes recorded when a decision is made."),
        Rd<DecisionImplementationStatus>("decision-implementation-statuses", "Decision implementation statuses", "Tracks implementation progress."),
        Rd<RaidEvidenceType>("raid-evidence-types", "Evidence types", "Shared evidence/documentation types."),
        Rd<GovernanceBoard>("governance-boards", "Governance boards", "Committees and boards used for RAID escalation."),
        Rd<DemandRequestStatus>("demand-request-statuses", "Demand request statuses", "Workflow states for demand requests."),
        Rd<DemandTriageOutcomeStage>("triage-outcome-stages", "Triage outcome stages", "Stages recorded with demand triage outcomes (Active, Draft, In delivery, Rejected, Paused)."),
        Rd<AssumptionStatus>("assumption-statuses", "Assumption statuses", "Lifecycle states for delivery assumptions."),
        Rd<AssumptionCriticality>("assumption-criticalities", "Assumption criticalities", "How critical the assumption is if it fails."),
        Rd<DependencyCriticality>("dependency-criticalities", "Dependency criticalities", "Criticality of dependency relationships."),
        Rd<DependencyLinkType>("dependency-link-types", "Dependency link types", "Standard dependency classifications."),
        Rd<NearMissType>("near-miss-types", "Near miss types", "Near miss or unexpected issue classification."),
        Rd<NearMissSeriousness>("near-miss-seriousness", "Near miss seriousness", "Seriousness scale (1–4) for near misses."),
        Rd<NearMissStatus>("near-miss-statuses", "Near miss statuses", "Open or closed workflow states for near misses.")
    };

    private static RaidLookupDef Rd<T>(string key, string label, string? desc) where T : RaidLookupBase, new() =>
        new(key, label, desc, ctx => ctx.Set<T>().Cast<RaidLookupBase>(), () => new T());

    private RaidLookupDef? FindRaidDef(string? key) =>
        _raidDefs.FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase));

    [HttpPost("raid-lookup/create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RaidLookupCreate(string lookupKey, string code, string label, string? description, int sortOrder, int? matrixScore = null)
    {
        var def = FindRaidDef(lookupKey);
        if (def == null) return NotFound();
        code = (code ?? "").Trim();
        label = (label ?? "").Trim();
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(label))
        {
            TempData["AdminMessage"] = "Code and label are required.";
            return RedirectToAction(nameof(LookupCreate), new { panel = lookupKey });
        }
        var entity = def.Factory();
        entity.Code = code;
        entity.Label = label;
        entity.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        entity.SortOrder = sortOrder;
        entity.IsActive = true;
        entity.CreatedAt = DateTime.UtcNow;
        entity.UpdatedAt = DateTime.UtcNow;
        if (matrixScore is >= 1 and <= 5)
        {
            switch (entity)
            {
                case RiskLikelihood rlCreate:
                    rlCreate.MatrixScore = matrixScore.Value;
                    break;
                case RiskImpactLevel imCreate:
                    imCreate.MatrixScore = matrixScore.Value;
                    break;
            }
        }

        _context.Add(entity);
        await _context.SaveChangesAsync();

        TempData["AdminMessage"] = $"Added \"{label}\" to {def.Label.ToLowerInvariant()}.";
        return RedirectToAction(nameof(LookupEdit), new { panel = lookupKey, id = entity.Id });
    }

    [HttpPost("raid-lookup/{lookupKey}/{id:int}/edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RaidLookupEdit(string lookupKey, int id, string code, string label, string? description, int sortOrder, int? matrixScore = null)
    {
        var def = FindRaidDef(lookupKey);
        if (def == null) return NotFound();
        var entity = await def.Query(_context).FirstOrDefaultAsync(x => x.Id == id);
        if (entity == null) return NotFound();
        code = (code ?? "").Trim();
        label = (label ?? "").Trim();
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(label))
        {
            TempData["AdminMessage"] = "Code and label are required.";
            return RedirectToAction(nameof(LookupEdit), new { panel = lookupKey, id });
        }
        entity.Code = code;
        entity.Label = label;
        entity.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        entity.SortOrder = sortOrder;
        entity.UpdatedAt = DateTime.UtcNow;

        if (matrixScore is >= 1 and <= 5)
        {
            switch (entity)
            {
                case RiskLikelihood rlEdit:
                    rlEdit.MatrixScore = matrixScore.Value;
                    break;
                case RiskImpactLevel imEdit:
                    imEdit.MatrixScore = matrixScore.Value;
                    break;
            }
        }

        await _context.SaveChangesAsync();
        TempData["AdminMessage"] = $"Updated \"{label}\".";
        return RedirectToAction(nameof(Index), new { panel = lookupKey });
    }

    [HttpPost("raid-lookup/{lookupKey}/{id:int}/toggle")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RaidLookupToggle(string lookupKey, int id)
    {
        var def = FindRaidDef(lookupKey);
        if (def == null) return NotFound();
        var entity = await def.Query(_context).FirstOrDefaultAsync(x => x.Id == id);
        if (entity == null) return NotFound();
        entity.IsActive = !entity.IsActive;
        entity.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        TempData["AdminMessage"] = $"\"{entity.Label}\" is now {(entity.IsActive ? "active" : "inactive")}.";
        return RedirectToAction(nameof(LookupEdit), new { panel = lookupKey, id });
    }

    [HttpPost("risk-tier/{id:int}/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RiskTierDelete(int id)
    {
        var entity = await _context.RiskTiers.FindAsync(id);
        if (entity == null) return NotFound();

        var inUse = await _context.Risks.AsNoTracking().AnyAsync(r => r.RiskTierId == id);
        if (inUse)
        {
            TempData["AdminMessage"] = "Cannot delete this tier while risks still reference it. Deactivate it instead.";
            return RedirectToAction(nameof(LookupEdit), new { panel = "risk-tiers", id });
        }

        var name = entity.Name;
        _context.RiskTiers.Remove(entity);
        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            TempData["AdminMessage"] = "Cannot delete this tier: other records still reference it.";
            return RedirectToAction(nameof(LookupEdit), new { panel = "risk-tiers", id });
        }

        TempData["AdminMessage"] = $"Deleted risk tier \"{name}\".";
        return RedirectToAction(nameof(Index), new { panel = "risk-tiers" });
    }

    [HttpPost("raid-lookup/{lookupKey}/{id:int}/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RaidLookupDelete(string lookupKey, int id)
    {
        var def = FindRaidDef(lookupKey);
        if (def == null) return NotFound();

        var entity = await def.Query(_context).FirstOrDefaultAsync(x => x.Id == id);
        if (entity == null) return NotFound();

        var label = entity.Label;
        _context.Remove(entity);
        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            TempData["AdminMessage"] = $"Cannot delete \"{label}\": other records still reference this value.";
            return RedirectToAction(nameof(LookupEdit), new { panel = lookupKey, id });
        }

        TempData["AdminMessage"] = $"Deleted \"{label}\".";
        return RedirectToAction(nameof(Index), new { panel = lookupKey });
    }

    [HttpPost("raid-lookup/{lookupKey}/seed")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RaidLookupSeed(string lookupKey)
    {
        if (string.Equals(lookupKey, "risk-appetites", StringComparison.OrdinalIgnoreCase))
        {
            if (!RaidLookupSeedData.TryGetValues(lookupKey, out var appetiteSeeds) || appetiteSeeds.Count == 0)
            {
                TempData["AdminMessage"] = "No recommended values for this lookup.";
                return RedirectToAction(nameof(Index), new { panel = lookupKey });
            }

            var existingNames = await _context.RiskAppetiteLookups.AsNoTracking()
                .Select(x => x.Name.ToLower())
                .ToListAsync();
            var appetiteToAdd = appetiteSeeds
                .Where(s => !existingNames.Contains(s.Label.ToLowerInvariant()))
                .ToList();
            if (!appetiteToAdd.Any())
            {
                TempData["AdminMessage"] = "All recommended values already exist.";
                return RedirectToAction(nameof(Index), new { panel = lookupKey });
            }

            foreach (var s in appetiteToAdd)
            {
                _context.RiskAppetiteLookups.Add(new RiskAppetiteLookup
                {
                    Name = s.Label,
                    Description = s.Description,
                    SortOrder = s.SortOrder,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                });
            }

            await _context.SaveChangesAsync();
            TempData["AdminMessage"] = $"Added {appetiteToAdd.Count} recommended value{(appetiteToAdd.Count == 1 ? "" : "s")}.";
            return RedirectToAction(nameof(Index), new { panel = lookupKey });
        }

        var def = FindRaidDef(lookupKey);
        if (def == null) return NotFound();
        if (!RaidLookupSeedData.TryGetValues(lookupKey, out var seeds) || seeds.Count == 0)
        {
            TempData["AdminMessage"] = "No recommended values for this lookup.";
            return RedirectToAction(nameof(Index), new { panel = lookupKey });
        }
        if (string.Equals(lookupKey, "risk-categories", StringComparison.OrdinalIgnoreCase))
        {
            var strategy = _context.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await _context.Database.BeginTransactionAsync();
                var links = await _context.RiskRiskCategories.ToListAsync();
                if (links.Count > 0)
                    _context.RiskRiskCategories.RemoveRange(links);

                var risksWithCategory = await _context.Risks
                    .Where(r => r.RiskCategoryId != null)
                    .ToListAsync();
                foreach (var risk in risksWithCategory)
                    risk.RiskCategoryId = null;

                var existingCats = await _context.RiskCategories.ToListAsync();
                if (existingCats.Count > 0)
                    _context.RiskCategories.RemoveRange(existingCats);

                await _context.SaveChangesAsync();

                foreach (var s in seeds)
                {
                    _context.RiskCategories.Add(new RiskCategory
                    {
                        Code = s.Code,
                        Label = s.Label,
                        Description = s.Description,
                        SortOrder = s.SortOrder,
                        IsActive = true,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    });
                }

                await _context.SaveChangesAsync();
                await tx.CommitAsync();
            });
            TempData["AdminMessage"] = $"Replaced risk categories with {seeds.Count} recommended values.";
            return RedirectToAction(nameof(Index), new { panel = lookupKey });
        }
        if (string.Equals(lookupKey, "risk-proximities", StringComparison.OrdinalIgnoreCase))
        {
            var strategy = _context.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await _context.Database.BeginTransactionAsync();

                var risksWithProximity = await _context.Risks
                    .Where(r => r.RiskProximityId != null)
                    .ToListAsync();
                foreach (var risk in risksWithProximity)
                    risk.RiskProximityId = null;

                var existingProximities = await _context.RiskProximities.ToListAsync();
                if (existingProximities.Count > 0)
                    _context.RiskProximities.RemoveRange(existingProximities);

                await _context.SaveChangesAsync();

                foreach (var s in seeds)
                {
                    _context.RiskProximities.Add(new RiskProximity
                    {
                        Code = s.Code,
                        Label = s.Label,
                        Description = s.Description,
                        SortOrder = s.SortOrder,
                        IsActive = true,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    });
                }

                await _context.SaveChangesAsync();
                await tx.CommitAsync();
            });
            TempData["AdminMessage"] = $"Replaced risk proximities with {seeds.Count} recommended values.";
            return RedirectToAction(nameof(Index), new { panel = lookupKey });
        }

        var existingCodes = await def.Query(_context).Select(x => x.Code.ToLower()).ToListAsync();
        var toAdd = seeds.Where(s => !existingCodes.Contains(s.Code.ToLowerInvariant())).ToList();
        if (!toAdd.Any())
        {
            TempData["AdminMessage"] = "All recommended values already exist.";
            return RedirectToAction(nameof(Index), new { panel = lookupKey });
        }
        foreach (var s in toAdd)
        {
            var e = def.Factory();
            e.Code = s.Code; e.Label = s.Label; e.Description = s.Description;
            e.SortOrder = s.SortOrder; e.IsActive = true;
            e.CreatedAt = DateTime.UtcNow; e.UpdatedAt = DateTime.UtcNow;
            _context.Add(e);
        }
        await _context.SaveChangesAsync();
        TempData["AdminMessage"] = $"Added {toAdd.Count} recommended value{(toAdd.Count == 1 ? "" : "s")}.";
        return RedirectToAction(nameof(Index), new { panel = lookupKey });
    }

    // ── API Token Permissions (modern) ─────────────────────────────

    private static readonly string[] ApiTokenResources = ApiTokenResourceCatalog.Resources;

    [HttpGet("api-tokens/new")]
    [RequireSuperAdmin]
    public IActionResult ApiTokenCreate()
    {
        SetAdminChrome("admin-index");
        if (TempData["AdminError"] is string createErr)
            ViewBag.AdminError = createErr;
        return View("~/Views/Modern/Admin/ApiTokenCreate.cshtml");
    }

    [HttpPost("api-tokens/new")]
    [ValidateAntiForgeryToken]
    [RequireSuperAdmin]
    public async Task<IActionResult> ApiTokenCreate(string name, string? description, DateTime? expiresAt)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            TempData["AdminError"] = "Token name is required.";
            return RedirectToAction(nameof(ApiTokenCreate));
        }

        try
        {
            var userEmail = User.Identity?.Name ?? "unknown";
            var token = await _apiTokenService.CreateTokenAsync(
                name.Trim(),
                description?.Trim() ?? string.Empty,
                userEmail,
                expiresAt);
            TempData["AdminMessage"] = "API token created successfully. Copy the token now — you will not be able to see it again.";
            TempData["NewToken"] = token.Token;
            return RedirectToAction(nameof(ApiTokenDetail), new { id = token.Id });
        }
        catch (Exception)
        {
            TempData["AdminError"] = "An error occurred while creating the API token.";
            return RedirectToAction(nameof(ApiTokenCreate));
        }
    }

    [HttpGet("api-tokens/{id:int}")]
    [RequireSuperAdmin]
    public async Task<IActionResult> ApiTokenDetail(int id)
    {
        var token = await _apiTokenService.GetByIdAsync(id);
        if (token == null)
        {
            TempData["AdminError"] = "API token not found.";
            return RedirectToAction(nameof(Index), new { panel = "api-tokens" });
        }

        var permissions = await _apiTokenService.GetPermissionsAsync(id);

        ViewBag.Token = token;
        ViewBag.Permissions = permissions;
        ViewBag.Resources = ApiTokenResources;
        if (TempData["AdminMessage"] is string msg)
            ViewBag.AdminMessage = msg;
        if (TempData["AdminError"] is string err)
            ViewBag.AdminError = err;

        SetAdminChrome("admin-index");
        return View("~/Views/Modern/Admin/ApiTokenPermissions.cshtml");
    }

    [HttpPost("api-tokens/{id:int}/save-permissions")]
    [ValidateAntiForgeryToken]
    [RequireSuperAdmin]
    public async Task<IActionResult> ApiTokenSavePermissions(int id, Dictionary<string, string> permissions)
    {
        try
        {
            var permissionsDict = new Dictionary<string, (bool read, bool create, bool update, bool delete)>();

            foreach (var resource in ApiTokenResources)
            {
                var read = permissions.ContainsKey($"{resource}_read") && permissions[$"{resource}_read"] == "on";
                var create = permissions.ContainsKey($"{resource}_create") && permissions[$"{resource}_create"] == "on";
                var update = permissions.ContainsKey($"{resource}_update") && permissions[$"{resource}_update"] == "on";
                var delete = permissions.ContainsKey($"{resource}_delete") && permissions[$"{resource}_delete"] == "on";

                if (read || create || update || delete)
                {
                    permissionsDict[resource] = (read, create, update, delete);
                }
            }

            await _apiTokenService.SetPermissionsAsync(id, permissionsDict);
            TempData["AdminMessage"] = "Permissions updated successfully.";
        }
        catch (Exception)
        {
            TempData["AdminError"] = "An error occurred while saving permissions.";
        }

        return RedirectToAction(nameof(ApiTokenDetail), new { id });
    }

    [HttpPost("api-tokens/{id:int}/recycle")]
    [ValidateAntiForgeryToken]
    [RequireSuperAdmin]
    public async Task<IActionResult> ApiTokenRecycle(int id, [FromServices] IApiTokenPortalService portal)
    {
        try
        {
            var token = await _apiTokenService.GetByIdAsync(id);
            if (token == null)
            {
                TempData["AdminError"] = "API token not found.";
                return RedirectToAction(nameof(Index), new { panel = "api-tokens" });
            }

            var newToken = await _apiTokenService.RecycleTokenAsync(id);
            await portal.NotifyTokenRecycledAsync(id, newToken);
            TempData["AdminMessage"] = "API token recycled successfully. Copy the new token now — you won't be able to see it again. The owner has been emailed.";
            TempData["NewToken"] = newToken;
        }
        catch (Exception)
        {
            TempData["AdminError"] = "An error occurred while recycling the token.";
        }

        return RedirectToAction(nameof(ApiTokenDetail), new { id });
    }

    [HttpPost("api-tokens/{id:int}/toggle")]
    [ValidateAntiForgeryToken]
    [RequireSuperAdmin]
    public async Task<IActionResult> ApiTokenToggle(int id)
    {
        try
        {
            var token = await _apiTokenService.GetByIdAsync(id);
            if (token != null)
            {
                var newStatus = !token.IsActive;
                await _apiTokenService.UpdateTokenStatusAsync(id, newStatus);
                TempData["AdminMessage"] = $"API token {(newStatus ? "activated" : "suspended")} successfully.";
            }
        }
        catch (Exception)
        {
            TempData["AdminError"] = "An error occurred while updating the token status.";
        }

        return RedirectToAction(nameof(ApiTokenDetail), new { id });
    }

    [HttpGet("api-logs")]
    [RequireSuperAdmin]
    public async Task<IActionResult> ApiLogs(int? tokenId = null)
    {
        var query = _context.ApiRequestLogs
            .Include(l => l.ApiToken)
            .OrderByDescending(l => l.RequestTimestamp)
            .AsQueryable();

        if (tokenId.HasValue)
            query = query.Where(l => l.ApiTokenId == tokenId.Value);

        var logs = await query.Take(1000).ToListAsync();
        ViewBag.Tokens = await _apiTokenService.GetAllTokensAsync();
        ViewBag.SelectedTokenId = tokenId;
        SetAdminChrome("admin-index");
        return View("~/Views/Modern/Admin/ApiLogs.cshtml", logs);
    }

    [HttpPost("api-tokens/{id:int}/delete")]
    [ValidateAntiForgeryToken]
    [RequireSuperAdmin]
    public async Task<IActionResult> ApiTokenDelete(int id)
    {
        try
        {
            await _apiTokenService.DeleteTokenAsync(id);
            TempData["AdminMessage"] = "API token deleted successfully.";
            return RedirectToAction(nameof(Index), new { panel = "api-tokens" });
        }
        catch (Exception)
        {
            TempData["AdminError"] = "An error occurred while deleting the token.";
            return RedirectToAction(nameof(ApiTokenDetail), new { id });
        }
    }

    [HttpPost("api-tokens/{id:int}/update-owner")]
    [ValidateAntiForgeryToken]
    [RequireSuperAdmin]
    public async Task<IActionResult> ApiTokenUpdateOwner(int id, string? ownerEmail)
    {
        var token = await _context.ApiTokens.FindAsync(id);
        if (token == null)
        {
            TempData["AdminError"] = "API token not found.";
            return RedirectToAction(nameof(Index), new { panel = "api-tokens" });
        }

        var trimmed = ownerEmail?.Trim();
        if (!string.IsNullOrEmpty(trimmed) &&
            !trimmed.Contains('@', StringComparison.Ordinal))
        {
            TempData["AdminError"] = "Enter a valid email address for the token owner.";
            return RedirectToAction(nameof(ApiTokenDetail), new { id });
        }

        token.OwnerEmail = string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
        await _context.SaveChangesAsync();

        TempData["AdminMessage"] = string.IsNullOrWhiteSpace(trimmed)
            ? "Token owner removed."
            : $"Token owner set to {trimmed}.";
        return RedirectToAction(nameof(ApiTokenDetail), new { id });
    }

    [HttpPost("cms-access-products/add")]
    [ValidateAntiForgeryToken]
    [RequireSuperAdmin]
    public async Task<IActionResult> CmsAccessProductAdd(string name, string signInPageUrl, int sortOrder = 0)
    {
        name = name?.Trim() ?? "";
        signInPageUrl = signInPageUrl?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(signInPageUrl))
        {
            TempData["AdminError"] = "CMS product name and sign-in URL are required.";
            return RedirectToAction(nameof(Index), new { panel = "cms-access-products" });
        }

        var exists = await _context.CmsAccessRequestProducts
            .AnyAsync(p => p.Name.ToLower() == name.ToLower());
        if (exists)
        {
            TempData["AdminError"] = "A product with that name already exists.";
            return RedirectToAction(nameof(Index), new { panel = "cms-access-products" });
        }

        var now = DateTime.UtcNow;
        _context.CmsAccessRequestProducts.Add(new CmsAccessRequestProduct
        {
            Name = name,
            SignInPageUrl = signInPageUrl,
            SortOrder = sortOrder,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        });
        await _context.SaveChangesAsync();
        TempData["AdminMessage"] = "CMS access product added.";
        return RedirectToAction(nameof(Index), new { panel = "cms-access-products" });
    }

    [HttpGet("cms-access-products/{id:int}/edit")]
    [RequireSuperAdmin]
    public async Task<IActionResult> CmsAccessProductEdit(int id)
    {
        var row = await _context.CmsAccessRequestProducts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (row == null)
        {
            TempData["AdminError"] = "Product not found.";
            return RedirectToAction(nameof(Index), new { panel = "cms-access-products" });
        }

        SetAdminChrome("admin-index");
        ViewBag.Product = row;
        if (TempData["AdminError"] is string editErr)
            ViewBag.AdminError = editErr;
        return View("~/Views/Modern/Admin/CmsAccessProductEdit.cshtml");
    }

    [HttpPost("cms-access-products/{id:int}/save")]
    [ValidateAntiForgeryToken]
    [RequireSuperAdmin]
    public async Task<IActionResult> CmsAccessProductSave(int id, string name, string signInPageUrl, int sortOrder, bool isActive)
    {
        name = name?.Trim() ?? "";
        signInPageUrl = signInPageUrl?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(signInPageUrl))
        {
            TempData["AdminError"] = "CMS product name and sign-in URL are required.";
            return RedirectToAction(nameof(CmsAccessProductEdit), new { id });
        }

        var entity = await _context.CmsAccessRequestProducts.FirstOrDefaultAsync(x => x.Id == id);
        if (entity == null)
        {
            TempData["AdminError"] = "Product not found.";
            return RedirectToAction(nameof(Index), new { panel = "cms-access-products" });
        }

        var dup = await _context.CmsAccessRequestProducts
            .AnyAsync(p => p.Id != id && p.Name.ToLower() == name.ToLower());
        if (dup)
        {
            TempData["AdminError"] = "Another product already uses that name.";
            return RedirectToAction(nameof(CmsAccessProductEdit), new { id });
        }

        entity.Name = name;
        entity.SignInPageUrl = signInPageUrl;
        entity.SortOrder = sortOrder;
        entity.IsActive = isActive;
        entity.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        TempData["AdminMessage"] = "CMS access product saved.";
        return RedirectToAction(nameof(Index), new { panel = "cms-access-products" });
    }

    [HttpPost("cms-access-products/{id:int}/delete")]
    [ValidateAntiForgeryToken]
    [RequireSuperAdmin]
    public async Task<IActionResult> CmsAccessProductDelete(int id)
    {
        var entity = await _context.CmsAccessRequestProducts.FirstOrDefaultAsync(x => x.Id == id);
        if (entity == null)
        {
            TempData["AdminError"] = "Product not found.";
            return RedirectToAction(nameof(Index), new { panel = "cms-access-products" });
        }

        _context.CmsAccessRequestProducts.Remove(entity);
        await _context.SaveChangesAsync();
        TempData["AdminMessage"] = "CMS access product removed.";
        return RedirectToAction(nameof(Index), new { panel = "cms-access-products" });
    }

}
