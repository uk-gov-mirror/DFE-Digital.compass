using System.Text;
using ClosedXML.Excel;
using Compass.Data;
using Compass.Models;
using Compass.Models.DemandTriage;
using Compass.Services;
using Compass.Services.Modern;
using Compass.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Compass.Controllers;

/// <summary>Data exports hub — <c>/Exports</c>. Used by Work and Demand sub-navigation.</summary>
[Authorize]
public class ExportsController : Controller
{
    private readonly CompassDbContext _db;
    private readonly IGlobalFeatureToggleService _features;
    private readonly IMonthlyUpdateService _monthlyUpdateService;

    public ExportsController(
        CompassDbContext db,
        IGlobalFeatureToggleService features,
        IMonthlyUpdateService monthlyUpdateService)
    {
        _db = db;
        _features = features;
        _monthlyUpdateService = monthlyUpdateService;
    }

    private bool IsDemandGloballyActive()
    {
        var row = _db.Features.AsNoTracking().FirstOrDefault(f => f.Code == FeatureCodes.Demand);
        return row == null || row.IsActive;
    }

    /// <summary>Exports landing. <paramref name="section"/> drives which area nav is active (reporting, work, performance, demand).</summary>
    [HttpGet]
    public async Task<IActionResult> Index(string? section = "reporting")
    {
        var s = (section ?? "reporting").Trim().ToLowerInvariant();
        if (s is not ("work" or "demand" or "performance" or "reporting"))
            s = "reporting";

        if (s == "demand")
        {
            if (!await _features.IsFeatureEnabledForPrincipalAsync(FeatureCodes.Demand, User))
                return RedirectToAction(nameof(Index), new { section = "reporting" });

            ViewBag.MainNavSection = "demand";
            ViewBag.SubNavItem = "demand-dashboard";
        }
        else if (s == "work")
        {
            ViewBag.MainNavSection = "work";
            ViewBag.SubNavItem = "work-allwork";
        }
        else if (s == "performance")
        {
            ViewBag.MainNavSection = "performance";
            ViewBag.SubNavItem = "perf-dashboard";
        }
        else
        {
            ViewBag.MainNavSection = "reporting";
            ViewBag.SubNavItem = "reporting-exports";
        }

        ViewBag.ExportsSection = s;
        var demandEnabledForUser = await _features.IsFeatureEnabledForPrincipalAsync(FeatureCodes.Demand, User);
        ViewBag.ShowDemandExportRows = IsDemandGloballyActive() && demandEnabledForUser;

        return View("~/Views/Modern/Exports/Index.cshtml");
    }

    [HttpGet]
    public Task<IActionResult> DownloadDemandExcel(CancellationToken cancellationToken = default)
        => DownloadExcel(cancellationToken);

    [HttpGet]
    public async Task<IActionResult> DownloadWorkExcel(CancellationToken cancellationToken = default)
    {
        using var wb = new XLWorkbook();

        var projects = await _db.Projects.AsNoTracking()
            .Where(p => !p.IsDeleted)
            .Include(p => p.PhaseLookup)
            .Include(p => p.BusinessAreaLookup)
            .Include(p => p.RagStatusLookup)
            .Include(p => p.DeliveryPriority)
            .Include(p => p.ActivityTypeLookup)
            .Include(p => p.RiskAppetiteLookup)
            .Include(p => p.PrimaryOrganizationalGroup)
            .Include(p => p.PrimaryContactUser)
            .OrderBy(p => p.Title)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);

        var projectIds = projects.Select(p => p.Id).ToList();

        var monthlyAll = await _db.ProjectMonthlyUpdates.AsNoTracking()
            .Where(m => projectIds.Contains(m.ProjectId))
            .OrderByDescending(m => m.Year).ThenByDescending(m => m.Month)
            .ToListAsync(cancellationToken);
        var monthlyByProjectId = monthlyAll
            .GroupBy(m => m.ProjectId)
            .ToDictionary(g => g.Key, g => g.ToList());
        foreach (var project in projects)
        {
            project.MonthlyUpdates = monthlyByProjectId.TryGetValue(project.Id, out var updates)
                ? updates
                : new List<ProjectMonthlyUpdate>();
        }

        var (reportYear, reportMonth) = _monthlyUpdateService.ResolveDashboardReportingPeriod(DateTime.UtcNow);
        var periodColumns = await WorkRegisterMonthlySubmissionExportHelper.LoadPeriodColumnsAsync(
            _db,
            reportYear,
            reportMonth,
            WorkRegisterMonthlySubmissionExportHelper.DefaultMinReportYear,
            cancellationToken);
        var periodStatusesByProject = WorkRegisterMonthlySubmissionExportHelper.BuildPeriodStatusesByProject(
            projects,
            periodColumns);

        var latestMuByProject = monthlyAll
            .GroupBy(m => m.ProjectId)
            .ToDictionary(g => g.Key, g => g.First());

        var latestMuIds = latestMuByProject.Values.Select(m => m.Id).ToList();
        var latestDraftMeta = latestMuIds.Count == 0
            ? new Dictionary<int, ProjectMonthlyUpdate>()
            : await _db.ProjectMonthlyUpdates.AsNoTracking()
                .Where(m => latestMuIds.Contains(m.Id))
                .Include(m => m.DraftRagStatusLookup)
                .ToDictionaryAsync(m => m.Id, cancellationToken);

        var narrativeRows = latestMuIds.Count == 0
            ? new List<MonthlyUpdateNarrative>()
            : await _db.MonthlyUpdateNarratives.AsNoTracking()
                .Where(n => latestMuIds.Contains(n.ProjectMonthlyUpdateId))
                .OrderBy(n => n.ProjectMonthlyUpdateId).ThenBy(n => n.Id)
                .ToListAsync(cancellationToken);
        var extraNarrativeByMuId = narrativeRows
            .GroupBy(n => n.ProjectMonthlyUpdateId)
            .ToDictionary(g => g.Key, g => string.Join("\n---\n", g.Select(x => x.Narrative)));

