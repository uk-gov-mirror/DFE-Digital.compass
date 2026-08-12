using System.Globalization;
using ClosedXML.Excel;
using Compass.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Compass.Controllers.Modern;

/// <summary>Full RAID register Excel export (multi-sheet workbook).</summary>
public partial class ModernRaidController
{
    [HttpGet("export/raid-register.xlsx")]
    public async Task<IActionResult> ExportRaidRegisterExcel(CancellationToken cancellationToken = default)
    {
        var bytes = await BuildRaidRegisterExcelWorkbookAsync(cancellationToken);
        return File(
            bytes,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"raid-register-full-{DateTime.UtcNow:yyyyMMddHHmmss}.xlsx");
    }

    [HttpGet("export/risks.xlsx")]
    public async Task<IActionResult> ExportRisksExcel(CancellationToken cancellationToken = default)
    {
        var bytes = await BuildRisksExcelWorkbookAsync(cancellationToken);
        return File(
            bytes,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"risks-register-{DateTime.UtcNow:yyyyMMddHHmmss}.xlsx");
    }

    private async Task<byte[]> BuildRisksExcelWorkbookAsync(CancellationToken cancellationToken)
    {
        var risks = await LoadRisksForExcelExportAsync(cancellationToken);
        using var wb = new XLWorkbook();
        WriteRisksExcelWorksheet(wb.Worksheets.Add("Risks"), risks);
        await using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    private async Task<List<Risk>> LoadRisksForExcelExportAsync(CancellationToken cancellationToken) =>
        await _db.Risks.AsNoTracking()
            .Where(r => !r.IsDeleted)
            .Include(r => r.Project)
            .Include(r => r.PrimaryProduct)
            .Include(r => r.RiskTier)
            .Include(r => r.RiskStatus)
            .Include(r => r.RiskPriority)
            .Include(r => r.Likelihood)
            .Include(r => r.ImpactLevel)
            .Include(r => r.Proximity)
            .Include(r => r.RiskCategory)
            .Include(r => r.GovernanceBoard)
            .Include(r => r.OwnerUser)
            .Include(r => r.SroUser)
            .Include(r => r.CreatedByUser)
            .Include(r => r.UpdatedByUser)
            .Include(r => r.ClosedByUser)
            .Include(r => r.RiskBusinessAreas).ThenInclude(x => x.BusinessAreaLookup)
            .Include(r => r.RiskDivisions).ThenInclude(x => x.Division)
            .Include(r => r.RiskRiskCategories).ThenInclude(x => x.RiskCategory)
            .Include(r => r.RiskRiskTypes).ThenInclude(x => x.RiskType)
            .Include(r => r.Tags)
            .Include(r => r.KeyRiskIndicators)
            .Include(r => r.RiskActions).ThenInclude(ra => ra.Action)
            .OrderBy(r => r.Id)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);

    private async Task<byte[]> BuildRaidRegisterExcelWorkbookAsync(CancellationToken cancellationToken)
    {
        static string? UserDisp(User? u) => u == null ? null : (u.Name ?? u.Email);

        var risks = await LoadRisksForExcelExportAsync(cancellationToken);

        var issues = await _db.Issues.AsNoTracking()
            .Where(i => !i.IsDeleted)
            .Include(i => i.Project)
            .Include(i => i.PrimaryProduct)
            .Include(i => i.StatusLookup)
            .Include(i => i.PriorityLookup)
            .Include(i => i.SeverityLookup)
            .Include(i => i.CategoryLookup)
            .Include(i => i.OwnerUser)
            .Include(i => i.SroUser)
            .Include(i => i.SourceRisk)
            .Include(i => i.Risk)
            .Include(i => i.Milestone)
            .Include(i => i.CreatedByUser)
            .Include(i => i.UpdatedByUser)
            .Include(i => i.ClosedByUser)
            .Include(i => i.Tags)
            .Include(i => i.IssueBusinessAreas).ThenInclude(x => x.BusinessAreaLookup)
            .Include(i => i.IssueDivisions).ThenInclude(x => x.Division)
            .Include(i => i.IssueIssueCategories).ThenInclude(x => x.IssueCategory)
            .Include(i => i.IssueActions).ThenInclude(ia => ia.Action)
            .OrderBy(i => i.Id)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);

