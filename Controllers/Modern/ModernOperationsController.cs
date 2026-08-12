using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Compass.Attributes;
using Compass.Data;
using Compass.Helpers;
using Compass.Models;
using Compass.Models.Modern.Work;
using Compass.Models.DemandPipeline;
using Compass.Models.Fips;
using Compass.Services;
using Compass.Services.Modern;
using Compass.Services.Fips;
using Compass.Services.Raid;
using Compass.ViewModels.Modern;
using Compass.ViewModels.Modern.Ddr;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Compass.Controllers.Modern;

/// <summary>Operations UI at <c>/modern/operations/*</c> — restricted to Super admin, Central Operations Admin, or Admin group.</summary>
[Authorize]
[RequireOperationConsoleUser]
[Route("modern/operations")]
public class ModernOperationsController : Controller
{
    private static readonly JsonSerializerOptions CmdbSyncSseJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly CompassDbContext _db;
    private readonly IFipsCmdbProductSyncService _fipsCmdbProductSync;
    private readonly IFipsProductWriteService _fipsProductWrite;
    private readonly ILogger<ModernOperationsController> _logger;
    private readonly IGlobalFeatureToggleService _globalFeatureToggle;
    private readonly IModernWorkService _modernWork;
    private readonly IProductsApiService _productsApi;
    private readonly IFipsBusinessAreaLookupSyncService _fipsBusinessAreaLookupSync;
    private readonly IFipsDirectorateLookupSyncService _fipsDirectorateLookupSync;
    private readonly IFipsCompletionBulkImportService _fipsCompletionBulkImport;
    private readonly IFipsStrapiLegacyImportService _fipsStrapiLegacyImport;
    private readonly IOperationsRiskEditService _operationsRiskEdit;
    private readonly INotificationService _notificationService;
    private readonly IConfiguration _configuration;
    private readonly CommissionReportingAnalyticsService _commissionReportingAnalytics;

    public ModernOperationsController(
        CompassDbContext db,
        IFipsCmdbProductSyncService fipsCmdbProductSync,
        IFipsProductWriteService fipsProductWrite,
        ILogger<ModernOperationsController> logger,
        IGlobalFeatureToggleService globalFeatureToggle,
        IModernWorkService modernWork,
        IProductsApiService productsApi,
        IFipsBusinessAreaLookupSyncService fipsBusinessAreaLookupSync,
        IFipsDirectorateLookupSyncService fipsDirectorateLookupSync,
        IFipsCompletionBulkImportService fipsCompletionBulkImport,
        IFipsStrapiLegacyImportService fipsStrapiLegacyImport,
        IOperationsRiskEditService operationsRiskEdit,
        INotificationService notificationService,
        IConfiguration configuration,
        CommissionReportingAnalyticsService commissionReportingAnalytics)
    {
        _db = db;
        _fipsCmdbProductSync = fipsCmdbProductSync;
        _fipsProductWrite = fipsProductWrite;
        _logger = logger;
        _globalFeatureToggle = globalFeatureToggle;
        _modernWork = modernWork;
        _productsApi = productsApi;
        _fipsBusinessAreaLookupSync = fipsBusinessAreaLookupSync;
        _fipsDirectorateLookupSync = fipsDirectorateLookupSync;
        _fipsCompletionBulkImport = fipsCompletionBulkImport;
        _fipsStrapiLegacyImport = fipsStrapiLegacyImport;
        _operationsRiskEdit = operationsRiskEdit;
        _notificationService = notificationService;
        _configuration = configuration;
        _commissionReportingAnalytics = commissionReportingAnalytics;
    }

    private async Task<IActionResult?> DemandDisabledRedirectAsync()
    {
        if (!await _globalFeatureToggle.IsFeatureEnabledForPrincipalAsync(FeatureCodes.Demand, User))
            return RedirectToAction("Index", "ModernDashboard");
        return null;
    }

    private async Task<IActionResult?> FipsDatabaseDisabledRedirectAsync()
    {
        if (!await _globalFeatureToggle.IsFeatureEnabledForPrincipalAsync(FeatureCodes.Fips, User))
        {
            TempData["ErrorMessage"] =
                "The service register is turned off. FIPS product data is read from the CMS when this feature is off.";
            return RedirectToAction(nameof(Dashboard));
        }

        return null;
    }

    private static string NormalizeServiceRegisterProductTab(string? tab) =>
        tab switch
        {
            "history" => "history",
            "cmdb" => "cmdb",
            "risks" => "risks",
            "issues" => "issues",
            _ => "information"
        };

    private static bool TryParseBulkEnterpriseAction(string? action, out bool isEnterprise)
    {
        if (string.Equals(action, "true", StringComparison.OrdinalIgnoreCase))
        {
            isEnterprise = true;
            return true;
        }

        if (string.Equals(action, "false", StringComparison.OrdinalIgnoreCase))
        {
            isEnterprise = false;
            return true;
        }

        isEnterprise = false;
        return false;
    }