        var contacts = await _db.ProjectContacts.AsNoTracking()
            .Where(c => projectIds.Contains(c.ProjectId))
            .OrderBy(c => c.ProjectId).ThenBy(c => c.SortOrder)
            .ToListAsync(cancellationToken);
        var contactsByProject = contacts
            .GroupBy(c => c.ProjectId)
            .ToDictionary(
                g => g.Key,
                g => string.Join("; ", g.Select(x => $"{x.Role}: {x.Name} <{x.Email}>")));

        var sros = await _db.ProjectSeniorResponsibleOfficers.AsNoTracking()
            .Where(s => projectIds.Contains(s.ProjectId))
            .Include(s => s.User)
            .ToListAsync(cancellationToken);
        var sroByProject = sros
            .GroupBy(s => s.ProjectId)
            .ToDictionary(
                g => g.Key,
                g => string.Join("; ", g.Select(x => UserDisplay(x.User))));

        var svcOwners = await _db.ProjectServiceOwners.AsNoTracking()
            .Where(s => projectIds.Contains(s.ProjectId))
            .Include(s => s.User)
            .ToListAsync(cancellationToken);
        var svcByProject = svcOwners
            .GroupBy(s => s.ProjectId)
            .ToDictionary(
                g => g.Key,
                g => string.Join("; ", g.Select(x => UserDisplay(x.User))));

        var pmo = await _db.ProjectPmoContacts.AsNoTracking()
            .Where(s => projectIds.Contains(s.ProjectId))
            .Include(s => s.User)
            .ToListAsync(cancellationToken);
        var pmoByProject = pmo
            .GroupBy(s => s.ProjectId)
            .ToDictionary(
                g => g.Key,
                g => string.Join("; ", g.Select(x => UserDisplay(x.User))));

        var directorates = await _db.ProjectDirectorates.AsNoTracking()
            .Where(d => projectIds.Contains(d.ProjectId))
            .Include(d => d.Division)
            .ToListAsync(cancellationToken);
        var divByProject = directorates
            .GroupBy(d => d.ProjectId)
            .ToDictionary(
                g => g.Key,
                g => string.Join("; ", g.Select(x => x.Division.Name).Distinct()));

        var tags = await _db.ProjectWorkItemTags.AsNoTracking()
            .Where(t => projectIds.Contains(t.ProjectId))
            .Include(t => t.WorkItemTagLookup)
            .ToListAsync(cancellationToken);
        var tagsByProject = tags
            .GroupBy(t => t.ProjectId)
            .ToDictionary(
                g => g.Key,
                g => string.Join("; ", g.Select(x => x.WorkItemTagLookup.Name)));

        var products = await _db.ProjectProducts.AsNoTracking()
            .Where(pp => projectIds.Contains(pp.ProjectId))
            .ToListAsync(cancellationToken);
        var productsByProject = products
            .GroupBy(pp => pp.ProjectId)
            .ToDictionary(
                g => g.Key,
                g => string.Join("; ", g.Select(x =>
                    string.IsNullOrWhiteSpace(x.ProductFipsId)
                        ? x.ProductTitle
                        : $"{x.ProductTitle} ({x.ProductFipsId})")));

        var openRiskCounts = await _db.Risks.AsNoTracking()
            .Where(r => !r.IsDeleted && r.ClosedDate == null && r.ProjectId != null && projectIds.Contains(r.ProjectId.Value))
            .GroupBy(r => r.ProjectId!.Value)
            .Select(g => new { ProjectId = g.Key, C = g.Count() })
            .ToListAsync(cancellationToken);
        var openRiskDict = openRiskCounts.ToDictionary(x => x.ProjectId, x => x.C);

        var openIssueCounts = await _db.Issues.AsNoTracking()
            .Where(r => !r.IsDeleted && r.ClosedDate == null && r.ProjectId != null && projectIds.Contains(r.ProjectId.Value))
            .GroupBy(r => r.ProjectId!.Value)
            .Select(g => new { ProjectId = g.Key, C = g.Count() })
            .ToListAsync(cancellationToken);
        var openIssueDict = openIssueCounts.ToDictionary(x => x.ProjectId, x => x.C);

        var asmCounts = await _db.Assumptions.AsNoTracking()
            .Where(a => !a.IsDeleted && a.ProjectId != null && projectIds.Contains(a.ProjectId.Value))
            .GroupBy(a => a.ProjectId!.Value)
            .Select(g => new { ProjectId = g.Key, C = g.Count() })
            .ToListAsync(cancellationToken);
        var asmDict = asmCounts.ToDictionary(x => x.ProjectId, x => x.C);

        var msCounts = await _db.Milestones.AsNoTracking()
            .Where(m => m.ProjectId != null && !m.IsDeleted && projectIds.Contains(m.ProjectId.Value))
            .GroupBy(m => m.ProjectId!.Value)
            .Select(g => new { ProjectId = g.Key, C = g.Count() })
            .ToListAsync(cancellationToken);
        var msDict = msCounts.ToDictionary(x => x.ProjectId, x => x.C);

        var prodCounts = products
            .GroupBy(p => p.ProjectId)
            .ToDictionary(g => g.Key, g => g.Count());

        var wsMaster = wb.AddWorksheet("Work items (master)");
        WriteWorkItemsMasterSheet(
            wsMaster,
            projects,
            latestMuByProject,
            latestDraftMeta,
            extraNarrativeByMuId,
            contactsByProject,
            sroByProject,
            svcByProject,
            pmoByProject,
            divByProject,
            tagsByProject,
            productsByProject,
            prodCounts,
            openRiskDict,
            openIssueDict,
            asmDict,
            msDict,
            periodColumns,
            periodStatusesByProject);