        var dependencies = await _db.Dependencies.AsNoTracking()
            .Include(d => d.LinkTypeLookup)
            .Include(d => d.CriticalityLookup)
            .Include(d => d.OwnerUser)
            .OrderBy(d => d.Id)
            .ToListAsync(cancellationToken);

        var depTitleMap = dependencies.Count > 0
            ? await BuildDependencyEndpointTitleMapAsync(dependencies, cancellationToken)
            : new Dictionary<(string, int), string>();

        string DepTitle(string entityType, int id)
        {
            var key = (NormalizeRaidEntityType(entityType), id);
            return depTitleMap.TryGetValue(key, out var t) ? t : $"{entityType.Trim()} #{id}";
        }

        var assumptions = await _db.Assumptions.AsNoTracking()
            .Where(a => !a.IsDeleted)
            .Include(a => a.Project)
            .Include(a => a.PrimaryProduct)
            .Include(a => a.OwnerUser)
            .Include(a => a.SroUser)
            .Include(a => a.CriticalityLookup)
            .Include(a => a.StatusLookup)
            .Include(a => a.AssumptionDivisions).ThenInclude(x => x.Division)
            .Include(a => a.AssumptionBusinessAreas).ThenInclude(x => x.BusinessAreaLookup)
            .OrderBy(a => a.Id)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);

        using var wb = new XLWorkbook();

        var ci = CultureInfo.InvariantCulture;

        WriteRisksExcelWorksheet(wb.Worksheets.Add("Risks"), risks);

        // --- Issues ---
        var wsI = wb.Worksheets.Add("Issues");
        var issueHeaders = new[]
        {
            "Id", "Reference", "Title", "Description", "DetailedCause", "AssuranceArrangements", "LegacyCategory",
            "LegacyBusinessArea", "LegacyStatus", "LegacySeverity", "LegacyPriority", "Status (lookup)",
            "Priority (lookup)", "Severity (lookup)", "Primary category (lookup)", "DetectedDate", "TargetResolutionDate",
            "ResolvedDate", "ClosedDate", "BlocksRelease", "UserImpactSummary", "ServiceImpactSummary", "ResolutionSummary",
            "Workaround", "SourceType", "SourceReference", "SourceRecordUrl", "SourceRiskId", "Source risk title",
            "LinkedRiskId", "Linked risk title", "MilestoneId", "ProjectId", "Work item", "PrimaryProductId",
            "Primary product", "RaidAssociationKind", "FipsId", "ProductDocumentId", "Source", "SourceId", "Owner",
            "SRO", "Tags", "Categories (multi)", "Divisions (multi)", "Business areas (multi)", "Linked actions",
            "CreatedBy", "UpdatedBy", "ClosedBy", "CreatedAt", "UpdatedAt"
        };
        for (var h = 0; h < issueHeaders.Length; h++)
            wsI.Cell(1, h + 1).Value = issueHeaders[h];
        var ir = 2;
        foreach (var i in issues)
        {
            var tags = string.Join("; ", i.Tags.Select(t => t.Value));
            var cats = string.Join("; ", i.IssueIssueCategories.Select(x => x.IssueCategory.Label));
            var divs = string.Join("; ", i.IssueDivisions.Select(x => x.Division.Name));
            var bas = string.Join("; ", i.IssueBusinessAreas.Select(x => x.BusinessAreaLookup.Name));
            var acts = string.Join("; ", i.IssueActions.Where(ia => ia.Action != null && !ia.Action.IsDeleted)
                .Select(ia => ia.Action!.Title));

            wsI.Cell(ir, 1).Value = i.Id;
            wsI.Cell(ir, 2).Value = $"I-{i.Id:D4}";
            wsI.Cell(ir, 3).Value = i.Title;
            wsI.Cell(ir, 4).Value = i.Description;
            wsI.Cell(ir, 5).Value = i.DetailedCause;
            wsI.Cell(ir, 6).Value = i.AssuranceArrangements;
            wsI.Cell(ir, 7).Value = i.Category;
            wsI.Cell(ir, 8).Value = i.BusinessArea;
            wsI.Cell(ir, 9).Value = i.Status;
            wsI.Cell(ir, 10).Value = i.Severity;
            wsI.Cell(ir, 11).Value = i.Priority;
            wsI.Cell(ir, 12).Value = i.StatusLookup?.Label;
            wsI.Cell(ir, 13).Value = i.PriorityLookup?.Label ?? i.Priority;
            wsI.Cell(ir, 14).Value = i.SeverityLookup?.Label ?? i.Severity;
            wsI.Cell(ir, 15).Value = i.CategoryLookup?.Label;
            wsI.Cell(ir, 16).Value = i.DetectedDate.ToString("u", ci);
            wsI.Cell(ir, 17).Value = i.TargetResolutionDate?.ToString("u", ci);
            wsI.Cell(ir, 18).Value = i.ResolvedDate?.ToString("u", ci);
            wsI.Cell(ir, 19).Value = i.ClosedDate?.ToString("u", ci);
            wsI.Cell(ir, 20).Value = i.BlockedFlag ? "Yes" : "No";
            wsI.Cell(ir, 21).Value = i.UserImpactSummary;
            wsI.Cell(ir, 22).Value = i.ServiceImpactSummary;
            wsI.Cell(ir, 23).Value = i.ResolutionSummary;
            wsI.Cell(ir, 24).Value = i.Workaround;
            wsI.Cell(ir, 25).Value = i.SourceType;
            wsI.Cell(ir, 26).Value = i.SourceReference;
            wsI.Cell(ir, 27).Value = i.SourceRecordUrl;
            wsI.Cell(ir, 28).Value = i.SourceRiskId;
            wsI.Cell(ir, 29).Value = i.SourceRisk?.Title;
            wsI.Cell(ir, 30).Value = i.RiskId;
            wsI.Cell(ir, 31).Value = i.Risk?.Title;
            wsI.Cell(ir, 32).Value = i.MilestoneId;
            wsI.Cell(ir, 33).Value = i.ProjectId;
            wsI.Cell(ir, 34).Value = i.Project?.Title;
            wsI.Cell(ir, 35).Value = i.PrimaryProductId;
            wsI.Cell(ir, 36).Value = i.PrimaryProduct != null
                ? (i.PrimaryProduct.DisplayName ?? i.PrimaryProduct.FipsId)
                : null;
            wsI.Cell(ir, 37).Value = i.RaidAssociationKind;
            wsI.Cell(ir, 38).Value = i.FipsId;
            wsI.Cell(ir, 39).Value = i.ProductDocumentId;
            wsI.Cell(ir, 40).Value = i.Source;
            wsI.Cell(ir, 41).Value = i.SourceId;
            wsI.Cell(ir, 42).Value = UserDisp(i.OwnerUser);
            wsI.Cell(ir, 43).Value = UserDisp(i.SroUser);
            wsI.Cell(ir, 44).Value = tags;
            wsI.Cell(ir, 45).Value = cats;
            wsI.Cell(ir, 46).Value = divs;
            wsI.Cell(ir, 47).Value = bas;
            wsI.Cell(ir, 48).Value = acts;
            wsI.Cell(ir, 49).Value = UserDisp(i.CreatedByUser);
            wsI.Cell(ir, 50).Value = UserDisp(i.UpdatedByUser);
            wsI.Cell(ir, 51).Value = UserDisp(i.ClosedByUser);
            wsI.Cell(ir, 52).Value = i.CreatedAt.ToString("u", ci);
            wsI.Cell(ir, 53).Value = i.UpdatedAt.ToString("u", ci);
            ir++;
        }

        wsI.Row(1).Style.Font.Bold = true;
        if (ir > 2)
            wsI.Range(1, 1, ir - 1, issueHeaders.Length).SetAutoFilter();
        wsI.SheetView.FreezeRows(1);
        wsI.Columns().AdjustToContents(1, 1, 80, 120);

        // --- Dependencies ---
        var wsD = wb.Worksheets.Add("Dependencies");
        var depHeaders = new[]
        {
            "Id", "SourceEntityType", "SourceEntityId", "Source title", "TargetEntityType", "TargetEntityId",
            "Target title", "DependencyType (legacy)", "Link type (lookup)", "Criticality (lookup)", "Owner",
            "Organisation", "DueDate", "Description", "Status", "ResolvedDate", "ResolvedByEmail", "ResolvedByName",
            "CreatedAt", "UpdatedAt"
        };
        for (var h = 0; h < depHeaders.Length; h++)
            wsD.Cell(1, h + 1).Value = depHeaders[h];
        var dr = 2;
        foreach (var d in dependencies)
        {
            wsD.Cell(dr, 1).Value = d.Id;
            wsD.Cell(dr, 2).Value = d.SourceEntityType;
            wsD.Cell(dr, 3).Value = d.SourceEntityId;
            wsD.Cell(dr, 4).Value = DepTitle(d.SourceEntityType, d.SourceEntityId);
            wsD.Cell(dr, 5).Value = d.TargetEntityType;
            wsD.Cell(dr, 6).Value = d.TargetEntityId;
            wsD.Cell(dr, 7).Value = DepTitle(d.TargetEntityType, d.TargetEntityId);
            wsD.Cell(dr, 8).Value = d.DependencyType;
            wsD.Cell(dr, 9).Value = d.LinkTypeLookup?.Label;
            wsD.Cell(dr, 10).Value = d.CriticalityLookup?.Label;
            wsD.Cell(dr, 11).Value = UserDisp(d.OwnerUser);
            wsD.Cell(dr, 12).Value = d.Organisation;
            wsD.Cell(dr, 13).Value = d.DueDate?.ToString("u", ci);
            wsD.Cell(dr, 14).Value = d.Description;
            wsD.Cell(dr, 15).Value = d.Status;
            wsD.Cell(dr, 16).Value = d.ResolvedDate?.ToString("u", ci);
            wsD.Cell(dr, 17).Value = d.ResolvedByEmail;
            wsD.Cell(dr, 18).Value = d.ResolvedByName;
            wsD.Cell(dr, 19).Value = d.CreatedAt.ToString("u", ci);
            wsD.Cell(dr, 20).Value = d.UpdatedAt.ToString("u", ci);
            dr++;
        }

        wsD.Row(1).Style.Font.Bold = true;
        if (dr > 2)
            wsD.Range(1, 1, dr - 1, depHeaders.Length).SetAutoFilter();
        wsD.SheetView.FreezeRows(1);
        wsD.Columns().AdjustToContents(1, 1, 80, 120);

        // --- Assumptions ---
        var wsA = wb.Worksheets.Add("Assumptions");
        var asmHeaders = new[]
        {
            "Id", "Description", "ValidationOutcome", "ReviewDate", "ProjectId", "Work item", "PrimaryProductId",
            "Primary product", "RaidAssociationKind", "Owner", "SRO", "Criticality (lookup)", "Status (lookup)",
            "Divisions (multi)", "Business areas (multi)", "CreatedAt", "UpdatedAt"
        };
        for (var h = 0; h < asmHeaders.Length; h++)
            wsA.Cell(1, h + 1).Value = asmHeaders[h];
        var ar = 2;
        foreach (var a in assumptions)
        {
            var divs = string.Join("; ", a.AssumptionDivisions.Select(x => x.Division.Name));
            var bas = string.Join("; ", a.AssumptionBusinessAreas.Select(x => x.BusinessAreaLookup.Name));

            wsA.Cell(ar, 1).Value = a.Id;
            wsA.Cell(ar, 2).Value = a.Description;
            wsA.Cell(ar, 3).Value = a.ValidationOutcome;
            wsA.Cell(ar, 4).Value = a.ReviewDate?.ToString("u", ci);
            wsA.Cell(ar, 5).Value = a.ProjectId;
            wsA.Cell(ar, 6).Value = a.Project?.Title;
            wsA.Cell(ar, 7).Value = a.PrimaryProductId;
            wsA.Cell(ar, 8).Value = a.PrimaryProduct != null
                ? (a.PrimaryProduct.DisplayName ?? a.PrimaryProduct.FipsId)
                : null;
            wsA.Cell(ar, 9).Value = a.RaidAssociationKind;
            wsA.Cell(ar, 10).Value = UserDisp(a.OwnerUser);
            wsA.Cell(ar, 11).Value = UserDisp(a.SroUser);
            wsA.Cell(ar, 12).Value = a.CriticalityLookup?.Label;
            wsA.Cell(ar, 13).Value = a.StatusLookup?.Label;
            wsA.Cell(ar, 14).Value = divs;
            wsA.Cell(ar, 15).Value = bas;
            wsA.Cell(ar, 16).Value = a.CreatedAt.ToString("u", ci);
            wsA.Cell(ar, 17).Value = a.UpdatedAt.ToString("u", ci);
            ar++;
        }

        wsA.Row(1).Style.Font.Bold = true;
        if (ar > 2)
            wsA.Range(1, 1, ar - 1, asmHeaders.Length).SetAutoFilter();
        wsA.SheetView.FreezeRows(1);
        wsA.Columns().AdjustToContents(1, 1, 80, 120);

        await using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    private static void WriteRisksExcelWorksheet(IXLWorksheet wsR, List<Risk> risks)
    {
        static string? UserDisp(User? u) => u == null ? null : (u.Name ?? u.Email);
        var ci = CultureInfo.InvariantCulture;

        var riskHeaders = new[]
        {
            "Id", "Reference", "Title", "Description", "Notes", "LegacyCategory", "LegacyBusinessArea", "LegacyStatus",
            "Status (lookup)", "Tier", "Priority", "Likelihood (lookup)", "Impact level (lookup)", "Proximity (lookup)",
            "Primary category (lookup)", "ImpactRating", "LikelihoodRating", "RiskScore", "InherentScore", "ResidualScore",
            "ResidualImpact", "ResidualLikelihood", "Response (legacy)", "ResponseStrategy", "ProximityDate",
            "IdentifiedDate", "NextReviewDate", "LastReviewDate", "ClosedDate", "TargetDate", "OwnerEmail",
            "Owner", "SRO", "ProjectId", "Work item", "PrimaryProductId", "Primary product", "RaidAssociationKind",
            "FipsId", "ProductDocumentId", "Source", "SourceId", "GovernanceBoard", "HowIdentified", "Cause",
            "ImpactIfRealised", "Tags", "Risk types", "Categories (multi)", "Divisions (multi)", "Business areas (multi)",
            "Mitigation actions", "Key risk indicators", "CreatedBy", "UpdatedBy", "ClosedBy", "CreatedAt", "UpdatedAt"
        };
        for (var h = 0; h < riskHeaders.Length; h++)
            wsR.Cell(1, h + 1).Value = riskHeaders[h];

        var rr = 2;
        foreach (var r in risks)
        {
            var statusLab = r.RiskStatus?.Label ?? r.Status;
            var tags = string.Join("; ", r.Tags.Select(t => t.Value));
            var types = string.Join("; ", r.RiskRiskTypes.Select(x => x.RiskType.Name));
            var cats = string.Join("; ", r.RiskRiskCategories.Select(x => x.RiskCategory.Label));
            var divs = string.Join("; ", r.RiskDivisions.Select(x => x.Division.Name));
            var bas = string.Join("; ", r.RiskBusinessAreas.Select(x => x.BusinessAreaLookup.Name));
            var mitig = string.Join("; ", r.RiskActions.Where(ra => ra.Action != null && !ra.Action.IsDeleted)
                .Select(ra => ra.Action!.Title));
            var kris = string.Join(" | ", r.KeyRiskIndicators.OrderBy(k => k.SortOrder).Select(k =>
                $"{k.Title}{(string.IsNullOrEmpty(k.Metric) ? "" : $" ({k.Metric})")}"));

            wsR.Cell(rr, 1).Value = r.Id;
            wsR.Cell(rr, 2).Value = $"R-{r.Id:D4}";
            wsR.Cell(rr, 3).Value = r.Title;
            wsR.Cell(rr, 4).Value = r.Description;
            wsR.Cell(rr, 5).Value = r.Notes;
            wsR.Cell(rr, 6).Value = r.Category;
            wsR.Cell(rr, 7).Value = r.BusinessArea;
            wsR.Cell(rr, 8).Value = r.Status;
            wsR.Cell(rr, 9).Value = statusLab;
            wsR.Cell(rr, 10).Value = r.RiskTier?.Name;
            wsR.Cell(rr, 11).Value = r.RiskPriority?.Label;
            wsR.Cell(rr, 12).Value = r.Likelihood?.Label;
            wsR.Cell(rr, 13).Value = r.ImpactLevel?.Label;
            wsR.Cell(rr, 14).Value = r.Proximity?.Label;
            wsR.Cell(rr, 15).Value = r.RiskCategory?.Label;
            wsR.Cell(rr, 16).Value = r.ImpactRating;
            wsR.Cell(rr, 17).Value = r.LikelihoodRating;
            wsR.Cell(rr, 18).Value = r.RiskScore;
            wsR.Cell(rr, 19).Value = r.InherentScore;
            wsR.Cell(rr, 20).Value = r.ResidualScore;
            wsR.Cell(rr, 21).Value = r.ResidualImpact;
            wsR.Cell(rr, 22).Value = r.ResidualLikelihood;
            wsR.Cell(rr, 23).Value = r.Response;
            wsR.Cell(rr, 24).Value = r.ResponseStrategy;
            wsR.Cell(rr, 25).Value = r.ProximityDate?.ToString("u", ci);
            wsR.Cell(rr, 26).Value = r.IdentifiedDate?.ToString("u", ci);
            wsR.Cell(rr, 27).Value = r.NextReviewDate?.ToString("u", ci);
            wsR.Cell(rr, 28).Value = r.LastReviewDate?.ToString("u", ci);
            wsR.Cell(rr, 29).Value = r.ClosedDate?.ToString("u", ci);
            wsR.Cell(rr, 30).Value = r.TargetDate?.ToString("u", ci);
            wsR.Cell(rr, 31).Value = r.OwnerEmail;
            wsR.Cell(rr, 32).Value = UserDisp(r.OwnerUser) ?? r.OwnerEmail;
            wsR.Cell(rr, 33).Value = UserDisp(r.SroUser);
            wsR.Cell(rr, 34).Value = r.ProjectId;
            wsR.Cell(rr, 35).Value = r.Project?.Title;
            wsR.Cell(rr, 36).Value = r.PrimaryProductId;
            wsR.Cell(rr, 37).Value = r.PrimaryProduct != null
                ? (r.PrimaryProduct.DisplayName ?? r.PrimaryProduct.FipsId)
                : null;
            wsR.Cell(rr, 38).Value = r.RaidAssociationKind;
            wsR.Cell(rr, 39).Value = r.FipsId;
            wsR.Cell(rr, 40).Value = r.ProductDocumentId;
            wsR.Cell(rr, 41).Value = r.Source;
            wsR.Cell(rr, 42).Value = r.SourceId;
            wsR.Cell(rr, 43).Value = r.GovernanceBoard?.Label;
            wsR.Cell(rr, 44).Value = r.HowIdentified;
            wsR.Cell(rr, 45).Value = r.Cause;
            wsR.Cell(rr, 46).Value = r.ImpactIfRealised;
            wsR.Cell(rr, 47).Value = tags;
            wsR.Cell(rr, 48).Value = types;
            wsR.Cell(rr, 49).Value = cats;
            wsR.Cell(rr, 50).Value = divs;
            wsR.Cell(rr, 51).Value = bas;
            wsR.Cell(rr, 52).Value = mitig;
            wsR.Cell(rr, 53).Value = kris;
            wsR.Cell(rr, 54).Value = UserDisp(r.CreatedByUser);
            wsR.Cell(rr, 55).Value = UserDisp(r.UpdatedByUser);
            wsR.Cell(rr, 56).Value = UserDisp(r.ClosedByUser);
            wsR.Cell(rr, 57).Value = r.CreatedAt.ToString("u", ci);
            wsR.Cell(rr, 58).Value = r.UpdatedAt.ToString("u", ci);
            rr++;
        }

        wsR.Row(1).Style.Font.Bold = true;
        if (rr > 2)
            wsR.Range(1, 1, rr - 1, riskHeaders.Length).SetAutoFilter();
        wsR.SheetView.FreezeRows(1);
        wsR.Columns().AdjustToContents(1, 1, 80, 120);
    }
}