    private static string? FormatCmdbSnapshotJson(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return raw;
        }
    }

    private string CurrentUserEmail =>
        User.Identity?.Name
        ?? User.FindFirst(ClaimTypes.Email)?.Value
        ?? User.FindFirst("preferred_username")?.Value
        ?? "";

    private void SetNav(string subNavItem)
    {
        ViewBag.MainNavSection = "operations";
        ViewBag.SubNavItem = subNavItem;
    }

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

    [HttpGet("")]
    public IActionResult Index() => RedirectToAction(nameof(Dashboard));

    [HttpGet("dashboard")]
    public async Task<IActionResult> Dashboard(CancellationToken ct)
    {
        SetNav("operations-dashboard");

        var raid = await RaidEscalationManagementViewModelBuilder.BuildAsync(_db, "escalations", ct);

        var raidEnabled = await _globalFeatureToggle.IsFeatureEnabledForPrincipalAsync(FeatureCodes.Raid, User);

        var pendingDdrInsight = 0;
        if (await _globalFeatureToggle.IsFeatureEnabledForPrincipalAsync(FeatureCodes.Ddr, User))
        {
            pendingDdrInsight = await _db.DesignDecisionRecords.AsNoTracking()
                .CountAsync(r => r.DeletedAt == null && r.SubmittedAt != null && !r.InsightClassifications.Any(), ct);
        }

        var cmsNew = await _db.CmsAccessRequests.AsNoTracking().CountAsync(x => x.Status == "New", ct);
        var cmsCompleted = await _db.CmsAccessRequests.AsNoTracking().CountAsync(x => x.Status == "Completed", ct);
        var cmsRejected = await _db.CmsAccessRequests.AsNoTracking().CountAsync(x => x.Status == "Rejected", ct);

        var attentionTotal = cmsNew + pendingDdrInsight + (raidEnabled ? raid.PendingApprovalCount : 0);

        var activeWorkCount = await _db.Projects.AsNoTracking()
            .CountAsync(p => p.Status == "Active", ct);

        var demandEnabled = await _globalFeatureToggle.IsFeatureEnabledForPrincipalAsync(FeatureCodes.Demand, User);
        var demandTotal = 0;
        var triageUpcoming = 0;
        if (demandEnabled)
        {
            demandTotal = await _db.DemandPipelineRequests.AsNoTracking().CountAsync(ct);
            var today = DateTime.UtcNow.Date;
            triageUpcoming = await _db.DemandPipelineTriageMeetings.AsNoTracking()
                .CountAsync(m =>
                    m.Status == "Scheduled" &&
                    (m.MeetingDate == null || m.MeetingDate.Value.Date >= today), ct);
        }

        var fipsEnabled = await _globalFeatureToggle.IsFeatureEnabledForPrincipalAsync(FeatureCodes.Fips, User);
        var serviceRegisterActive = 0;
        if (fipsEnabled)
        {
            serviceRegisterActive = await _db.CMDBProducts.AsNoTracking()
                .CountAsync(p => p.Status == CMDBProductStatus.Active && !p.IsEnterpriseService, ct);
        }

        var activeCommissions = await _db.Commissions.AsNoTracking()
            .CountAsync(c => c.IsActive, ct);

        var vm = new ModernOperationsDashboardViewModel
        {
            AttentionQueueTotal = attentionTotal,
            PendingTierChangeCount = raid.PendingApprovalCount,
            PendingEscalationsCount = raid.PendingEscalationsCount,
            PendingDeescalationsCount = raid.PendingDeescalationsCount,
            CurrentlyEscalatedCount = raid.CurrentlyEscalatedCount,
            ActiveRisksCount = raid.ActiveRisksCount,
            PendingDdrDesignOpsInsightCount = pendingDdrInsight,
            CmsAccessRequestsNewCount = cmsNew,
            CmsAccessRequestsCompletedCount = cmsCompleted,
            CmsAccessRequestsRejectedCount = cmsRejected,
            ManageWorkActiveCount = activeWorkCount,
            ManageDemandTotalCount = demandTotal,
            ManageTriageUpcomingMeetingsCount = triageUpcoming,
            ManagePerformanceActiveCommissionsCount = activeCommissions,
            ServiceRegisterActiveProductCount = serviceRegisterActive,
        };
        return View("~/Views/Modern/Operations/OperationsDashboard.cshtml", vm);
    }

    /// <summary>DDR overview for Central Operations — queues, breakdowns, and links into the DDR register and detail views.</summary>
    [HttpGet("ddr")]
    public async Task<IActionResult> Ddr(CancellationToken ct)
    {
        if (!await _globalFeatureToggle.IsFeatureEnabledForPrincipalAsync(FeatureCodes.Ddr, User))
        {
            TempData["ErrorMessage"] = "Design Decision Records are not enabled.";
            return RedirectToAction(nameof(Dashboard));
        }

        SetNav("operations-ddr");
        var model = await BuildOperationsDdrDashboardAsync(ct);
        return View("~/Views/Modern/Operations/DdrDashboard.cshtml", model);
    }

    private async Task<ModernOperationsDdrDashboardViewModel> BuildOperationsDdrDashboardAsync(CancellationToken ct)
    {
        var baseQ = _db.DesignDecisionRecords.AsNoTracking()
            .Where(r => r.DeletedAt == null);

        var total = await baseQ.CountAsync(ct);
        var pendingInsight = await baseQ.CountAsync(
            r => r.SubmittedAt != null && !r.InsightClassifications.Any(),
            ct);
        var submittedTotal = await baseQ.CountAsync(r => r.SubmittedAt != null, ct);
        var retrospectiveCount = await baseQ.CountAsync(r => r.RetrospectiveRecord, ct);

        var preview = await baseQ
            .Where(r => r.SubmittedAt != null && !r.InsightClassifications.Any())
            .OrderByDescending(r => r.SubmittedAt)
            .Take(20)
            .Select(r => new { r.Reference, r.ShortTitle, r.SubmittedAt })
            .ToListAsync(ct);

        var previewRows = preview.Select(r => new DdrOpsQueueRow
        {
            Reference = r.Reference,
            ShortTitle = r.ShortTitle,
            SubmittedAt = r.SubmittedAt,
            DetailUrl = Url.Action("Detail", "ModernDesignDecisionRecords", new { reference = r.Reference }) ?? "#",
        }).ToList();

        var byCategoryRaw = await baseQ
            .GroupBy(r => r.Category)
            .Select(g => new { Key = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        var byCategory = byCategoryRaw
            .OrderByDescending(x => x.Count)
            .Select(x => new DdrDashboardBreakdownRow
            {
                Label = x.Key ?? "(unknown)",
                Count = x.Count,
                RegisterUrl = Url.Action("Register", "ModernDesignDecisionRecords", new { category = x.Key }) ?? "#",
            })
            .ToList();

        var typedDeviations = await baseQ
            .Where(r => r.DeviationFlag && r.DeviationType != null && r.DeviationType != "")
            .GroupBy(r => r.DeviationType!)
            .Select(g => new { Key = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var unsetDeviation = await baseQ.CountAsync(
            r => r.DeviationFlag && (r.DeviationType == null || r.DeviationType == ""),
            ct);

        var byDeviation = typedDeviations.Select(x => new DdrDashboardBreakdownRow
        {
            Label = x.Key,
            Count = x.Count,
            RegisterUrl = Url.Action("Register", "ModernDesignDecisionRecords",
                new { deviation = "true", deviationType = x.Key }) ?? "#",
        }).ToList();

        if (unsetDeviation > 0)
        {
            byDeviation.Add(new DdrDashboardBreakdownRow
            {
                Label = "Type not set",
                Count = unsetDeviation,
                RegisterUrl = Url.Action("Register", "ModernDesignDecisionRecords",
                    new { deviation = "true", deviationType = DdrRegisterQueryValues.UnsetDeviationType }) ?? "#",
            });
        }

        byDeviation.Sort((a, b) => b.Count.CompareTo(a.Count));

        var productGroups = await (
                from pl in _db.DdrProductLinks.AsNoTracking()
                join r in _db.DesignDecisionRecords.AsNoTracking() on pl.DesignDecisionRecordId equals r.Id
                where r.DeletedAt == null
                group pl by pl.FipsProductId
                into g
                select new { ProductId = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .Take(40)
            .ToListAsync(ct);

        var pIds = productGroups.Select(x => x.ProductId).ToList();
        var productTitles = await _db.CMDBProducts.AsNoTracking()
            .Where(p => pIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Title })
            .ToDictionaryAsync(x => x.Id, x => x.Title, ct);

        var byProduct = productGroups.Select(pg =>
        {
            var title = productTitles.TryGetValue(pg.ProductId, out var t) && !string.IsNullOrWhiteSpace(t)
                ? t!
                : "(unknown product)";
            return new DdrDashboardBreakdownRow
            {
                Label = title,
                Count = pg.Count,
                RegisterUrl = Url.Action("Register", "ModernDesignDecisionRecords", new { productId = pg.ProductId }) ?? "#",
            };
        }).ToList();

        return new ModernOperationsDdrDashboardViewModel
        {
            TotalDdrs = total,
            PendingDesignOpsInsightCount = pendingInsight,
            SubmittedTotalCount = submittedTotal,
            RetrospectiveCount = retrospectiveCount,
            PendingInsightPreview = previewRows,
            ByCategory = byCategory,
            ByDeviationType = byDeviation,
            ByProduct = byProduct,
            OversightPendingInsightUrl = Url.Action("Oversight", "ModernDesignDecisionRecords",
                new { filter = "pending-insight" }) ?? "#",
            OversightDashboardUrl = Url.Action("Oversight", "ModernDesignDecisionRecords") ?? "#",
            RegisterUrl = Url.Action("Register", "ModernDesignDecisionRecords") ?? "#",
            RegisterRetrospectiveUrl = Url.Action("Register", "ModernDesignDecisionRecords",
                new { retrospective = "true" }) ?? "#",
        };
    }

    private static string NormalizeCmsAccessRequestTab(string? tab) =>
        (tab ?? "new").Trim().ToLowerInvariant() switch
        {
            "completed" => "completed",
            "rejected" => "rejected",
            _ => "new"
        };

    [HttpGet("cms-requests")]
    public async Task<IActionResult> CmsRequests(string? tab, CancellationToken ct)
    {
        SetNav("operations-cms-requests");

        var t = NormalizeCmsAccessRequestTab(tab);

        var newCount = await _db.CmsAccessRequests.AsNoTracking().CountAsync(x => x.Status == "New", ct);
        var completedCount = await _db.CmsAccessRequests.AsNoTracking().CountAsync(x => x.Status == "Completed", ct);
        var rejectedCount = await _db.CmsAccessRequests.AsNoTracking().CountAsync(x => x.Status == "Rejected", ct);

        var filtered = _db.CmsAccessRequests.AsNoTracking().AsQueryable();
        filtered = t switch
        {
            "completed" => filtered.Where(x => x.Status == "Completed"),
            "rejected" => filtered.Where(x => x.Status == "Rejected"),
            _ => filtered.Where(x => x.Status == "New")
        };

        var rows = await filtered
            .OrderByDescending(x => x.DateRequested)
            .Select(x => new CmsAccessRequestRowViewModel
            {
                Id = x.Id,
                RequestorDisplayName = (x.RequestorFirstName + " " + x.RequestorLastName).Trim(),
                CmsName = x.CmsName,
                DateRequested = x.DateRequested,
                Status = x.Status,
                Outcome = x.Outcome
            })
            .ToListAsync(ct);

        var vm = new CmsAccessRequestListViewModel
        {
            ActiveTab = t,
            NewCount = newCount,
            CompletedCount = completedCount,
            RejectedCount = rejectedCount,
            Rows = rows
        };

        return View("~/Views/Modern/Operations/CmsAccessRequests.cshtml", vm);
    }

    [HttpGet("cms-requests/{id:int}")]
    public async Task<IActionResult> CmsRequestDetail(int id, string? returnTab, CancellationToken ct)
    {
        SetNav("operations-cms-requests");

        var row = await _db.CmsAccessRequests.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (row == null)
            return NotFound();

        var displayName = $"{row.RequestorFirstName} {row.RequestorLastName}".Trim();
        var vm = new CmsAccessRequestDetailViewModel
        {
            Id = row.Id,
            CmsName = row.CmsName,
            SignInPageUrl = row.SignInPageUrl,
            RequestorEmail = row.RequestorEmail,
            RequestorFirstName = row.RequestorFirstName,
            RequestorLastName = row.RequestorLastName,
            RequestorDisplayName = string.IsNullOrEmpty(displayName) ? row.RequestorEmail : displayName,
            DateRequested = row.DateRequested,
            PublisherAccessRequired = row.PublisherAccessRequired,
            Comments = row.Comments,
            Status = row.Status,
            Outcome = row.Outcome,
            RegistrationToken = row.RegistrationToken,
            CanProcess = string.Equals(row.Status, "New", StringComparison.OrdinalIgnoreCase),
            ReturnTab = NormalizeCmsAccessRequestTab(returnTab)
        };

        return View("~/Views/Modern/Operations/CmsAccessRequestDetail.cshtml", vm);
    }

    [HttpPost("cms-requests/{id:int}/process")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CmsRequestProcess(
        int id,
        [FromForm] CmsAccessRequestProcessForm form,
        string? returnTab,
        CancellationToken ct)
    {
        SetNav("operations-cms-requests");

        var listTab = NormalizeCmsAccessRequestTab(returnTab);
        var entity = await _db.CmsAccessRequests.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (entity == null)
            return NotFound();

        if (!string.Equals(entity.Status, "New", StringComparison.OrdinalIgnoreCase))
        {
            TempData["ErrorMessage"] = "This request has already been processed.";
            return RedirectToAction(nameof(CmsRequests), new { tab = listTab });
        }

        var outcomeKey = form.Outcome?.Trim();
        if (!string.Equals(outcomeKey, "Granted", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(outcomeKey, "Rejected", StringComparison.OrdinalIgnoreCase))
        {
            ModelState.AddModelError(nameof(form.Outcome), "Select granted or rejected.");
        }

        var tokenTrimmed = form.RegistrationToken?.Trim();
        if (string.Equals(outcomeKey, "Granted", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(tokenTrimmed))
        {
            ModelState.AddModelError(nameof(form.RegistrationToken), "Enter the registration link or token for a granted request.");
        }

        var actorId = await ResolveCurrentUserIdForOperationsAsync(ct);
        if (!actorId.HasValue)
        {
            ModelState.AddModelError("", "Your signed-in user could not be resolved.");
        }

        if (!ModelState.IsValid)
        {
            var displayName = $"{entity.RequestorFirstName} {entity.RequestorLastName}".Trim();
            var vm = new CmsAccessRequestDetailViewModel
            {
                Id = entity.Id,
                CmsName = entity.CmsName,
                SignInPageUrl = entity.SignInPageUrl,
                RequestorEmail = entity.RequestorEmail,
                RequestorFirstName = entity.RequestorFirstName,
                RequestorLastName = entity.RequestorLastName,
                RequestorDisplayName = string.IsNullOrEmpty(displayName) ? entity.RequestorEmail : displayName,
                DateRequested = entity.DateRequested,
                PublisherAccessRequired = entity.PublisherAccessRequired,
                Comments = entity.Comments,
                Status = entity.Status,
                Outcome = entity.Outcome,
                RegistrationToken = entity.RegistrationToken,
                CanProcess = true,
                ReturnTab = listTab,
                DraftOutcome = form.Outcome,
                DraftRegistrationToken = form.RegistrationToken
            };
            return View("~/Views/Modern/Operations/CmsAccessRequestDetail.cshtml", vm);
        }

        var outcomeStored = string.Equals(outcomeKey, "Granted", StringComparison.OrdinalIgnoreCase) ? "Granted" : "Rejected";
        entity.Outcome = outcomeStored;

        if (outcomeStored == "Granted")
        {
            var linkText = tokenTrimmed!;
            var subject = $"Access to {entity.CmsName} granted";
            var body =
                "You will need to set up a password to access the CMS, use this link to set your password:\n\n"
                + linkText
                + "\n\nIf you have any problems, reply to this email, or contact design.ops@education.gov.uk";

            var send = await _notificationService.SendEmailAsync(
                entity.RequestorEmail,
                subject,
                body,
                triggerCode: "cms_access_request",
                cancellationToken: ct);

            if (!send.Success)
            {
                _logger.LogWarning(
                    "CMS access granted email failed for request {Id}: {Error}",
                    id,
                    send.ErrorMessage);
                TempData["ErrorMessage"] = send.ErrorMessage != null
                    ? $"The email could not be sent: {send.ErrorMessage}"
                    : "The email could not be sent.";
                return RedirectToAction(nameof(CmsRequestDetail), new { id, returnTab = listTab });
            }

            entity.Status = "Completed";
            entity.RegistrationToken = linkText;
        }
        else
        {
            entity.Status = "Rejected";
            if (!string.IsNullOrWhiteSpace(tokenTrimmed))
                entity.RegistrationToken = tokenTrimmed;
        }

        entity.ActionedByUserId = actorId;
        await _db.SaveChangesAsync(ct);

        TempData["SuccessMessage"] = outcomeStored == "Granted"
            ? "Access granted and the requester has been emailed."
            : "The request has been rejected.";
        var destinationTab = outcomeStored == "Granted" ? "completed" : "rejected";
        return RedirectToAction(nameof(CmsRequests), new { tab = destinationTab });
    }

    [HttpPost("cms-requests/{id:int}/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CmsRequestDelete(int id, CancellationToken ct)
    {
        SetNav("operations-cms-requests");

        var entity = await _db.CmsAccessRequests.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (entity == null)
            return NotFound();

        var destinationTab = entity.Status switch
        {
            "Completed" => "completed",
            "Rejected" => "rejected",
            _ => "new"
        };

        _db.CmsAccessRequests.Remove(entity);
        await _db.SaveChangesAsync(ct);

        TempData["SuccessMessage"] = "The CMS access request has been deleted.";
        return RedirectToAction(nameof(CmsRequests), new { tab = destinationTab });
    }

    [HttpGet("accessibility")]
    public IActionResult Accessibility() =>
        RedirectToActionPermanent(nameof(Accessibility), "ModernReporting");

    [HttpGet("manage-work")]
    [HttpGet("~/ModernOperations/ManageWork")]
    public async Task<IActionResult> ManageWork(
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
        SetNav("operations-manage-work");

        var userEmail = User.Identity?.Name;
        if (string.IsNullOrEmpty(userEmail))
            return Unauthorized();

        var currentUser = await _db.Users.FirstOrDefaultAsync(u => u.Email.ToLower() == userEmail.ToLower(), cancellationToken);
        if (currentUser == null)
            return Unauthorized();

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
            vm, Url, nameof(ManageWork), "ModernOperations", activeTab);

        var manageWorkUrl = Url.Action(nameof(ManageWork), "ModernOperations") ?? "/modern/operations/manage-work";
        ViewBag.SearchAndFilter = new SearchAndFilterViewModel
        {
            IdPrefix = "ops-work",
            SearchPlaceholder = "Search work item titles…",
            SearchValue = search,
            FormActionUrl = Url.Action(nameof(ManageWork), "ModernOperations", new { tab = activeTab, page = 1 }) ?? manageWorkUrl,
            FormMethod = "get",
            ClearUrl = Url.Action(nameof(ManageWork), "ModernOperations", new { tab = activeTab }) ?? manageWorkUrl,
            ActiveChips = vm.ActiveFilterChips,
            Fields = new List<SearchAndFilterFieldViewModel>()
        };
        ViewBag.ActiveTab = activeTab;
        ViewBag.AllWorkActiveTab = activeTab;
        ViewBag.OpsManageWork = true;

        return View("~/Views/Modern/Work/AllWork.cshtml", vm);
    }

    [HttpGet("manage-demand")]
    public async Task<IActionResult> ManageDemand(string? search, string? status, string? department, CancellationToken ct)
    {
        var blocked = await DemandDisabledRedirectAsync();
        if (blocked != null) return blocked;

        SetNav("operations-manage-demand");

        var all = await _db.DemandPipelineRequests.AsNoTracking()
            .OrderByDescending(d => d.UpdatedAt).ThenByDescending(d => d.CreatedAt)
            .ToListAsync(ct);

        var filtered = all.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            filtered = filtered.Where(d =>
                (!string.IsNullOrEmpty(d.Title) && d.Title.Contains(term, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrEmpty(d.Reference) && d.Reference.Contains(term, StringComparison.OrdinalIgnoreCase)));
        }
        if (!string.IsNullOrWhiteSpace(status) && status != "all")
            filtered = filtered.Where(d => string.Equals(d.Status, status, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(department) && department != "all")
            filtered = filtered.Where(d => string.Equals(d.DepartmentGroup, department.Trim(), StringComparison.OrdinalIgnoreCase));

        var result = filtered.ToList();

        var statuses = all.Select(d => d.Status).Where(s => !string.IsNullOrEmpty(s)).Distinct().OrderBy(s => s).ToList();
        var departments = all.Select(d => d.DepartmentGroup).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().OrderBy(s => s!).ToList();

        var baseUrl = Url.Action(nameof(ManageDemand), "ModernOperations") ?? "/modern/operations/manage-demand";
        var sf = new SearchAndFilterViewModel
        {
            IdPrefix = "ops-demand",
            SearchPlaceholder = "Search by title or reference…",
            SearchValue = search,
            FormActionUrl = baseUrl,
            ClearUrl = baseUrl,
            Fields = new List<SearchAndFilterFieldViewModel>
            {
                new()
                {
                    Label = "Status", Name = "status",
                    SelectedValue = status,
                    Options = new[] { new SearchAndFilterOption { Value = "all", Text = "All statuses" } }
                        .Concat(statuses.Select(s => new SearchAndFilterOption { Value = s, Text = s }))
                        .ToList()
                },
                new()
                {
                    Label = "Department", Name = "department",
                    SelectedValue = department,
                    Options = new[] { new SearchAndFilterOption { Value = "all", Text = "All departments" } }
                        .Concat(departments.Select(d => new SearchAndFilterOption { Value = d!, Text = d! }))
                        .ToList()
                }
            }
        };
        sf.ActiveChips = SearchAndFilterActiveChipsBuilder.FromViewModel(
            sf, Url, nameof(ManageDemand), "ModernOperations", null);
        ViewBag.SearchAndFilter = sf;

        return View("~/Views/Modern/Operations/ManageDemand.cshtml", result);
    }

    [HttpPost("manage-demand/{id:guid}/update-status")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ManageDemandUpdateStatus(Guid id, string newStatus, string? search, string? status, string? department)
    {
        var blocked = await DemandDisabledRedirectAsync();
        if (blocked != null) return blocked;

        var demand = await _db.DemandPipelineRequests.FirstOrDefaultAsync(d => d.Id == id);
        if (demand == null) return NotFound();

        demand.Status = newStatus;
        demand.UpdatedAt = DateTime.UtcNow;
        demand.UpdatedBy = User.Identity?.Name;
        await _db.SaveChangesAsync();

        TempData["Success"] = $"Status for \"{demand.Title}\" updated to {newStatus}.";
        return RedirectToAction(nameof(ManageDemand), new { search, status, department });
    }

    [HttpGet("manage-performance")]
    public async Task<IActionResult> ManagePerformance(int? commissionId, CancellationToken cancellationToken = default)
    {
        SetNav("operations-manage-performance");

        try
        {
            var vm = await _commissionReportingAnalytics.BuildOperationsManagePerformanceAsync(
                commissionId, cancellationToken);
            return View("~/Views/Modern/Operations/ManagePerformance.cshtml", vm);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Operations manage performance: failed to build dashboard");
            TempData["Error"] = "Could not load performance data. Try again later.";
            return View("~/Views/Modern/Operations/ManagePerformance.cshtml",
                new OperationsManagePerformanceViewModel());
        }
    }

    [HttpGet("raid/escalations")]
    [ServiceFilter(typeof(Compass.Filters.RaidFeatureGateFilter))]
    public async Task<IActionResult> RaidEscalations(string? tab, CancellationToken ct)
    {
        SetNav("operations-raid");

        var vm = await RaidEscalationManagementViewModelBuilder.BuildAsync(_db, tab, ct);

        var email = CurrentUserEmail?.Trim();
        if (!string.IsNullOrWhiteSpace(email))
        {
            var permissions = HttpContext.RequestServices.GetRequiredService<IPermissionService>();
            ViewBag.CanActionTierChanges = await permissions.IsCentralOperationsAdminOrSuperAdminAsync(email);
        }
        else
        {
            ViewBag.CanActionTierChanges = false;
        }

        return View("~/Views/Modern/Operations/RaidEscalations.cshtml", vm);
    }

    [HttpPost("raid/soft-delete")]
    [ValidateAntiForgeryToken]
    [ServiceFilter(typeof(Compass.Filters.RaidFeatureGateFilter))]
    public async Task<IActionResult> RaidSoftDelete(
        [FromForm] string entityType,
        [FromForm] int entityId,
        CancellationToken ct)
    {
        var userEmail = CurrentUserEmail;
        if (entityType == "Risk")
        {
            var risk = await _db.Risks.FirstOrDefaultAsync(r => r.Id == entityId && !r.IsDeleted, ct);
            if (risk == null)
            {
                TempData["ErrorMessage"] = $"Risk R-{entityId:D4} not found or already deleted.";
                return RedirectToAction(nameof(RaidEscalations), new { tab = "active" });
            }
            risk.IsDeleted = true;
            risk.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            _logger.LogInformation("Risk {RiskId} soft-deleted by operations user {Email}", entityId, userEmail);
            TempData["SuccessMessage"] = $"Risk R-{entityId:D4} \"{risk.Title}\" has been deleted.";
        }
        else if (entityType == "Issue")
        {
            var issue = await _db.Issues.FirstOrDefaultAsync(i => i.Id == entityId && !i.IsDeleted, ct);
            if (issue == null)
            {
                TempData["ErrorMessage"] = $"Issue I-{entityId:D4} not found or already deleted.";
                return RedirectToAction(nameof(RaidEscalations), new { tab = "active" });
            }
            issue.IsDeleted = true;
            issue.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            _logger.LogInformation("Issue {IssueId} soft-deleted by operations user {Email}", entityId, userEmail);
            TempData["SuccessMessage"] = $"Issue I-{entityId:D4} \"{issue.Title}\" has been deleted.";
        }
        else
        {
            TempData["ErrorMessage"] = "Invalid entity type for deletion.";
        }

        return RedirectToAction(nameof(RaidEscalations), new { tab = "active" });
    }

    [HttpGet("raid/risks/{id:int}/edit")]
    [ServiceFilter(typeof(Compass.Filters.RaidFeatureGateFilter))]
    public async Task<IActionResult> RaidRiskOperationsEdit(int id, CancellationToken ct)
    {
        SetNav("operations-raid");
        var form = await _operationsRiskEdit.BuildFormAsync(id, ct);
        if (form == null)
            return NotFound();

        await _operationsRiskEdit.LoadEditorViewBagAsync(this, form, ct);
        var opsSummary = ViewBag.OperationsRiskSummary as OperationsRiskEditorSummaryVm;
        ViewBag.EditorTitle = opsSummary != null && !string.IsNullOrWhiteSpace(opsSummary.Reference)
            ? $"Edit {opsSummary.Reference} (Operations)"
            : "Edit risk (Operations)";
        ViewBag.OperationsRiskEdit = true;
        ViewBag.OperationsFormPostUrl = Url.Action(
            nameof(RaidRiskOperationsEditPost),
            "ModernOperations",
            new { id }) ?? $"/modern/operations/raid/risks/{id}/edit";
        ViewBag.OperationsListUrl = Url.Action(nameof(RaidEscalations), "ModernOperations", new { tab = "active" })
            ?? "/modern/operations/raid/escalations?tab=active";
        return View("~/Views/Modern/Raid/RiskEditor.cshtml", form);
    }

    [HttpPost("raid/risks/{id:int}/edit")]
    [ValidateAntiForgeryToken]
    [ServiceFilter(typeof(Compass.Filters.RaidFeatureGateFilter))]
    public async Task<IActionResult> RaidRiskOperationsEditPost(
        int id,
        [FromForm] ModernRaidRiskEditorForm form,
        [FromForm] string? operationsChangeReason,
        CancellationToken ct)
    {
        SetNav("operations-raid");
        form.Id = id;
        var editorId = await ResolveCurrentUserIdForOperationsAsync(ct);
        var ok = await _operationsRiskEdit.TrySaveAsync(
            id, form, operationsChangeReason, editorId, CurrentUserEmail, ModelState, ct);
        if (!ok)
        {
            await _operationsRiskEdit.LoadEditorViewBagAsync(this, form, ct);
            var opsSummary = ViewBag.OperationsRiskSummary as OperationsRiskEditorSummaryVm;
            ViewBag.EditorTitle = opsSummary != null && !string.IsNullOrWhiteSpace(opsSummary.Reference)
                ? $"Edit {opsSummary.Reference} (Operations)"
                : "Edit risk (Operations)";
            ViewBag.OperationsRiskEdit = true;
            ViewBag.OperationsFormPostUrl = Url.Action(
                nameof(RaidRiskOperationsEditPost),
                "ModernOperations",
                new { id }) ?? $"/modern/operations/raid/risks/{id}/edit";
            ViewBag.OperationsListUrl = Url.Action(nameof(RaidEscalations), "ModernOperations", new { tab = "active" })
                ?? "/modern/operations/raid/escalations?tab=active";
            ViewBag.OperationsChangeReasonDraft = operationsChangeReason ?? string.Empty;
            return View("~/Views/Modern/Raid/RiskEditor.cshtml", form);
        }

        TempData["Message"] = "Risk updated from Operations. The change reason is recorded in the risk notes.";
        return RedirectToAction(nameof(RaidEscalations), new { tab = "active" });
    }

    private async Task<int?> ResolveCurrentUserIdForOperationsAsync(CancellationToken ct)
    {
        var email = CurrentUserEmail;
        if (string.IsNullOrWhiteSpace(email))
            return null;
        var e = email.Trim().ToLowerInvariant();
        return await _db.Users.AsNoTracking()
            .Where(u => u.Email.ToLower() == e)
            .Select(u => (int?)u.Id)
            .FirstOrDefaultAsync(ct);
    }

    private static string NormaliseRaidEscalationListTab(string? t) =>
        (t ?? "escalations").Trim().ToLowerInvariant() switch
        {
            "deescalations" or "de-escalations" or "de" => "deescalations",
            "current" or "escalated" or "c" => "current",
            "active" or "a" or "all-risks" or "risks" => "active",
            _ => "escalations"
        };

    private static string InherentLabelFromScore(int score) =>
        score >= 20
            ? "Crisis / likely"
            : score >= 16
                ? "Critical / possible"
                : score >= 11
                    ? "High / possible"
                    : score >= 6
                        ? "Moderate / possible"
                        : "Low / unlikely";

    private static string UserDisplay(User? u) =>
        u == null
            ? "—"
            : (string.IsNullOrWhiteSpace(u.Name) ? (u.Email ?? "Unknown") : u.Name);

    private static int? InferFromTierIdForBackfill(
        RaidEscalationTierChangeRequest? lastApproved,
        RiskTier proposedTier,
        IReadOnlyList<RiskTier> activeTiers,
        bool assumeEscalation)
    {
        if (lastApproved?.FromRiskTierId is int fromId)
            return fromId;

        var list = activeTiers.Where(t => t.IsActive).ToList();
        var proposedLevel = RiskTierGovernance.ResolveLevel(proposedTier, list);
        var fromLevel = assumeEscalation ? proposedLevel + 1 : proposedLevel - 1;
        if (fromLevel < 1)
            fromLevel = 1;

        var operational = list
            .Where(t => !t.IsProposedTier)
            .FirstOrDefault(t => RiskTierGovernance.ResolveLevel(t, list) == fromLevel);

        operational ??= list
            .Where(t => !t.IsProposedTier)
            .OrderByDescending(t => RiskTierGovernance.ResolveLevel(t, list))
            .FirstOrDefault();

        return operational?.Id;
    }

    /// <summary>Ensure a pending tier-change request exists for a risk on a proposed tier, then open the review screen.</summary>
    [HttpGet("raid/escalations/action/risk/{riskId:int}")]
    [RequireCentralOpsAdmin]
    [ServiceFilter(typeof(Compass.Filters.RaidFeatureGateFilter))]
    public async Task<IActionResult> RaidEscalationActionForRisk(int riskId, string? returnTab, string? returnUrl, CancellationToken ct)
    {
        SetNav("operations-raid");
        var listTab = NormaliseRaidEscalationListTab(returnTab);

        var existingRequestId = await _db.RaidEscalationTierChangeRequests.AsNoTracking()
            .Where(x => x.RiskId == riskId
                && x.RecordType == "risk"
                && x.Status == "pending")
            .OrderByDescending(x => x.SubmittedAt)
            .Select(x => (int?)x.Id)
            .FirstOrDefaultAsync(ct);

        if (existingRequestId is int existingId)
        {
            return RedirectToAction(
                nameof(RaidEscalationAction),
                new { requestId = existingId, returnTab = listTab, returnUrl });
        }

        var risk = await _db.Risks
            .Include(r => r.RiskTier)
            .Include(r => r.OwnerUser)
            .FirstOrDefaultAsync(r => r.Id == riskId && !r.IsDeleted && r.ClosedDate == null, ct);

        if (risk?.RiskTier is not { IsProposedTier: true } || risk.RiskTierId is not int proposedTierId)
        {
            TempData["ErrorMessage"] = "This risk is not waiting on a proposed tier change.";
            return RedirectToAction(nameof(RaidEscalations), new { tab = listTab });
        }

        var activeTiers = await _db.RiskTiers.AsNoTracking()
            .Where(t => t.IsActive)
            .ToListAsync(ct);

        var lastApproved = await _db.RaidEscalationTierChangeRequests.AsNoTracking()
            .Where(x => x.RiskId == riskId && x.RecordType == "risk" && x.Status == "approved")
            .OrderByDescending(x => x.DecidedAt ?? x.SubmittedAt)
            .FirstOrDefaultAsync(ct);

        var assumeEscalation = listTab == "escalations";
        var fromTierId = InferFromTierIdForBackfill(lastApproved, risk.RiskTier, activeTiers, assumeEscalation);
        if (fromTierId == null)
        {
            TempData["ErrorMessage"] =
                "Could not determine the risk's previous tier. Check tier configuration in Admin → RAID.";
            return RedirectToAction(nameof(RaidEscalations), new { tab = listTab });
        }

        var req = new RaidEscalationTierChangeRequest
        {
            RecordType = "risk",
            RiskId = riskId,
            FromRiskTierId = fromTierId,
            ToRiskTierId = proposedTierId,
            Rationale = "Risk is on a proposed tier awaiting Operations review.",
            Status = "pending",
            SubmittedAt = risk.UpdatedAt,
            SubmittedByUserId = risk.OwnerUserId
        };

        _db.RaidEscalationTierChangeRequests.Add(req);
        await _db.SaveChangesAsync(ct);

        return RedirectToAction(
            nameof(RaidEscalationAction),
            new { requestId = req.Id, returnTab = listTab, returnUrl });
    }

    /// <summary>Primary URL for operations to approve/reject a tier request (<c>…/action/{id}</c>).</summary>
    [HttpGet("raid/escalations/action/{requestId:int}")]
    [RequireCentralOpsAdmin]
    [ServiceFilter(typeof(Compass.Filters.RaidFeatureGateFilter))]
    public Task<IActionResult> RaidEscalationAction(int requestId, string? returnTab, string? returnUrl, CancellationToken ct) =>
        RaidEscalationReview(requestId, returnTab, returnUrl, ct);

    /// <summary>Legacy URL — use <see cref="RaidEscalationAction" /> (…/action/{id}).</summary>
    [HttpGet("raid/escalations/manage/{requestId:int}")]
    [RequireCentralOpsAdmin]
    [ServiceFilter(typeof(Compass.Filters.RaidFeatureGateFilter))]
    public Task<IActionResult> ManageRaidEscalationRequest(int requestId, string? returnTab, string? returnUrl, CancellationToken ct) =>
        RaidEscalationReview(requestId, returnTab, returnUrl, ct);

    [HttpGet("raid/escalations/review/{requestId:int}")]
    [RequireCentralOpsAdmin]
    [ServiceFilter(typeof(Compass.Filters.RaidFeatureGateFilter))]
    public async Task<IActionResult> RaidEscalationReview(int requestId, string? returnTab, string? returnUrl, CancellationToken ct)
    {
        SetNav("operations-raid");

        var req = await _db.RaidEscalationTierChangeRequests
            .AsNoTracking()
            .Include(x => x.Risk!).ThenInclude(r => r.RiskStatus)
            .Include(x => x.Risk!).ThenInclude(r => r.RiskTier)
            .Include(x => x.FromRiskTier)
            .Include(x => x.ToRiskTier)
            .Include(x => x.SubmittedByUser)
            .FirstOrDefaultAsync(x => x.Id == requestId, ct);

        if (req == null
            || !string.Equals(req.RecordType, "risk", StringComparison.OrdinalIgnoreCase)
            || req.Risk == null
            || !req.RiskId.HasValue)
        {
            return NotFound();
        }

        if (!string.Equals(req.Status, "pending", StringComparison.OrdinalIgnoreCase))
        {
            TempData["ErrorMessage"] = "This request has already been decided.";
            return RedirectToAction(nameof(RaidEscalations), new { tab = NormaliseRaidEscalationListTab(returnTab) });
        }

        var activeTiers = await _db.RiskTiers.AsNoTracking()
            .Where(t => t.IsActive)
            .ToListAsync(ct);
        var toTier = req.ToRiskTier ?? activeTiers.FirstOrDefault(t => t.Id == req.ToRiskTierId);
        if (toTier == null)
        {
            TempData["ErrorMessage"] = "The requested target tier is no longer available.";
            return RedirectToAction(nameof(RaidEscalations), new { tab = NormaliseRaidEscalationListTab(returnTab) });
        }

        var listTab = NormaliseRaidEscalationListTab(returnTab);
        var listReturnPath = !string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)
            ? returnUrl
            : Url.Action(nameof(RaidEscalations), "ModernOperations", new { tab = listTab })
              ?? "/modern/operations/raid/escalations";
        var r = req.Risk;
        var statusLab = r.RiskStatus?.Label ?? r.Status ?? "—";

        // On submit, the risk row is moved to the *proposed* target tier so queues align (see RiskEscalationRequestPost).
        // "Current" for reviewers must be the pre-request band from the request snapshot, not r.RiskTier (which matches "to").
        var fromTierSnap = req.FromRiskTier ?? activeTiers.FirstOrDefault(t => t.Id == req.FromRiskTierId);
        var currentTierName = !string.IsNullOrWhiteSpace(fromTierSnap?.Name)
            ? fromTierSnap!.Name.Trim()
            : "—";

        var opTierRows = activeTiers
            .Where(t => !t.IsProposedTier)
            .OrderBy(t => t.SortOrder)
            .ThenBy(t => t.Id)
            .Select(t => new ModernRaidEscalationRejectTierOption(t.Id, t.Name, t.SortOrder))
            .ToList();

        int? rejectDefault = null;
        if (fromTierSnap is { } fromSnap)
        {
            if (!fromSnap.IsProposedTier && opTierRows.Any(o => o.Id == fromSnap.Id))
                rejectDefault = fromSnap.Id;
            else
            {
                var rFrom = RiskTierGovernance.ResolveOperationalTierMatchingGovernance(fromSnap, activeTiers);
                if (rFrom != null && opTierRows.Any(o => o.Id == rFrom.Id))
                    rejectDefault = rFrom.Id;
            }
        }

        if (rejectDefault == null && fromTierSnap is { } rejectFromSnap)
        {
            var resolved = RiskTierGovernance.ResolveOperationalTierMatchingGovernance(rejectFromSnap, activeTiers);
            if (resolved != null && opTierRows.Any(o => o.Id == resolved.Id))
                rejectDefault = resolved.Id;
        }

        if (rejectDefault == null && r.RiskTier is { IsProposedTier: false } && r.RiskTierId is int liveTierId && opTierRows.Any(o => o.Id == liveTierId))
            rejectDefault = liveTierId;

        if (rejectDefault == null && opTierRows.Count > 0)
            rejectDefault = opTierRows[0].Id;

        var defaultRejectName = rejectDefault is int defRid
            ? opTierRows.FirstOrDefault(o => o.Id == defRid)?.Name ?? "—"
            : "—";

        var fromTierForClass = req.FromRiskTier ?? activeTiers.FirstOrDefault(t => t.Id == req.FromRiskTierId);
        var toTierForClass = toTier;
        var isEscalation = fromTierForClass != null && toTierForClass != null
            && RiskTierGovernance.IsEscalation(fromTierForClass, toTierForClass, activeTiers);

        var vm = new ModernRaidEscalationReviewViewModel
        {
            RequestId = req.Id,
            RiskId = r.Id,
            Reference = $"R-{r.Id:D4}",
            Title = r.Title,
            RiskScore = r.RiskScore,
            InherentLabel = InherentLabelFromScore(r.RiskScore),
            StatusLabel = statusLab,
            CurrentRiskTierLabel = currentTierName,
            RequestedToTierLabel = toTier.Name,
            DefaultRejectTierName = defaultRejectName,
            Rationale = string.IsNullOrWhiteSpace(req.Rationale) ? null : req.Rationale.Trim(),
            SubmittedByDisplay = UserDisplay(req.SubmittedByUser),
            SubmittedAt = req.SubmittedAt,
            RiskDetailUrl = Url.Action("RiskDetail", "ModernRaid", new { id = r.Id }) ?? "#",
            ListReturnUrl = listReturnPath,
            ListReturnTab = listTab,
            IsEscalation = isEscalation,
            RejectTierOptions = opTierRows,
            RejectTierDefaultId = rejectDefault
        };

        return View("~/Views/Modern/Operations/RaidEscalationReview.cshtml", vm);
    }

    [HttpPost("raid/escalations/decide")]
    [ValidateAntiForgeryToken]
    [RequireCentralOpsAdmin]
    [ServiceFilter(typeof(Compass.Filters.RaidFeatureGateFilter))]
    public async Task<IActionResult> RaidEscalationDecide(
        int requestId,
        string? decision,
        [FromForm] string? rationale,
        int? rejectTierId,
        string? returnUrl,
        string? returnTab,
        CancellationToken ct)
    {
        SetNav("operations-raid");

        var listTab = NormaliseRaidEscalationListTab(returnTab);

        IActionResult RedirectAfterSuccess()
        {
            if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
                return Redirect(returnUrl);
            return RedirectToAction(nameof(RaidEscalations), new { tab = listTab });
        }

        IActionResult RedirectToReviewError(string message)
        {
            TempData["ErrorMessage"] = message;
            return RedirectToAction(nameof(RaidEscalationAction), new { requestId, returnTab = listTab, returnUrl = returnUrl });
        }

        var normalizedDecision = (decision ?? "").Trim().ToLowerInvariant();
        if (normalizedDecision is not ("approve" or "reject"))
        {
            return RedirectToReviewError("Choose whether to approve or reject this request.");
        }

        var req = await _db.RaidEscalationTierChangeRequests
            .Include(x => x.Risk)
            .Include(x => x.FromRiskTier)
            .Include(x => x.ToRiskTier)
            .FirstOrDefaultAsync(x => x.Id == requestId, ct);
        if (req == null || !string.Equals(req.RecordType, "risk", StringComparison.OrdinalIgnoreCase))
        {
            TempData["ErrorMessage"] = "Tier change request was not found.";
            return RedirectToAction(nameof(RaidEscalations), new { tab = listTab });
        }

        if (!string.Equals(req.Status, "pending", StringComparison.OrdinalIgnoreCase))
        {
            TempData["ErrorMessage"] = "This request has already been decided.";
            return RedirectToAction(nameof(RaidEscalations), new { tab = listTab });
        }

        var userEmail = CurrentUserEmail;
        var currentUser = await _db.Users.FirstOrDefaultAsync(
            u => u.Email != null && u.Email.ToLower() == userEmail.ToLower(),
            ct);
        if (currentUser == null)
            return Unauthorized();

        var utcNow = DateTime.UtcNow;
        if (normalizedDecision == "approve")
        {
            var ac = (rationale ?? Request.Form["rationale"].ToString()).Trim();
            if (string.IsNullOrWhiteSpace(ac))
            {
                return RedirectToReviewError("Enter a rationale for this decision (your approval comment).");
            }

            if (ac.Length > 500)
            {
                return RedirectToReviewError("Rationale must be 500 characters or fewer.");
            }

            if (req.Risk == null)
            {
                return RedirectToReviewError("Risk record for this request could not be loaded.");
            }

            var activeTiers = await _db.RiskTiers.AsNoTracking()
                .Where(t => t.IsActive)
                .ToListAsync(ct);
            var toTier = req.ToRiskTier ?? activeTiers.FirstOrDefault(t => t.Id == req.ToRiskTierId);
            if (toTier == null)
            {
                return RedirectToReviewError("The requested target tier is no longer available.");
            }

            var operationalTarget = RiskTierGovernance.ResolveApprovalOperationalTier(toTier, activeTiers);
            if (operationalTarget == null)
            {
                return RedirectToReviewError(
                    "No operational risk tier is configured for the requested governance level. In Admin → RAID → Risk tiers, add an active operational band (Tier 1, Tier 2, Tier 3) that matches the proposed tier level.");
            }

            req.Risk.RiskTierId = operationalTarget.Id;
            req.Risk.UpdatedByUserId = currentUser.Id;
            req.Risk.UpdatedAt = utcNow;

            req.Status = "approved";
            req.DecidedAt = utcNow;
            req.DecidedByUserId = currentUser.Id;
            req.DecisionNote = ac;

            TempData["SuccessMessage"] = $"Request approved. The risk is now on {operationalTarget.Name}.";
        }
        else
        {
            var note = (rationale ?? Request.Form["rationale"].ToString()).Trim();
            if (string.IsNullOrWhiteSpace(note))
            {
                return RedirectToReviewError("Enter a rationale for this decision (the reason for declining).");
            }

            if (note.Length > 500)
            {
                return RedirectToReviewError("Rationale must be 500 characters or fewer.");
            }

            if (req.Risk == null)
            {
                return RedirectToReviewError("Risk record for this request could not be loaded.");
            }

            var activeTiers = await _db.RiskTiers.AsNoTracking()
                .Where(t => t.IsActive)
                .ToListAsync(ct);
            var operationalById = activeTiers
                .Where(t => !t.IsProposedTier)
                .ToDictionary(t => t.Id, t => t);
            if (rejectTierId is not int opId || !operationalById.TryGetValue(opId, out var chosenTier))
            {
                return RedirectToReviewError("Choose the operational tier this risk should be set to.");
            }

            req.Risk.RiskTierId = opId;
            req.Risk.UpdatedByUserId = currentUser.Id;
            req.Risk.UpdatedAt = utcNow;

            req.Status = "rejected";
            req.DecidedAt = utcNow;
            req.DecidedByUserId = currentUser.Id;
            req.DecisionNote = note;
            TempData["SuccessMessage"] = $"Request declined. The risk is now on {chosenTier.Name}.";
        }

        await _db.SaveChangesAsync(ct);
        return RedirectAfterSuccess();
    }

    [HttpGet("service-register")]
    public async Task<IActionResult> ServiceRegister(
        string? tab,
        string? search,
        int? businessAreaId,
        int? channelId,
        int? userGroupId,
        int? typeId,
        int? phaseId,
        CancellationToken ct)
    {
        var blocked = await FipsDatabaseDisabledRedirectAsync();
        if (blocked != null)
            return blocked;

        SetNav("operations-service-register");

        var requestedTab = string.IsNullOrWhiteSpace(tab) ? "active" : tab.Trim().ToLowerInvariant();
        var email = CurrentUserEmail;

        var vm = await FipsProductListingHelper.BuildProductsViewModelAsync(
            _db, requestedTab, email, search, businessAreaId, channelId, userGroupId, typeId, phaseId,
            categorisationItemId: null, categorisationGroupId: null, ct);
        var activeTab = vm.ActiveTab;
        vm.CanSyncFromCmdb = true;

        var baseUrl = Url.Action(nameof(ServiceRegister), "ModernOperations", new { tab = activeTab })
            ?? "/modern/operations/service-register";
        var sf = FipsProductListingHelper.BuildSearchAndFilter(vm, activeTab, baseUrl);
        sf.ActiveChips = SearchAndFilterActiveChipsBuilder.FromViewModel(
            sf, Url, nameof(ServiceRegister), "ModernOperations", new { tab = activeTab });
        ViewBag.SearchAndFilter = sf;

        return View("~/Views/Modern/Operations/ServiceRegister.cshtml", vm);
    }

    [HttpPost("service-register/bulk-new")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ServiceRegisterBulkNewProducts([FromForm] ServiceRegisterBulkNewForm f, CancellationToken ct)
    {
        var blocked = await FipsDatabaseDisabledRedirectAsync();
        if (blocked != null)
            return blocked;

        var email = CurrentUserEmail;
        var auditName = User.Identity?.Name ?? email;
        var sourceTab = (f.SourceTab ?? "new").Trim().ToLowerInvariant();
        var returnTab = sourceTab is "enterprise" or "all" or "active" or "search" ? sourceTab : "new";

        IActionResult RedirectBack() =>
            RedirectToAction(nameof(ServiceRegister), new
            {
                tab = returnTab,
                search = f.RSearch,
                businessAreaId = f.RBusinessAreaId,
                channelId = f.RChannelId,
                userGroupId = f.RUserGroupId,
                typeId = f.RTypeId,
                phaseId = f.RPhaseId
            });

        var ids = f.ProductIds?.Where(x => x != Guid.Empty).Distinct().ToList() ?? new List<Guid>();
        if (ids.Count == 0)
        {
            TempData["Error"] = "Select at least one product.";
            return RedirectBack();
        }

        var bulkEnterpriseAction = string.IsNullOrWhiteSpace(f.BulkEnterpriseAction)
            ? Request.Form["BulkEnterpriseAction"].ToString()
            : f.BulkEnterpriseAction;
        var applyEnterprise = TryParseBulkEnterpriseAction(bulkEnterpriseAction, out var bulkEnterpriseValue);

        if (!f.ApplyStatus && !f.ApplyPhase && !f.ApplyBusinessArea && !f.ApplyDirectorate && !f.ApplyChannel && !f.ApplyType && !applyEnterprise)
        {
            TempData["Error"] = "Select at least one action (status, phase, enterprise, directorate, business area, channels, or types).";
            return RedirectBack();
        }

        CMDBProductStatus? newStatus = null;
        if (f.ApplyStatus)
        {
            if (f.TargetStatus is null or < 0 or > 3)
            {
                TempData["Error"] = "Choose a valid target status.";
                return RedirectBack();
            }

            newStatus = (CMDBProductStatus)f.TargetStatus.Value;
        }

        int[]? resolvedBasFromLookups = null;
        if (f.ApplyBusinessArea)
        {
            resolvedBasFromLookups = await _fipsBusinessAreaLookupSync.ResolveToFipsBusinessAreaIdsAsync(
                f.BusinessAreaLookupIds ?? Array.Empty<int>(), ct);
        }

        int[]? resolvedDirsFromLookups = null;
        if (f.ApplyDirectorate)
        {
            resolvedDirsFromLookups = await _fipsDirectorateLookupSync.ResolveToFipsDirectorateIdsAsync(
                f.DirectorateLookupIds ?? Array.Empty<int>(), ct);
        }

        var isNewTabOnly = string.Equals(f.SourceTab, "new", StringComparison.OrdinalIgnoreCase);
        var productsQ = _db.CMDBProducts
            .AsNoTracking()
            .Include(p => p.BusinessAreas)
            .Include(p => p.Directorates)
            .Include(p => p.Channels)
            .Include(p => p.UserGroups)
            .Include(p => p.Types)
            .Include(p => p.CategorisationItems)
            .Where(p => ids.Contains(p.Id));
        if (isNewTabOnly)
            productsQ = productsQ.Where(p => p.Status == CMDBProductStatus.New);
        var products = await productsQ.ToListAsync(ct);

        if (products.Count == 0)
        {
            TempData["Error"] = isNewTabOnly
                ? "No new products matched your selection. Refresh the list and try again."
                : "No products matched your selection. Refresh the list and try again.";
            return RedirectBack();
        }

        var fail = 0;
        var ok = 0;

        foreach (var p in products.OrderBy(x => x.Title))
        {
            try
            {
                if (f.ApplyPhase || f.ApplyBusinessArea || f.ApplyDirectorate || f.ApplyChannel || f.ApplyType || applyEnterprise)
                {
                    var phase = f.ApplyPhase ? f.BulkPhaseId : p.PhaseId;
                    var bas = f.ApplyBusinessArea
                        ? (resolvedBasFromLookups ?? Array.Empty<int>())
                        : p.BusinessAreas.Select(b => b.FipsBusinessAreaId).ToArray();
                    var dirs = f.ApplyDirectorate
                        ? (resolvedDirsFromLookups ?? Array.Empty<int>())
                        : p.Directorates.Select(d => d.FipsDirectorateId).ToArray();
                    var ch = f.ApplyChannel
                        ? (f.BulkChannelIds ?? Array.Empty<int>())
                        : p.Channels.Select(c => c.FipsChannelId).ToArray();
                    var ug = p.UserGroups.Select(u => u.FipsUserGroupId).ToArray();
                    var ty = f.ApplyType
                        ? (f.BulkTypeIds ?? Array.Empty<int>())
                        : p.Types.Select(t => t.FipsTypeId).ToArray();
                    var cat = p.CategorisationItems.Select(c => c.FipsCategorisationItemId).ToArray();
                    bool? isEnterprise = applyEnterprise ? bulkEnterpriseValue : null;

                    var u = await _fipsProductWrite.TryUpdateAsync(
                        p.Id,
                        email,
                        auditName,
                        false,
                        p.UserDescription,
                        phase,
                        p.ProductURL,
                        bas,
                        ch,
                        ug,
                        ty,
                        dirs,
                        cat,
                        null,
                        isEnterprise,
                        ct);

                    if (u.NotFound || u.Forbidden)
                    {
                        fail++;
                        continue;
                    }
                }

                if (f.ApplyStatus && newStatus.HasValue)
                {
                    var s = await _fipsProductWrite.TryChangeStatusAsync(
                        p.Id, email, auditName, false, newStatus.Value, ct);
                    if (s.NotFound || s.Forbidden)
                    {
                        fail++;
                        continue;
                    }
                }

                ok++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Bulk new product update failed for {ProductId}", p.Id);
                fail++;
            }
        }

        var notInScope = ids.Count - products.Count;
        if (ok > 0)
            TempData["Success"] = $"Updated {ok} product(s).";

        if (fail > 0 || notInScope > 0)
        {
            var parts = new List<string>();
            if (fail > 0)
                parts.Add($"{fail} product(s) could not be updated.");
            if (notInScope > 0)
            {
                parts.Add(
                    !isNewTabOnly
                        ? $"{notInScope} selected id(s) were not in this list or no longer match."
                        : $"{notInScope} selected id(s) were not new products in this list or no longer match.");
            }
            TempData["Error"] = string.Join(" ", parts);
        }
        else if (ok == 0)
        {
            TempData["Error"] = "No products were updated.";
        }

        return RedirectBack();
    }

    [HttpPost("service-register/sync-cmdb")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ServiceRegisterSync(CancellationToken ct)
    {
        var blocked = await FipsDatabaseDisabledRedirectAsync();
        if (blocked != null)
            return blocked;

        try
        {
            await EnsureDefaultCmdbSyncRulesAsync(ct);
            var r = await _fipsCmdbProductSync.SyncActiveServiceOfferingsAsync(CurrentUserEmail, ct);
            TempData["Success"] =
                $"CMDB sync finished: {r.Created} product(s) created, {r.Updated} updated. {r.StatusSetByRules} status change(s) from sync rules. " +
                $"Skipped {r.SkippedRetired} inactive (retired) in Compass, {r.SkippedNoSysId} CMDB rows without sys_id.";
            if (r.Errors > 0)
            {
                TempData["Error"] =
                    $"{r.Errors} error(s). " +
                    (r.ErrorSamples.Count > 0 ? string.Join(" ", r.ErrorSamples) : string.Empty);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Operations CMDB product sync failed");
            TempData["Error"] = "CMDB sync failed: " + ex.Message;
        }

        return RedirectToAction(nameof(ServiceRegister), new { tab = "active" });
    }

    /// <summary>Server-sent events while bulk CMDB sync runs (for service register modal).</summary>
    [HttpPost("service-register/sync-cmdb-stream")]
    [ValidateAntiForgeryToken]
    public async Task ServiceRegisterSyncStream(CancellationToken ct)
    {
        var blocked = await FipsDatabaseDisabledRedirectAsync();
        if (blocked != null)
        {
            Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        Response.ContentType = "text/event-stream; charset=utf-8";
        Response.Headers.CacheControl = "no-cache, no-store";
        Response.Headers.Pragma = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";

        async Task SendEventAsync(object payload)
        {
            var json = JsonSerializer.Serialize(payload, CmdbSyncSseJsonOptions);
            await Response.WriteAsync($"data: {json}\n\n", ct);
            await Response.Body.FlushAsync(ct);
        }

        try
        {
            await EnsureDefaultCmdbSyncRulesAsync(ct);
            var syncResult = await _fipsCmdbProductSync.SyncActiveServiceOfferingsAsync(
                CurrentUserEmail,
                ct,
                async update => { await SendEventAsync(update); });

            await SendEventAsync(new
            {
                phase = "complete",
                success = true,
                result = new
                {
                    syncResult.Created,
                    syncResult.Updated,
                    syncResult.SkippedRetired,
                    syncResult.SkippedNoSysId,
                    syncResult.SkippedNoLocalMatch,
                    syncResult.StatusSetByRules,
                    syncResult.Errors,
                    errorSamples = syncResult.ErrorSamples
                }
            });
        }
        catch (OperationCanceledException)
        {
            await SendEventAsync(new { phase = "error", success = false, message = "The sync was cancelled." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Operations CMDB product sync stream failed");
            await SendEventAsync(new { phase = "error", success = false, message = ex.Message });
        }
    }

    [HttpPost("service-register/product/{id:guid}/sync-cmdb-json")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ServiceRegisterProductSyncCmdbJson(Guid id, CancellationToken ct)
    {
        var blocked = await FipsDatabaseDisabledRedirectAsync();
        if (blocked != null)
            return blocked;

        try
        {
            var r = await _fipsCmdbProductSync.SyncSingleProductAsync(id, CurrentUserEmail, ct);
            return Json(new { success = r.Success, message = r.Message, statusSetByRule = r.StatusSetByRule });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Operations single CMDB sync JSON failed for product {ProductId}", id);
            return Json(new { success = false, message = "Sync failed: " + ex.Message });
        }
    }

    [HttpGet("service-register/product/{id:guid}")]
    public async Task<IActionResult> ServiceRegisterProduct(Guid id, string? tab, bool edit, CancellationToken ct)
    {
        var blocked = await FipsDatabaseDisabledRedirectAsync();
        if (blocked != null)
            return blocked;

        SetNav("operations-service-register");

        var product = await _db.CMDBProducts
            .Include(p => p.Phase)
            .Include(p => p.BusinessAreas).ThenInclude(ba => ba.FipsBusinessArea)
            .Include(p => p.Directorates).ThenInclude(d => d.FipsDirectorate).ThenInclude(fd => fd.DirectorateLookup)
            .Include(p => p.Channels).ThenInclude(c => c.FipsChannel)
            .Include(p => p.UserGroups).ThenInclude(ug => ug.FipsUserGroup)
            .Include(p => p.Types).ThenInclude(t => t.FipsType)
            .Include(p => p.CategorisationItems).ThenInclude(ci => ci.FipsCategorisationItem).ThenInclude(i => i.Group)
            .Include(p => p.Contacts).ThenInclude(c => c.FipsContactRole)
            .FirstOrDefaultAsync(p => p.Id == id, ct);

        if (product == null)
            return NotFound();

        var detailTab = NormalizeServiceRegisterProductTab(tab);
        if (detailTab == "cmdb")
            edit = false;
        if (edit && detailTab == "information")
            return RedirectToAction("FipsProductEditInformation", "ModernManage", new { id, nc = "operations" });

        var email = CurrentUserEmail;
        var productIdStr = product.Id.ToString();
        var auditHistory = await _db.AuditLogs
            .Where(a => a.Entity == "CMDBProduct" && a.EntityId == productIdStr)
            .OrderByDescending(a => a.ChangedUtc)
            .Select(a => new FipsAuditRow
            {
                ChangedAt = a.ChangedUtc,
                ChangedBy = a.ChangedBy ?? a.ChangedByEmail,
                ChangeType = a.Action,
                FieldName = a.EntityReference,
                PreviousValue = a.BeforeJson,
                NewValue = a.AfterJson
            })
            .ToListAsync(ct);

        var editMode = false;

        var vm = new FipsProductDetailViewModel
        {
            Product = product,
            CanManage = true,
            CanEditInformation = true,
            CurrentUserEmail = email,
            AuditHistory = auditHistory,
            NavContext = null,
            IsOperationsServiceRegisterProduct = true,
            CmdbSnapshotJsonFormatted = FormatCmdbSnapshotJson(product.LastCmdbSnapshotJson),
            EditMode = editMode,
            ActiveDetailTab = detailTab,
        };

        await FipsProductCategorisationPresentation.PopulateAsync(_db, vm, editMode, ct);

        await FipsProductRaidQuery.PopulateRaidListsAsync(_db, vm, product, ct);

        vm.DataCompletion = FipsProductListingHelper.GetDataCompletionSummary(product);

        return View("~/Views/Modern/Operations/ServiceRegisterProduct.cshtml", vm);
    }

    [HttpPost("service-register/product/{id:guid}/update")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ServiceRegisterProductUpdate(Guid id, string? userDescription,
        int? phaseId, string? productURL,
        int[]? directorateLookupIds, int[]? businessAreaLookupIds, int[]? channelIds, int[]? userGroupIds, int[]? typeIds,
        int[]? categorisationItemIds,
        int? reportingContactUserId,
        bool isEnterpriseService,
        CancellationToken ct)
    {
        var blocked = await FipsDatabaseDisabledRedirectAsync();
        if (blocked != null)
            return blocked;

        var resolvedDirectorateIds =
            await _fipsDirectorateLookupSync.ResolveToFipsDirectorateIdsAsync(directorateLookupIds ?? Array.Empty<int>(), ct);
        var resolvedBusinessAreaIds =
            await _fipsBusinessAreaLookupSync.ResolveToFipsBusinessAreaIdsAsync(businessAreaLookupIds ?? Array.Empty<int>(), ct);

        var email = CurrentUserEmail;
        var auditName = User.Identity?.Name ?? email;
        var outcome = await _fipsProductWrite.TryUpdateAsync(
            id,
            email,
            auditName,
            requireServiceOwnerManager: false,
            userDescription,
            phaseId,
            productURL,
            resolvedBusinessAreaIds,
            channelIds,
            userGroupIds,
            typeIds,
            resolvedDirectorateIds,
            categorisationItemIds,
            reportingContactUserId,
            isEnterpriseService: isEnterpriseService,
            ct);

        if (outcome.NotFound)
            return NotFound();

        if (outcome.Changes.Count > 0)
            TempData["Success"] = $"Product updated: {string.Join(", ", outcome.Changes)}.";

        return RedirectToAction(nameof(ServiceRegisterProduct), new { id, tab = "information" });
    }

    [HttpPost("service-register/product/{id:guid}/change-status")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ServiceRegisterProductChangeStatus(Guid id, CMDBProductStatus newStatus, CancellationToken ct)
    {
        var blocked = await FipsDatabaseDisabledRedirectAsync();
        if (blocked != null)
            return blocked;

        var email = CurrentUserEmail;
        var auditName = User.Identity?.Name ?? email;
        var outcome = await _fipsProductWrite.TryChangeStatusAsync(
            id, email, auditName, requireServiceOwnerManager: false, newStatus, ct);

        if (outcome.NotFound)
            return NotFound();

        if (outcome.Changes.Count > 0)
            TempData["Success"] = "Status updated.";

        return RedirectToAction(nameof(ServiceRegisterProduct), new { id });
    }

    [HttpPost("service-register/product/{id:guid}/sync-cmdb")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ServiceRegisterProductSyncCmdb(Guid id, CancellationToken ct)
    {
        var blocked = await FipsDatabaseDisabledRedirectAsync();
        if (blocked != null)
            return blocked;

        try
        {
            var r = await _fipsCmdbProductSync.SyncSingleProductAsync(id, CurrentUserEmail, ct);
            if (r.Success)
                TempData["Success"] = r.Message;
            else
                TempData["Error"] = r.Message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Operations single CMDB sync failed for product {ProductId}", id);
            TempData["Error"] = "CMDB sync failed: " + ex.Message;
        }

        return RedirectToAction(nameof(ServiceRegisterProduct), new { id, tab = "information" });
    }

    [HttpGet("service-register/sync-settings")]
    public async Task<IActionResult> ServiceRegisterSyncSettings(CancellationToken ct)
    {
        var blocked = await FipsDatabaseDisabledRedirectAsync();
        if (blocked != null)
            return blocked;

        SetNav("operations-service-register");
        await EnsureDefaultCmdbSyncRulesAsync(ct);
        var rules = await _db.FipsCmdbSyncRules.AsNoTracking()
            .OrderBy(r => r.SortOrder)
            .ThenBy(r => r.Id)
            .ToListAsync(ct);
        var subNav = await FipsProductListingHelper.BuildSubNavModelAsync(
            _db, "sync", CurrentUserEmail, ct);
        var vm = new ServiceRegisterSyncSettingsViewModel { Rules = rules, SubNav = subNav, CanSyncFromCmdb = true };
        return View("~/Views/Modern/Operations/ServiceRegisterSyncSettings.cshtml", vm);
    }

    [HttpGet("service-register/cmdb-sync-rules")]
    public IActionResult ServiceRegisterCmdbRulesLegacy() =>
        RedirectToAction(nameof(ServiceRegisterSyncSettings));

    [HttpGet("service-register/sync-settings/export-completion")]
    public IActionResult ServiceRegisterExportCompletionTemplate()
    {
        var bytes = FipsCompletionSpreadsheet.BuildHeaderOnlyWorkbook();
        var fileName = $"fips-completion-import-template-{DateTime.UtcNow:yyyyMMdd}.xlsx";
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
    }

    [HttpPost("service-register/sync-settings/import-completion")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ServiceRegisterImportCompletion(IFormFile? file, CancellationToken ct)
    {
        var blocked = await FipsDatabaseDisabledRedirectAsync();
        if (blocked != null)
            return blocked;

        SetNav("operations-service-register");
        await EnsureDefaultCmdbSyncRulesAsync(ct);

        FipsCompletionImportResult? importResult = null;

        if (file == null || file.Length == 0)
        {
            TempData["Error"] = "Choose an Excel (.xlsx) file to upload.";
        }
        else
        {
            var ext = Path.GetExtension(file.FileName ?? "").ToLowerInvariant();
            if (ext is not ".xlsx" and not ".xlsm")
            {
                TempData["Error"] = "Upload an Excel file (.xlsx). Use the FIPS completion export format.";
            }
            else
            {
                try
                {
                    await using var stream = file.OpenReadStream();
                    var rows = FipsCompletionSpreadsheet.ParseImportRows(stream);
                    if (rows.Count == 0)
                    {
                        TempData["Error"] = "No rows with phase or product URL values were found in the file.";
                    }
                    else
                    {
                        var email = CurrentUserEmail;
                        var auditName = User.Identity?.Name ?? email;
                        importResult = await _fipsCompletionBulkImport.ImportAsync(rows, email, auditName, ct);
                        if (importResult.UpdatedCount > 0)
                        {
                            TempData["Success"] =
                                $"Imported {importResult.UpdatedCount} row(s). Skipped {importResult.SkippedCount}, failed {importResult.FailedCount}.";
                        }
                        else if (importResult.FailedCount > 0)
                            TempData["Error"] = $"Import failed for all {importResult.FailedCount} row(s). See details below.";
                        else
                            TempData["Success"] = "No changes were needed — values already match.";
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "FIPS completion import parse failed");
                    TempData["Error"] =
                        "Could not read the file. Use the FIPS completion export from DdtReports, or the download template on this page.";
                }
            }
        }

        var rules = await _db.FipsCmdbSyncRules.AsNoTracking()
            .OrderBy(r => r.SortOrder)
            .ThenBy(r => r.Id)
            .ToListAsync(ct);
        var subNav = await FipsProductListingHelper.BuildSubNavModelAsync(
            _db, "sync", CurrentUserEmail, ct);
        var vm = new ServiceRegisterSyncSettingsViewModel
        {
            Rules = rules,
            SubNav = subNav,
            CanSyncFromCmdb = true,
            LastImportResult = importResult
        };
        return View("~/Views/Modern/Operations/ServiceRegisterSyncSettings.cshtml", vm);
    }

    [HttpPost("service-register/sync-settings/import-strapi-json")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ServiceRegisterImportStrapiJson(IFormFile? file, bool dryRun, CancellationToken ct)
    {
        var blocked = await FipsDatabaseDisabledRedirectAsync();
        if (blocked != null)
            return blocked;

        SetNav("operations-service-register");
        await EnsureDefaultCmdbSyncRulesAsync(ct);

        FipsCompletionImportResult? importResult = null;

        if (file == null || file.Length == 0)
        {
            TempData["Error"] = "Choose a JSON file to upload (legacy Strapi/CMS export with a data array).";
        }
        else
        {
            var ext = Path.GetExtension(file.FileName ?? "").ToLowerInvariant();
            if (ext is not ".json")
            {
                TempData["Error"] = "Upload a JSON file (.json) exported from the legacy FIPS CMS.";
            }
            else
            {
                try
                {
                    await using var stream = file.OpenReadStream();
                    var email = CurrentUserEmail;
                    var auditName = User.Identity?.Name ?? email;
                    importResult = await _fipsStrapiLegacyImport.ImportAsync(stream, email, auditName, dryRun, ct);
                    if (importResult.UpdatedCount > 0)
                    {
                        TempData["Success"] = dryRun
                            ? $"Dry run: {importResult.UpdatedCount} row(s) would update. Skipped {importResult.SkippedCount}, failed {importResult.FailedCount}."
                            : $"Imported {importResult.UpdatedCount} row(s). Skipped {importResult.SkippedCount}, failed {importResult.FailedCount}.";
                    }
                    else if (importResult.FailedCount > 0)
                        TempData["Error"] = $"Import failed for all {importResult.FailedCount} row(s). See details below.";
                    else
                        TempData["Success"] = dryRun
                            ? "Dry run: no changes would be applied."
                            : "No changes were needed — values already match.";
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Strapi legacy JSON import parse failed");
                    TempData["Error"] = "Could not read the JSON file. Ensure it is a valid Strapi export with a top-level data array.";
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Strapi legacy JSON import failed");
                    TempData["Error"] = "Import failed. Check the file format and try again.";
                }
            }
        }

        var rules = await _db.FipsCmdbSyncRules.AsNoTracking()
            .OrderBy(r => r.SortOrder)
            .ThenBy(r => r.Id)
            .ToListAsync(ct);
        var subNav = await FipsProductListingHelper.BuildSubNavModelAsync(
            _db, "sync", CurrentUserEmail, ct);
        var vm = new ServiceRegisterSyncSettingsViewModel
        {
            Rules = rules,
            SubNav = subNav,
            CanSyncFromCmdb = true,
            LastStrapiImportResult = importResult
        };
        return View("~/Views/Modern/Operations/ServiceRegisterSyncSettings.cshtml", vm);
    }

    [HttpPost("service-register/sync-settings/add")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ServiceRegisterSyncSettingsAdd(
        string? name,
        string fieldScope,
        string matchKind,
        string pattern,
        string ruleAction,
        CMDBProductStatus targetStatus,
        int sortOrder,
        bool isActive,
        CancellationToken ct)
    {
        var blocked = await FipsDatabaseDisabledRedirectAsync();
        if (blocked != null)
            return blocked;

        fieldScope = (fieldScope ?? "").Trim();
        matchKind = (matchKind ?? "").Trim();
        pattern = (pattern ?? "").Trim();
        ruleAction = (ruleAction ?? FipsCmdbSyncRuleActions.SetStatus).Trim();
        if (!FipsCmdbSyncRuleActions.All.Contains(ruleAction))
        {
            TempData["Error"] = "Invalid rule action.";
            return RedirectToAction(nameof(ServiceRegisterSyncSettings));
        }
        if (!FipsCmdbSyncRuleScopes.All.Contains(fieldScope))
        {
            TempData["Error"] = "Invalid field scope.";
            return RedirectToAction(nameof(ServiceRegisterSyncSettings));
        }
        if (!FipsCmdbSyncRuleMatchKinds.All.Contains(matchKind))
        {
            TempData["Error"] = "Match kind must be Contains or Regex.";
            return RedirectToAction(nameof(ServiceRegisterSyncSettings));
        }
        if (pattern.Length == 0)
        {
            TempData["Error"] = "Enter a pattern to match.";
            return RedirectToAction(nameof(ServiceRegisterSyncSettings));
        }
        if (pattern.Length > 2000)
        {
            TempData["Error"] = "Pattern is too long (max 2000 characters).";
            return RedirectToAction(nameof(ServiceRegisterSyncSettings));
        }
        if (string.Equals(ruleAction, FipsCmdbSyncRuleActions.SetStatus, StringComparison.OrdinalIgnoreCase)
            && targetStatus != CMDBProductStatus.Active
            && targetStatus != CMDBProductStatus.Rejected
            && targetStatus != CMDBProductStatus.Inactive)
        {
            TempData["Error"] = "Status rules may only set status to Active, Rejected, or Retired (inactive).";
            return RedirectToAction(nameof(ServiceRegisterSyncSettings));
        }

        var now = DateTime.UtcNow;
        _db.FipsCmdbSyncRules.Add(new FipsCmdbSyncRule
        {
            Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
            FieldScope = fieldScope,
            MatchKind = matchKind,
            Pattern = pattern,
            Action = ruleAction,
            TargetStatus = targetStatus,
            SortOrder = sortOrder,
            IsActive = isActive,
            CreatedAt = now,
            UpdatedAt = now
        });
        await _db.SaveChangesAsync(ct);
        return RedirectToAction(nameof(ServiceRegisterSyncSettings));
    }

    [HttpPost("service-register/sync-settings/{id:int}/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ServiceRegisterSyncSettingsDelete(int id, CancellationToken ct)
    {
        var blocked = await FipsDatabaseDisabledRedirectAsync();
        if (blocked != null)
            return blocked;

        var row = await _db.FipsCmdbSyncRules.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (row == null) return NotFound();
        _db.FipsCmdbSyncRules.Remove(row);
        await _db.SaveChangesAsync(ct);
        return RedirectToAction(nameof(ServiceRegisterSyncSettings));
    }

    [HttpPost("service-register/sync-settings/reset-products-for-cmdb")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ServiceRegisterResetProductsForCmdb(CancellationToken ct)
    {
        var blocked = await FipsDatabaseDisabledRedirectAsync();
        if (blocked != null)
            return blocked;

        try
        {
            var r = await _fipsCmdbProductSync.ResetAllProductsForCmdbResyncAsync(CurrentUserEmail, ct);
            TempData["Success"] =
                $"Reset {r.ProductsReset} product(s): cleared status, phase, business area, product URL, type, channel, and enterprise service. " +
                $"{r.SkippedInactive} retired product(s) were not changed. Run Sync from CMDB to reapply CMDB data and sync rules.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CMDB product reset for resync failed");
            TempData["Error"] = "Reset failed: " + ex.Message;
        }

        return RedirectToAction(nameof(ServiceRegisterSyncSettings));
    }

    [HttpPost("service-register/sync-settings/{id:int}/toggle")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ServiceRegisterSyncSettingsToggle(int id, CancellationToken ct)
    {
        var blocked = await FipsDatabaseDisabledRedirectAsync();
        if (blocked != null)
            return blocked;

        var row = await _db.FipsCmdbSyncRules.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (row == null) return NotFound();
        row.IsActive = !row.IsActive;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return RedirectToAction(nameof(ServiceRegisterSyncSettings));
    }

    private async Task EnsureDefaultCmdbSyncRulesAsync(CancellationToken cancellationToken)
    {
        if (!await _db.FipsCmdbSyncRules.AnyAsync(cancellationToken))
        {
            var now = DateTime.UtcNow;
            foreach (var rule in FipsCmdbSyncDefaultRules.CreateSeedRules(now))
                _db.FipsCmdbSyncRules.Add(rule);
            await _db.SaveChangesAsync(cancellationToken);
            return;
        }

        await EnsureBusinessServiceEnterpriseRuleAsync(cancellationToken);
    }

    private async Task EnsureBusinessServiceEnterpriseRuleAsync(CancellationToken cancellationToken)
    {
        var exists = await _db.FipsCmdbSyncRules.AnyAsync(
            r => r.Action == FipsCmdbSyncRuleActions.SetEnterpriseService
                 && r.FieldScope == FipsCmdbSyncRuleScopes.ServiceClassification
                 && r.Pattern == "Business Service",
            cancellationToken);
        if (exists)
            return;

        var now = DateTime.UtcNow;
        _db.FipsCmdbSyncRules.Add(FipsCmdbSyncDefaultRules.CreateBusinessServiceEnterpriseRule(now));
        await _db.SaveChangesAsync(cancellationToken);
    }

    [HttpGet("manage-triage")]
    public async Task<IActionResult> ManageTriage(string? tab = null)
    {
        var blocked = await DemandDisabledRedirectAsync();
        if (blocked != null) return blocked;

        SetNav("operations-manage-triage");
        var triageTab = NormalizeManageTriageTab(tab);
        ViewBag.TriageTab = triageTab;

        var meetings = await _db.DemandPipelineTriageMeetings.AsNoTracking()
            .OrderByDescending(m => m.MeetingDate ?? DateTime.MinValue)
            .ThenByDescending(m => m.CreatedAt)
            .ToListAsync();

        var today = DateTime.UtcNow.Date;
        var upcoming = meetings.Where(m => IsUpcomingTriageMeeting(m, today))
            .OrderBy(m => m.MeetingDate ?? DateTime.MaxValue)
            .ThenBy(m => m.StartTime)
            .ToList();
        var previous = meetings.Where(m => !IsUpcomingTriageMeeting(m, today))
            .OrderByDescending(m => m.MeetingDate ?? DateTime.MinValue)
            .ThenByDescending(m => m.CreatedAt)
            .ToList();

        ViewBag.UpcomingMeetings = upcoming;
        ViewBag.PreviousMeetings = previous;

        var assignedCounts = new Dictionary<Guid, int>();
        if (meetings.Count > 0)
        {
            var meetingIds = meetings.Select(m => m.Id).ToList();
            var rows = await _db.DemandPipelineRequests.AsNoTracking()
                .Where(d => d.TriageMeetingId != null && meetingIds.Contains(d.TriageMeetingId.Value))
                .GroupBy(d => d.TriageMeetingId!.Value)
                .Select(g => new { MeetingId = g.Key, Count = g.Count() })
                .ToListAsync();
            foreach (var row in rows)
                assignedCounts[row.MeetingId] = row.Count;
        }

        ViewBag.TriageMeetingAssignedDemandCounts = assignedCounts;

        return View("~/Views/Modern/Operations/ManageTriage.cshtml");
    }

    [HttpGet("manage-triage/add")]
    public async Task<IActionResult> AddTriageMeeting()
    {
        var blocked = await DemandDisabledRedirectAsync();
        if (blocked != null) return blocked;

        SetNav("operations-manage-triage");
        ViewBag.ReturnTab = "upcoming";
        return View("~/Views/Modern/Operations/EditTriageMeeting.cshtml");
    }

    [HttpGet("manage-triage/edit/{id:guid}")]
    public async Task<IActionResult> EditTriageMeeting(Guid id, string? returnTab = null)
    {
        var blocked = await DemandDisabledRedirectAsync();
        if (blocked != null) return blocked;

        SetNav("operations-manage-triage");
        var meeting = await _db.DemandPipelineTriageMeetings.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == id);

        if (meeting == null)
        {
            TempData["Error"] = "Meeting not found.";
            return RedirectToAction(nameof(ManageTriage), new { tab = returnTab });
        }

        ViewBag.EditingMeeting = meeting;
        ViewBag.ReturnTab = NormalizeManageTriageTab(returnTab);
        ParseChairDisplay(meeting.Chair, out var editName, out var editEmail);
        ViewBag.EditChairName = editName;
        ViewBag.EditChairEmail = editEmail;

        return View("~/Views/Modern/Operations/EditTriageMeeting.cshtml");
    }

    private static string NormalizeManageTriageTab(string? tab)
    {
        var t = (tab ?? "upcoming").Trim().ToLowerInvariant();
        return t == "previous" ? "previous" : "upcoming";
    }

    /// <summary>Scheduled meetings with no date or a date on/after today count as upcoming.</summary>
    private static bool IsUpcomingTriageMeeting(DemandPipelineTriageMeeting m, DateTime todayUtc)
    {
        if (!string.Equals(m.Status, "Scheduled", StringComparison.OrdinalIgnoreCase))
            return false;
        if (m.MeetingDate == null) return true;
        return m.MeetingDate.Value.Date >= todayUtc;
    }

    [HttpPost("manage-triage/save")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveTriageMeeting(
        Guid? id,
        string name,
        DateTime? meetingDate,
        string? startTime,
        string? endTime,
        string? chairName,
        string? chairEmail,
        string? returnTab = null)
    {
        var blocked = await DemandDisabledRedirectAsync();
        if (blocked != null) return blocked;

        var triageTab = NormalizeManageTriageTab(returnTab);
        if (string.IsNullOrWhiteSpace(name))
        {
            TempData["Error"] = "Enter a meeting name.";
            if (id.HasValue)
                return RedirectToAction(nameof(EditTriageMeeting), new { id = id.Value, returnTab = triageTab });
            return RedirectToAction(nameof(AddTriageMeeting));
        }

        var chair = BuildChairDisplay(chairName, chairEmail);
        var now = DateTime.UtcNow;
        var user = User.Identity?.Name;

        startTime = NormalizeTimeInput(startTime);
        endTime = NormalizeTimeInput(endTime);

        if (id.HasValue)
        {
            var entity = await _db.DemandPipelineTriageMeetings.FirstOrDefaultAsync(m => m.Id == id.Value);
            if (entity == null)
            {
                TempData["Error"] = "Meeting not found.";
                return RedirectToAction(nameof(ManageTriage), new { tab = triageTab });
            }

            entity.Name = name.Trim();
            entity.MeetingDate = meetingDate;
            entity.StartTime = startTime;
            entity.EndTime = endTime;
            entity.Chair = chair;
            entity.ChairUserId = null;
            entity.UpdatedAt = now;
            entity.UpdatedBy = user;
        }
        else
        {
            _db.DemandPipelineTriageMeetings.Add(new DemandPipelineTriageMeeting
            {
                Id = Guid.NewGuid(),
                Name = name.Trim(),
                MeetingDate = meetingDate,
                StartTime = startTime,
                EndTime = endTime,
                Chair = chair,
                ChairUserId = null,
                Status = "Scheduled",
                CreatedAt = now,
                UpdatedAt = now,
                CreatedBy = user,
                UpdatedBy = user
            });
        }

        await _db.SaveChangesAsync();
        TempData["Message"] = id.HasValue ? "Triage meeting updated." : "Triage meeting created.";
        return RedirectToAction(nameof(ManageTriage), new { tab = triageTab });
    }

    [HttpPost("manage-triage/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteTriageMeeting(Guid id, string? tab = null)
    {
        var blocked = await DemandDisabledRedirectAsync();
        if (blocked != null) return blocked;

        var triageTab = NormalizeManageTriageTab(tab);
        var entity = await _db.DemandPipelineTriageMeetings.FirstOrDefaultAsync(m => m.Id == id);
        if (entity == null)
        {
            TempData["Error"] = "Meeting not found.";
            return RedirectToAction(nameof(ManageTriage), new { tab = triageTab });
        }

        var assignedCount = await _db.DemandPipelineRequests.CountAsync(d => d.TriageMeetingId == id);
        if (assignedCount > 0)
        {
            TempData["Error"] = assignedCount == 1
                ? "There is 1 demand assigned to this meeting. Remove the assignment before deleting."
                : $"There are {assignedCount} demands assigned to this meeting. Remove the assignments before deleting.";
            return RedirectToAction(nameof(ManageTriage), new { tab = triageTab });
        }

        _db.DemandPipelineTriageMeetings.Remove(entity);
        await _db.SaveChangesAsync();
        TempData["Message"] = "Triage meeting deleted.";
        return RedirectToAction(nameof(ManageTriage), new { tab = triageTab });
    }

    private static string? NormalizeTimeInput(string? t)
    {
        if (string.IsNullOrWhiteSpace(t)) return null;
        t = t.Trim();
        return t.Length > 20 ? t[..20] : t;
    }

    private static string? BuildChairDisplay(string? name, string? email)
    {
        var n = string.IsNullOrWhiteSpace(name) ? "" : name.Trim();
        var e = string.IsNullOrWhiteSpace(email) ? "" : email.Trim();
        if (n.Length == 0 && e.Length == 0) return null;
        if (n.Length == 0) return e.Length > 450 ? e[..450] : e;
        if (e.Length == 0) return n.Length > 450 ? n[..450] : n;
        var combined = $"{n} ({e})";
        return combined.Length > 450 ? combined[..450] : combined;
    }

    private static void ParseChairDisplay(string? chair, out string? name, out string? email)
    {
        name = null;
        email = null;
        if (string.IsNullOrWhiteSpace(chair)) return;
        var m = Regex.Match(chair.Trim(), @"^(.*)\(([^)]+)\)\s*$");
        if (m.Success)
        {
            name = m.Groups[1].Value.Trim();
            email = m.Groups[2].Value.Trim();
        }
        else
            name = chair.Trim();
    }
}