        var milestones = await _db.Milestones.AsNoTracking()
            .Where(m => m.ProjectId != null && !m.IsDeleted && projectIds.Contains(m.ProjectId.Value))
            .OrderBy(m => m.ProjectId).ThenBy(m => m.DueDate)
            .ToListAsync(cancellationToken);
        var workItemTitles = projects.ToDictionary(p => p.Id, p => p.Title);
        var wsMilestones = wb.AddWorksheet("Milestones");
        WorkRegisterExcelExport.WriteMilestonesSheet(wsMilestones, milestones, workItemTitles);

        var monthlyDetailed = await _db.ProjectMonthlyUpdates.AsNoTracking()
            .Include(m => m.DraftRagStatusLookup)
            .Where(m => projectIds.Contains(m.ProjectId))
            .OrderBy(m => m.ProjectId).ThenByDescending(m => m.Year).ThenByDescending(m => m.Month)
            .ToListAsync(cancellationToken);
        var muIdsAll = monthlyDetailed.Select(m => m.Id).ToList();
        var narrAll = muIdsAll.Count == 0
            ? new List<MonthlyUpdateNarrative>()
            : await _db.MonthlyUpdateNarratives.AsNoTracking()
                .Where(n => muIdsAll.Contains(n.ProjectMonthlyUpdateId))
                .OrderBy(n => n.ProjectMonthlyUpdateId).ThenBy(n => n.Id)
                .ToListAsync(cancellationToken);
        var narrBlocksAll = narrAll
            .GroupBy(n => n.ProjectMonthlyUpdateId)
            .ToDictionary(g => g.Key, g => string.Join("\n---\n", g.Select(x => x.Narrative)));

        var codeTitle = projects.ToDictionary(p => p.Id, p => (Code: p.ProjectCode, Title: p.Title));
        var wsMonthly = wb.AddWorksheet("Monthly updates (history)");
        WriteProjectMonthlyUpdatesDetailed(wsMonthly, monthlyDetailed, narrBlocksAll, codeTitle);

        using var stream = new MemoryStream();
        wb.SaveAs(stream);
        stream.Position = 0;
        var fileName = $"Compass-work-export-{DateTime.UtcNow:yyyyMMdd-HHmm}.xlsx";
        return File(
            stream.ToArray(),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            fileName);
    }

    private static string UserDisplay(User? u)
    {
        if (u == null) return "";
        return string.IsNullOrWhiteSpace(u.Name) ? (u.Email ?? "") : $"{u.Name} ({u.Email})";
    }

    [HttpGet]
    public async Task<IActionResult> DownloadPerformanceExcel(CancellationToken cancellationToken = default)
    {
        using var wb = new XLWorkbook();

        var commissions = await _db.Commissions.AsNoTracking()
            .OrderByDescending(c => c.StartDate)
            .ThenBy(c => c.Name)
            .ToListAsync(cancellationToken);
        var wsCommissions = wb.AddWorksheet("Commissions");
        WriteCommissionsSheet(wsCommissions, commissions);

        var submissions = await _db.CommissionSubmissions.AsNoTracking()
            .OrderByDescending(s => s.UpdatedAt)
            .ToListAsync(cancellationToken);
        var wsSubmissions = wb.AddWorksheet("Commission submissions");
        WriteCommissionSubmissionsSheet(wsSubmissions, submissions);

        var metrics = await _db.PerformanceMetrics.AsNoTracking()
            .OrderBy(m => m.Identifier)
            .ToListAsync(cancellationToken);
        var wsMetrics = wb.AddWorksheet("Performance metrics");
        WritePerformanceMetricsSheet(wsMetrics, metrics);

        var metricNameById = metrics.ToDictionary(m => m.Id, m => m.Identifier);
        var metricValues = await _db.CommissionMetricValues.AsNoTracking()
            .OrderByDescending(v => v.UpdatedAt)
            .ToListAsync(cancellationToken);
        var wsValues = wb.AddWorksheet("Metric values");
        WriteCommissionMetricValuesSheet(wsValues, metricValues, metricNameById);

        using var stream = new MemoryStream();
        wb.SaveAs(stream);
        stream.Position = 0;
        var fileName = $"Compass-performance-export-{DateTime.UtcNow:yyyyMMdd-HHmm}.xlsx";
        return File(
            stream.ToArray(),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            fileName);
    }

    [HttpGet]
    public async Task<IActionResult> DownloadExcel(CancellationToken cancellationToken = default)
    {
        if (!IsDemandGloballyActive())
            return NotFound();

        using var wb = new XLWorkbook();

        var businessCases = await _db.BusinessCases.AsNoTracking().OrderBy(b => b.BusinessCaseId).ToListAsync(cancellationToken);
        WriteBusinessCasesSheet(wb.AddWorksheet("Business cases"), businessCases);

        var demands = await _db.DemandTriageRequests.AsNoTracking().OrderBy(d => d.RequestReference).ToListAsync(cancellationToken);
        WriteDemandsSheet(wb.AddWorksheet("Demands"), demands);

        var outcomes = await _db.DemandTriageOutcomes.AsNoTracking()
            .OrderByDescending(t => t.DecidedAt ?? t.CreatedAt)
            .ToListAsync(cancellationToken);
        WriteTriageOutcomesSheet(wb.AddWorksheet("Triage outcomes"), outcomes);

        var scorecards = await _db.DemandScorecards.AsNoTracking()
            .OrderByDescending(s => s.UpdatedAt)
            .ToListAsync(cancellationToken);
        WriteScorecardsSheet(wb.AddWorksheet("Scoring"), scorecards);

        var reviews = await _db.DemandExploratoryReviews.AsNoTracking()
            .OrderByDescending(r => r.CompletedAt ?? r.CreatedAt)
            .ToListAsync(cancellationToken);
        WriteExploratoryReviewsSheet(wb.AddWorksheet("Exploratory reviews"), reviews);

        using var stream = new MemoryStream();
        wb.SaveAs(stream);
        stream.Position = 0;
        var fileName = $"Compass-demand-export-{DateTime.UtcNow:yyyyMMdd-HHmm}.xlsx";
        return File(
            stream.ToArray(),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            fileName);
    }

    private static void WriteWorkItemsMasterSheet(
        IXLWorksheet ws,
        List<Project> projects,
        IReadOnlyDictionary<int, ProjectMonthlyUpdate> latestMuByProject,
        IReadOnlyDictionary<int, ProjectMonthlyUpdate> latestDraftMeta,
        IReadOnlyDictionary<int, string> extraNarrativeByMuId,
        IReadOnlyDictionary<int, string> contactsByProject,
        IReadOnlyDictionary<int, string> sroByProject,
        IReadOnlyDictionary<int, string> svcByProject,
        IReadOnlyDictionary<int, string> pmoByProject,
        IReadOnlyDictionary<int, string> divByProject,
        IReadOnlyDictionary<int, string> tagsByProject,
        IReadOnlyDictionary<int, string> productsByProject,
        IReadOnlyDictionary<int, int> prodCounts,
        IReadOnlyDictionary<int, int> openRiskDict,
        IReadOnlyDictionary<int, int> openIssueDict,
        IReadOnlyDictionary<int, int> asmDict,
        IReadOnlyDictionary<int, int> msDict,
        IReadOnlyList<SubmissionTrendMonthColumn> periodColumns,
        IReadOnlyDictionary<int, List<string>> periodStatusesByProject)
    {
        var headers = new List<string>
        {
            "WorkItemId", "ProjectCode", "Title", "Aim", "StrategicObjectives", "MissionPillars",
            "StartDate", "TargetDeliveryDate", "ActualDeliveryDate",
            "Phase", "BusinessArea", "RagStatus", "RagJustification", "PathToGreen",
            "DeliveryPriority", "DeliveryPriorityChangeReason",
            "ActivityType", "RiskAppetite",
            "Portfolio_OrganizationalGroup", "PrimaryContact",
            "Status", "StatusChangeReason",
            "IsFlagship", "IsAiInitiative", "ShowInFips", "IsMultiDepartmentProject", "OtherDepartmentsJson",
            "BusinessCaseApproval", "TotalPermFte", "TotalMspFte",
            "PipelineDemandRequestId", "ServiceUsers", "IsInternal", "IsExternal",
            "IsSubjectToSpendControl", "CreationMethod", "CreatedAt", "UpdatedAt",
            "HistoricBuRTId",
            "Divisions", "SeniorResponsibleOfficers", "ServiceOwners", "PmoContacts",
            "TeamContacts", "WorkItemTags",
            "LinkedProductsSummary", "LinkedProductCount",
            "OpenRisksCount", "OpenIssuesCount", "AssumptionsLinkedCount", "MilestoneCount",
            "DiscoveryStartDatePlanned", "DiscoveryStartDateActual", "DiscoveryEndDatePlanned", "DiscoveryEndDateActual",
            "AlphaStartDatePlanned", "AlphaStartDateActual", "AlphaEndDatePlanned", "AlphaEndDateActual",
            "PrivateBetaStartDatePlanned", "PrivateBetaStartDateActual", "PrivateBetaEndDatePlanned", "PrivateBetaEndDateActual",
            "PublicBetaStartDatePlanned", "PublicBetaStartDateActual", "PublicBetaEndDatePlanned", "PublicBetaEndDateActual",
            "LatestMonthly_Period",
            "LatestMonthly_SubmittedAt",
            "LatestMonthly_MainNarrative",
            "LatestMonthly_AdditionalNarrativeBlocks",
            "LatestMonthly_PermFte", "LatestMonthly_MspFte",
            "LatestMonthly_DraftRag", "LatestMonthly_DraftRagJustification", "LatestMonthly_DraftPathToGreen",
            "LatestMonthly_CreatedAt", "LatestMonthly_UpdatedAt",
            "LatestMonthly_CreatedByName", "LatestMonthly_CreatedByEmail"
        };
        headers.AddRange(periodColumns.Select(c => c.Label));

        for (var c = 0; c < headers.Count; c++)
            ws.Cell(1, c + 1).Value = headers[c];
        ws.Row(1).Style.Font.Bold = true;

        var row = 2;
        foreach (var p in projects)
        {
            latestMuByProject.TryGetValue(p.Id, out var mu);
            ProjectMonthlyUpdate? muFull = null;
            if (mu != null)
                latestDraftMeta.TryGetValue(mu.Id, out muFull);

            string? extraNar = null;
            if (mu != null)
                extraNarrativeByMuId.TryGetValue(mu.Id, out extraNar);
            contactsByProject.TryGetValue(p.Id, out var contactStr);
            sroByProject.TryGetValue(p.Id, out var sroStr);
            svcByProject.TryGetValue(p.Id, out var svcStr);
            pmoByProject.TryGetValue(p.Id, out var pmoStr);
            divByProject.TryGetValue(p.Id, out var divStr);
            tagsByProject.TryGetValue(p.Id, out var tagStr);
            productsByProject.TryGetValue(p.Id, out var prodStr);
            prodCounts.TryGetValue(p.Id, out var pc);
            openRiskDict.TryGetValue(p.Id, out var rc);
            openIssueDict.TryGetValue(p.Id, out var ic);
            asmDict.TryGetValue(p.Id, out var ac);
            msDict.TryGetValue(p.Id, out var mc);

            var period = mu == null
                ? ""
                : $"{mu.Year:D4}-{mu.Month:D2}";

            var draftRagName = muFull?.DraftRagStatusLookup?.Name ?? "";

            var col = 1;
            SetCell(ws, row, ref col, p.Id);
            SetCell(ws, row, ref col, p.ProjectCode);
            SetCell(ws, row, ref col, p.Title);
            SetCell(ws, row, ref col, p.Aim);
            SetCell(ws, row, ref col, p.StrategicObjectives);
            SetCell(ws, row, ref col, p.MissionPillars);
            SetCell(ws, row, ref col, p.StartDate);
            SetCell(ws, row, ref col, p.TargetDeliveryDate);
            SetCell(ws, row, ref col, p.ActualDeliveryDate);
            SetCell(ws, row, ref col, p.PhaseLookup?.Name);
            SetCell(ws, row, ref col, p.BusinessAreaLookup?.Name);
            SetCell(ws, row, ref col, p.RagStatusLookup?.Name ?? p.RagStatus);
            SetCell(ws, row, ref col, p.RagJustification);
            SetCell(ws, row, ref col, p.PathToGreen);
            SetCell(ws, row, ref col, p.DeliveryPriority?.Name);
            SetCell(ws, row, ref col, p.DeliveryPriorityChangeReason);
            SetCell(ws, row, ref col, p.ActivityTypeLookup?.Name);
            SetCell(ws, row, ref col, p.RiskAppetiteLookup?.Name);
            SetCell(ws, row, ref col, p.PrimaryOrganizationalGroup?.Name);
            SetCell(ws, row, ref col, p.PrimaryContactUser != null ? UserDisplay(p.PrimaryContactUser) : "");
            SetCell(ws, row, ref col, p.Status);
            SetCell(ws, row, ref col, p.StatusChangeReason);
            SetCell(ws, row, ref col, p.IsFlagship);
            SetCell(ws, row, ref col, p.IsAiInitiative);
            SetCell(ws, row, ref col, p.ShowInFips);
            SetCell(ws, row, ref col, p.IsMultiDepartmentProject);
            SetCell(ws, row, ref col, p.OtherDepartments);
            SetCell(ws, row, ref col, p.BusinessCaseApproval);
            SetCell(ws, row, ref col, p.TotalPermFte);
            SetCell(ws, row, ref col, p.TotalMspFte);
            SetCell(ws, row, ref col, p.PipelineDemandRequestId?.ToString());
            SetCell(ws, row, ref col, p.ServiceUsers);
            SetCell(ws, row, ref col, p.IsInternal);
            SetCell(ws, row, ref col, p.IsExternal);
            SetCell(ws, row, ref col, p.IsSubjectToSpendControl);
            SetCell(ws, row, ref col, p.CreationMethod);
            SetCell(ws, row, ref col, p.CreatedAt);
            SetCell(ws, row, ref col, p.UpdatedAt);
            SetCell(ws, row, ref col, p.HistoricBuRTId);
            SetCell(ws, row, ref col, divStr ?? "");
            SetCell(ws, row, ref col, sroStr ?? "");
            SetCell(ws, row, ref col, svcStr ?? "");
            SetCell(ws, row, ref col, pmoStr ?? "");
            SetCell(ws, row, ref col, contactStr ?? "");
            SetCell(ws, row, ref col, tagStr ?? "");
            SetCell(ws, row, ref col, prodStr ?? "");
            SetCell(ws, row, ref col, pc);
            SetCell(ws, row, ref col, rc);
            SetCell(ws, row, ref col, ic);
            SetCell(ws, row, ref col, ac);
            SetCell(ws, row, ref col, mc);
            SetCell(ws, row, ref col, p.DiscoveryStartDatePlanned);
            SetCell(ws, row, ref col, p.DiscoveryStartDateActual);
            SetCell(ws, row, ref col, p.DiscoveryEndDatePlanned);
            SetCell(ws, row, ref col, p.DiscoveryEndDateActual);
            SetCell(ws, row, ref col, p.AlphaStartDatePlanned);
            SetCell(ws, row, ref col, p.AlphaStartDateActual);
            SetCell(ws, row, ref col, p.AlphaEndDatePlanned);
            SetCell(ws, row, ref col, p.AlphaEndDateActual);
            SetCell(ws, row, ref col, p.PrivateBetaStartDatePlanned);
            SetCell(ws, row, ref col, p.PrivateBetaStartDateActual);
            SetCell(ws, row, ref col, p.PrivateBetaEndDatePlanned);
            SetCell(ws, row, ref col, p.PrivateBetaEndDateActual);
            SetCell(ws, row, ref col, p.PublicBetaStartDatePlanned);
            SetCell(ws, row, ref col, p.PublicBetaStartDateActual);
            SetCell(ws, row, ref col, p.PublicBetaEndDatePlanned);
            SetCell(ws, row, ref col, p.PublicBetaEndDateActual);
            SetCell(ws, row, ref col, period);
            SetCell(ws, row, ref col, mu?.SubmittedAt);
            SetCell(ws, row, ref col, mu?.Narrative);
            SetCell(ws, row, ref col, extraNar ?? "");
            SetCell(ws, row, ref col, mu?.MonthlyPermFte);
            SetCell(ws, row, ref col, mu?.MonthlyMspFte);
            SetCell(ws, row, ref col, draftRagName);
            SetCell(ws, row, ref col, mu?.DraftRagJustification);
            SetCell(ws, row, ref col, mu?.DraftPathToGreen);
            SetCell(ws, row, ref col, mu?.CreatedAt);
            SetCell(ws, row, ref col, mu?.UpdatedAt);
            SetCell(ws, row, ref col, mu?.CreatedByName);
            SetCell(ws, row, ref col, mu?.CreatedByEmail);

            if (periodColumns.Count > 0
                && periodStatusesByProject.TryGetValue(p.Id, out var periodStatuses))
            {
                for (var i = 0; i < periodColumns.Count; i++)
                {
                    var status = i < periodStatuses.Count ? periodStatuses[i] : "—";
                    SetCell(ws, row, ref col, status);
                }
            }
            else if (periodColumns.Count > 0)
            {
                for (var i = 0; i < periodColumns.Count; i++)
                    SetCell(ws, row, ref col, "—");
            }

            row++;
        }

        ws.SheetView.FreezeRows(1);
        ws.Columns(1, headers.Count).AdjustToContents();
    }

    private static void SetCell(IXLWorksheet ws, int row, ref int col, object? v)
    {
        var cell = ws.Cell(row, col++);
        switch (v)
        {
            case null:
                return;
            case DateTime dt:
                cell.Value = dt;
                return;
            case bool b:
                cell.Value = b;
                return;
            case int i:
                cell.Value = i;
                return;
            case long l:
                cell.Value = l;
                return;
            case decimal d:
                cell.Value = d;
                return;
            case double f:
                cell.Value = f;
                return;
            default:
                cell.Value = v.ToString() ?? "";
                return;
        }
    }

    private static void WriteProjectMonthlyUpdatesDetailed(
        IXLWorksheet ws,
        List<ProjectMonthlyUpdate> list,
        IReadOnlyDictionary<int, string> narrativeBlocksByMuId,
        IReadOnlyDictionary<int, (string Code, string Title)> codeTitle)
    {
        var headers = new[]
        {
            "MonthlyUpdateId", "ProjectId", "ProjectCode", "ProjectTitle", "Year", "Month",
            "MainNarrative", "AdditionalNarrativeBlocks",
            "SubmittedAt", "MonthlyPermFte", "MonthlyMspFte",
            "DraftRagStatus", "DraftRagJustification", "DraftPathToGreen",
            "CreatedAt", "UpdatedAt", "CreatedByName", "CreatedByEmail"
        };
        for (var c = 0; c < headers.Length; c++)
            ws.Cell(1, c + 1).Value = headers[c];
        ws.Row(1).Style.Font.Bold = true;
        var row = 2;
        foreach (var m in list)
        {
            codeTitle.TryGetValue(m.ProjectId, out var ct);
            narrativeBlocksByMuId.TryGetValue(m.Id, out var blocks);
            var col = 1;
            SetCell(ws, row, ref col, m.Id);
            SetCell(ws, row, ref col, m.ProjectId);
            SetCell(ws, row, ref col, ct.Code ?? "");
            SetCell(ws, row, ref col, ct.Title ?? "");
            SetCell(ws, row, ref col, m.Year);
            SetCell(ws, row, ref col, m.Month);
            SetCell(ws, row, ref col, m.Narrative);
            SetCell(ws, row, ref col, blocks ?? "");
            SetCell(ws, row, ref col, m.SubmittedAt);
            SetCell(ws, row, ref col, m.MonthlyPermFte);
            SetCell(ws, row, ref col, m.MonthlyMspFte);
            SetCell(ws, row, ref col, m.DraftRagStatusLookup?.Name ?? "");
            SetCell(ws, row, ref col, m.DraftRagJustification);
            SetCell(ws, row, ref col, m.DraftPathToGreen);
            SetCell(ws, row, ref col, m.CreatedAt);
            SetCell(ws, row, ref col, m.UpdatedAt);
            SetCell(ws, row, ref col, m.CreatedByName);
            SetCell(ws, row, ref col, m.CreatedByEmail);
            row++;
        }

        ws.SheetView.FreezeRows(1);
        ws.Columns(1, headers.Length).AdjustToContents();
    }

    private static void WriteBusinessCasesSheet(IXLWorksheet ws, List<BusinessCase> list)
    {
        var headers = new[] { "Id", "BusinessCaseId", "Title", "Status", "BusinessArea", "CreatedAt", "UpdatedAt" };
        for (var c = 0; c < headers.Length; c++)
            ws.Cell(1, c + 1).Value = headers[c];
        ws.Row(1).Style.Font.Bold = true;
        var row = 2;
        foreach (var b in list)
        {
            ws.Cell(row, 1).Value = b.Id;
            ws.Cell(row, 2).Value = b.BusinessCaseId;
            ws.Cell(row, 3).Value = b.Title;
            ws.Cell(row, 4).Value = b.Status;
            ws.Cell(row, 5).Value = b.BusinessArea;
            ws.Cell(row, 6).Value = b.CreatedAt;
            ws.Cell(row, 7).Value = b.UpdatedAt;
            row++;
        }
        ws.Columns().AdjustToContents();
    }

    private static void WriteDemandsSheet(IXLWorksheet ws, List<DemandTriageRequest> list)
    {
        var headers = new[] { "Id", "RequestReference", "Status", "RequestName", "RequesterFullName", "ProposedRequestTitle", "TargetDeliveryDate", "CreatedAt", "UpdatedAt" };
        for (var c = 0; c < headers.Length; c++)
            ws.Cell(1, c + 1).Value = headers[c];
        ws.Row(1).Style.Font.Bold = true;
        var row = 2;
        foreach (var d in list)
        {
            ws.Cell(row, 1).Value = d.Id;
            ws.Cell(row, 2).Value = d.RequestReference;
            ws.Cell(row, 3).Value = d.Status;
            ws.Cell(row, 4).Value = d.RequestName;
            ws.Cell(row, 5).Value = d.RequesterFullName;
            ws.Cell(row, 6).Value = d.ProposedRequestTitle;
            ws.Cell(row, 7).Value = d.TargetDeliveryDate;
            ws.Cell(row, 8).Value = d.CreatedAt;
            ws.Cell(row, 9).Value = d.UpdatedAt;
            row++;
        }
        ws.Columns().AdjustToContents();
    }

    private static void WriteTriageOutcomesSheet(IXLWorksheet ws, List<DemandTriageOutcome> list)
    {
        var headers = new[] { "Id", "DemandTriageRequestId", "OutcomeSelection", "OutcomeSummary", "RoutedToArea", "DecidedAt", "DecidedBy", "CreatedAt" };
        for (var c = 0; c < headers.Length; c++)
            ws.Cell(1, c + 1).Value = headers[c];
        ws.Row(1).Style.Font.Bold = true;
        var row = 2;
        foreach (var t in list)
        {
            ws.Cell(row, 1).Value = t.Id;
            ws.Cell(row, 2).Value = t.DemandTriageRequestId;
            ws.Cell(row, 3).Value = t.OutcomeSelection;
            ws.Cell(row, 4).Value = t.OutcomeSummary;
            ws.Cell(row, 5).Value = t.RoutedToArea;
            ws.Cell(row, 6).Value = t.DecidedAt;
            ws.Cell(row, 7).Value = t.DecidedBy;
            ws.Cell(row, 8).Value = t.CreatedAt;
            row++;
        }
        ws.Columns().AdjustToContents();
    }

    private static void WriteScorecardsSheet(IXLWorksheet ws, List<DemandScorecard> list)
    {
        var headers = new[] { "Id", "DemandTriageRequestId", "ScorecardStatus", "TotalScore", "SuggestionBand", "FinalisedAt", "CreatedAt", "UpdatedAt" };
        for (var c = 0; c < headers.Length; c++)
            ws.Cell(1, c + 1).Value = headers[c];
        ws.Row(1).Style.Font.Bold = true;
        var row = 2;
        foreach (var s in list)
        {
            ws.Cell(row, 1).Value = s.Id;
            ws.Cell(row, 2).Value = s.DemandTriageRequestId;
            ws.Cell(row, 3).Value = s.ScorecardStatus;
            ws.Cell(row, 4).Value = s.TotalScore;
            ws.Cell(row, 5).Value = s.SuggestionBand;
            ws.Cell(row, 6).Value = s.FinalisedAt;
            ws.Cell(row, 7).Value = s.CreatedAt;
            ws.Cell(row, 8).Value = s.UpdatedAt;
            row++;
        }
        ws.Columns().AdjustToContents();
    }

    private static void WriteExploratoryReviewsSheet(IXLWorksheet ws, List<DemandExploratoryReview> list)
    {
        var headers = new[] { "Id", "DemandTriageRequestId", "RecommendationToProceed", "CompletedAt", "CompletedBy", "CreatedAt", "UpdatedAt" };
        for (var c = 0; c < headers.Length; c++)
            ws.Cell(1, c + 1).Value = headers[c];
        ws.Row(1).Style.Font.Bold = true;
        var row = 2;
        foreach (var r in list)
        {
            ws.Cell(row, 1).Value = r.Id;
            ws.Cell(row, 2).Value = r.DemandTriageRequestId;
            ws.Cell(row, 3).Value = r.RecommendationToProceed.HasValue ? (r.RecommendationToProceed.Value ? "Yes" : "No") : "";
            ws.Cell(row, 4).Value = r.CompletedAt;
            ws.Cell(row, 5).Value = r.CompletedBy;
            ws.Cell(row, 6).Value = r.CreatedAt;
            ws.Cell(row, 7).Value = r.UpdatedAt;
            row++;
        }
        ws.Columns().AdjustToContents();
    }

    private static void WriteCommissionsSheet(IXLWorksheet ws, List<Commission> list)
    {
        var headers = new[] { "Id", "Name", "Quarter", "StartDate", "EndDate", "OpenDate", "DueDate", "IsActive", "InScopePhases", "InScopeTypes", "IncludedPerformanceMetricIds", "CreatedAt", "UpdatedAt" };
        for (var c = 0; c < headers.Length; c++)
            ws.Cell(1, c + 1).Value = headers[c];
        ws.Row(1).Style.Font.Bold = true;
        var row = 2;
        foreach (var item in list)
        {
            ws.Cell(row, 1).Value = item.Id;
            ws.Cell(row, 2).Value = item.Name;
            ws.Cell(row, 3).Value = item.Quarter;
            ws.Cell(row, 4).Value = item.StartDate;
            ws.Cell(row, 5).Value = item.EndDate;
            ws.Cell(row, 6).Value = item.OpenDate;
            ws.Cell(row, 7).Value = item.DueDate;
            ws.Cell(row, 8).Value = item.IsActive;
            ws.Cell(row, 9).Value = item.InScopePhases;
            ws.Cell(row, 10).Value = item.InScopeTypes;
            ws.Cell(row, 11).Value = item.IncludedPerformanceMetricIds;
            ws.Cell(row, 12).Value = item.CreatedAt;
            ws.Cell(row, 13).Value = item.UpdatedAt;
            row++;
        }
        ws.SheetView.FreezeRows(1);
        ws.Columns(1, 13).AdjustToContents();
    }

    private static void WriteCommissionSubmissionsSheet(IXLWorksheet ws, List<CommissionSubmission> list)
    {
        var headers = new[] { "Id", "CommissionId", "ProductDocumentId", "FipsId", "ProductTitle", "Status", "SubmittedDate", "SubmittedBy", "Comments", "CreatedAt", "UpdatedAt" };
        for (var c = 0; c < headers.Length; c++)
            ws.Cell(1, c + 1).Value = headers[c];
        ws.Row(1).Style.Font.Bold = true;
        var row = 2;
        foreach (var item in list)
        {
            ws.Cell(row, 1).Value = item.Id;
            ws.Cell(row, 2).Value = item.CommissionId;
            ws.Cell(row, 3).Value = item.ProductDocumentId;
            ws.Cell(row, 4).Value = item.FipsId;
            ws.Cell(row, 5).Value = item.ProductTitle;
            ws.Cell(row, 6).Value = item.Status.ToString();
            ws.Cell(row, 7).Value = item.SubmittedDate;
            ws.Cell(row, 8).Value = item.SubmittedBy;
            ws.Cell(row, 9).Value = item.Comments;
            ws.Cell(row, 10).Value = item.CreatedAt;
            ws.Cell(row, 11).Value = item.UpdatedAt;
            row++;
        }
        ws.SheetView.FreezeRows(1);
        ws.Columns(1, 11).AdjustToContents();
    }

    private static void WritePerformanceMetricsSheet(IXLWorksheet ws, List<PerformanceMetric> list)
    {
        var headers = new[] { "Id", "Identifier", "Title", "ValueType", "ValidFromYear", "ValidFromMonth", "ApplicablePhases", "ApplicableTypes", "IsDisabled", "ConditionalOnMetricId", "CreatedAt", "UpdatedAt" };
        for (var c = 0; c < headers.Length; c++)
            ws.Cell(1, c + 1).Value = headers[c];
        ws.Row(1).Style.Font.Bold = true;
        var row = 2;
        foreach (var item in list)
        {
            ws.Cell(row, 1).Value = item.Id;
            ws.Cell(row, 2).Value = item.Identifier;
            ws.Cell(row, 3).Value = item.Title;
            ws.Cell(row, 4).Value = item.ValueType.ToString();
            ws.Cell(row, 5).Value = item.ValidFromYear;
            ws.Cell(row, 6).Value = item.ValidFromMonth;
            ws.Cell(row, 7).Value = item.ApplicablePhases;
            ws.Cell(row, 8).Value = item.ApplicableTypes;
            ws.Cell(row, 9).Value = item.IsDisabled;
            ws.Cell(row, 10).Value = item.ConditionalOnMetricId;
            ws.Cell(row, 11).Value = item.CreatedAt;
            ws.Cell(row, 12).Value = item.UpdatedAt;
            row++;
        }
        ws.SheetView.FreezeRows(1);
        ws.Columns(1, 12).AdjustToContents();
    }

    private static void WriteCommissionMetricValuesSheet(IXLWorksheet ws, List<CommissionMetricValue> list, Dictionary<int, string> metricNameById)
    {
        var headers = new[] { "Id", "CommissionSubmissionId", "PerformanceMetricId", "PerformanceMetricIdentifier", "Value", "IsComplete", "IsNotCaptured", "NotCapturedReason", "ReasonForDifference", "CreatedAt", "UpdatedAt" };
        for (var c = 0; c < headers.Length; c++)
            ws.Cell(1, c + 1).Value = headers[c];
        ws.Row(1).Style.Font.Bold = true;
        var row = 2;
        foreach (var item in list)
        {
            ws.Cell(row, 1).Value = item.Id;
            ws.Cell(row, 2).Value = item.CommissionSubmissionId;
            ws.Cell(row, 3).Value = item.PerformanceMetricId;
            ws.Cell(row, 4).Value = metricNameById.TryGetValue(item.PerformanceMetricId, out var metricName) ? metricName : "";
            ws.Cell(row, 5).Value = item.Value;
            ws.Cell(row, 6).Value = item.IsComplete;
            ws.Cell(row, 7).Value = item.IsNotCaptured;
            ws.Cell(row, 8).Value = item.NotCapturedReason;
            ws.Cell(row, 9).Value = item.ReasonForDifference;
            ws.Cell(row, 10).Value = item.CreatedAt;
            ws.Cell(row, 11).Value = item.UpdatedAt;
            row++;
        }
        ws.SheetView.FreezeRows(1);
        ws.Columns(1, 11).AdjustToContents();
    }

    /// <summary>CSV export of pipeline demand requests (modern demand register).</summary>
    [HttpGet]
    public async Task<IActionResult> DownloadDemandRegisterCsv(CancellationToken cancellationToken = default)
    {
        if (!IsDemandGloballyActive())
            return NotFound();

        var rows = await _db.DemandPipelineRequests.AsNoTracking()
            .OrderByDescending(d => d.CreatedAt)
            .ToListAsync(cancellationToken);
        var sb = new StringBuilder();
        sb.AppendLine("Reference,Title,Department,SRO,Status,Score,Band,TriageOutcome,Submitted");
        foreach (var d in rows)
        {
            sb.AppendLine(string.Join(",", new[]
            {
                CsvEscape(d.Reference),
                CsvEscape(d.Title),
                CsvEscape(d.DepartmentGroup),
                CsvEscape(d.Sro),
                CsvEscape(d.Status),
                d.TotalScore?.ToString() ?? "",
                CsvEscape(d.SuggestedBand),
                CsvEscape(d.TriageOutcome),
                d.SubmittedDate?.ToString("yyyy-MM-dd") ?? ""
            }));
        }
        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        var fileName = $"Compass-demand-register-{DateTime.UtcNow:yyyyMMdd-HHmm}.csv";
        return File(bytes, "text/csv;charset=utf-8", fileName);
    }

    private static string CsvEscape(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (s.Contains(',') || s.Contains('"') || s.Contains('\n') || s.Contains('\r'))
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        return s;
    }
}
