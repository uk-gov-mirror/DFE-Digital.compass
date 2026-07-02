using System.Globalization;
using ClosedXML.Excel;
using Compass.Models;
using Compass.Models.Modern.Work;
using Compass.Services;
using Compass.Services.Modern;
using Compass.ViewModels;
using Compass.ViewModels.Modern;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Compass.Controllers.Modern;

public partial class ModernReportingController
{
    /// <summary>Standard work export: Work, Milestones, Updates, Risks, Issues, Assumptions, Decisions, Near misses, Accessibility.</summary>
    [HttpGet("work/export")]
    public async Task<IActionResult> ExportWorkScope(
        [FromQuery] int[]? ids,
        string? label,
        CancellationToken cancellationToken = default)
    {
        var projectIds = (ids ?? Array.Empty<int>()).Distinct().Where(id => id > 0).ToList();
        if (projectIds.Count == 0)
            return BadRequest("No work items to export.");

        var user = await GetCurrentUserAsync(cancellationToken);
        if (user == null)
            return Unauthorized();

        var bytes = await _workScopedExcelExport.BuildWorkbookAsync(
            projectIds,
            user.Value.CurrentUser,
            user.Value.Email,
            Url,
            cancellationToken);

        var safeLabel = SanitizeFilePart(string.IsNullOrWhiteSpace(label) ? "work" : label);
        return ReturnExcelFile(bytes, $"work-export-{safeLabel}-{Timestamp()}.xlsx");
    }

    [HttpGet("drilldown/export")]
    public async Task<IActionResult> ExportDrilldown(
        string source,
        int? year,
        int? month,
        int? isoYear,
        int? isoWeek,
        string? dimension,
        string? ba,
        string? filter,
        int? businessAreaId,
        int? directorateId,
        int? groupId,
        CancellationToken cancellationToken = default)
    {
        var projectIds = await ResolveDrilldownProjectIdsAsync(
            source,
            year,
            month,
            isoYear,
            isoWeek,
            dimension,
            ba,
            filter,
            businessAreaId,
            directorateId,
            groupId,
            cancellationToken);
        if (projectIds.Count == 0)
            return BadRequest("No work items match this drill-down.");

        var user = await GetCurrentUserAsync(cancellationToken);
        if (user == null)
            return Unauthorized();

        var bytes = await _workScopedExcelExport.BuildWorkbookAsync(
            projectIds,
            user.Value.CurrentUser,
            user.Value.Email,
            Url,
            cancellationToken);

        var safeFilter = SanitizeFilePart(string.IsNullOrWhiteSpace(filter) ? "items" : filter);
        var safeBa = SanitizeFilePart(string.IsNullOrWhiteSpace(ba) ? "scope" : ba);
        return ReturnExcelFile(bytes, $"reporting-drilldown-{source}-{safeBa}-{safeFilter}-{Timestamp()}.xlsx");
    }

    private static string SanitizeFilePart(string value)
    {
        var cleaned = string.Concat(value.Select(c =>
            char.IsLetterOrDigit(c) || c == '-' ? c : '-'));
        return string.IsNullOrWhiteSpace(cleaned) ? "export" : cleaned;
    }

    private async Task<List<int>> ResolveDrilldownProjectIdsAsync(
        string? source,
        int? year,
        int? month,
        int? isoYear,
        int? isoWeek,
        string? dimension,
        string? ba,
        string? filter,
        int? businessAreaId,
        int? directorateId,
        int? groupId,
        CancellationToken cancellationToken)
    {
        var src = (source ?? "").Trim().ToLowerInvariant();
        var filterKey = (filter ?? "total").Trim();
        List<BusinessAreaProjectItem> sourceItems;

        if (src == "thematic")
        {
            var dashboard = await _monthlyReportService.BuildThematicReportDashboardAsync(cancellationToken);
            var dim = string.IsNullOrWhiteSpace(dimension) ? "theme" : dimension;
            sourceItems = ResolveDrillSourceItems(dashboard.Rows, dashboard.ScopeProjectItems, dim, ba);
        }
        else if (src == "priorities")
        {
            var pr = await _monthlyReportService.BuildPrioritiesReportAsync(
                dimension,
                year,
                month,
                groupId,
                cancellationToken);
            var rows = pr.Report.PrioritiesReport?.DimensionSections
                .SelectMany(s => s.Rows)
                .ToList() ?? new List<ModernBusinessAreaDashboardRow>();
            sourceItems = ResolveDrillSourceItems(rows, pr.Report.ScopeProjectItems, dimension, ba);
        }
        else if (src == "resourcing")
        {
            var rr = await _monthlyReportService.BuildResourcingReportAsync(
                year,
                month,
                businessAreaId,
                directorateId,
                dimension,
                groupId,
                cancellationToken);
            sourceItems = ResolveResourcingDrillSourceItems(rr, dimension, ba);
        }
        else if (src == "weekly")
        {
            var model = await _monthlyReportService.BuildWeeklyDashboardAsync(
                isoYear,
                isoWeek,
                businessAreaId,
                directorateId,
                cancellationToken);
            sourceItems = ResolveDrillSourceItems(model.BusinessAreaRows, model.ScopeProjectItems, dimension, ba);
        }
        else
        {
            var model = await _monthlyReportService.BuildDashboardAsync(
                year,
                month,
                businessAreaId,
                directorateId,
                cancellationToken: cancellationToken);
            sourceItems = ResolveDrillSourceItems(model.BusinessAreaRows, model.ScopeProjectItems, dimension, ba);
        }

        return ModernMonthlyReportService.FilterDrilldownItems(sourceItems, filterKey)
            .Select(p => p.Id)
            .Distinct()
            .ToList();
    }

    private static List<BusinessAreaProjectItem> ResolveDrillSourceItems(
        IEnumerable<ModernBusinessAreaDashboardRow> rows,
        IReadOnlyList<BusinessAreaProjectItem> scopeProjects,
        string? dimension,
        string? ba)
    {
        var groupKey = (ba ?? "").Trim();
        if (groupKey is "__scope__" or "Total")
            return scopeProjects.ToList();

        var rowList = rows.ToList();
        if (!string.IsNullOrEmpty(dimension))
        {
            var match = rowList.FirstOrDefault(r =>
                string.Equals(r.BusinessArea, groupKey, StringComparison.OrdinalIgnoreCase));
            return match?.Projects ?? new List<BusinessAreaProjectItem>();
        }

        var baMatch = rowList.FirstOrDefault(r =>
            string.Equals(r.BusinessArea, groupKey, StringComparison.OrdinalIgnoreCase));
        return baMatch?.Projects ?? new List<BusinessAreaProjectItem>();
    }

    private static List<BusinessAreaProjectItem> ResolveResourcingDrillSourceItems(
        ModernResourcingReportViewModel report,
        string? dimension,
        string? groupKey)
    {
        var key = (groupKey ?? "").Trim();
        var allItems = report.WorkItemRows
            .Select(ToResourcingDrillItem)
            .ToList();
        var byId = allItems.ToDictionary(i => i.Id);
        if (key is "__scope__" or "Total")
            return allItems;

        IEnumerable<int> ids = Array.Empty<int>();
        var dim = (dimension ?? "").Trim().ToLowerInvariant();
        if (dim == "directorate")
        {
            ids = report.DirectorateRows
                .FirstOrDefault(r => string.Equals(r.Name, key, StringComparison.OrdinalIgnoreCase))
                ?.ProjectIds ?? Enumerable.Empty<int>();
        }
        else if (dim == "business-area")
        {
            ids = report.BusinessAreaRows
                .FirstOrDefault(r => string.Equals(r.Name, key, StringComparison.OrdinalIgnoreCase))
                ?.ProjectIds ?? Enumerable.Empty<int>();
        }
        else if (dim == "month")
        {
            ids = report.TrendPoints
                .FirstOrDefault(t => string.Equals(t.Label, key, StringComparison.OrdinalIgnoreCase))
                ?.WorkItemIds ?? Enumerable.Empty<int>();
        }

        return ids
            .Distinct()
            .Where(id => byId.ContainsKey(id))
            .Select(id => byId[id])
            .ToList();
    }

    private static BusinessAreaProjectItem ToResourcingDrillItem(ResourcingWorkItemRow row) => new()
    {
        Id = row.WorkItemId,
        Title = row.Title,
        BusinessArea = row.BusinessArea,
        Rag = string.IsNullOrWhiteSpace(row.Rag) ? "Not Set" : row.Rag,
        Priority = string.IsNullOrWhiteSpace(row.Priority) ? "Not Set" : row.Priority!,
        PermFte = row.PermFte,
        MspFte = row.MspFte,
        SubmittedUpdate = true
    };

    [HttpGet("thematic/export")]
    public async Task<IActionResult> ExportThematicReport(
        int? themeId,
        string? tab,
        CancellationToken cancellationToken = default)
    {
        var summaryRows = await BuildThematicSummaryAsync(cancellationToken);
        var exportTab = NormalizeThematicExportTab(tab);
        var excel = await TryBuildThematicExcelAsync(
            summaryRows,
            themeId,
            exportTab,
            includeAllTaggedWork: false,
            cancellationToken);
        if (excel == null)
            return Unauthorized();

        var suffix = themeId.HasValue && themeId.Value > 0 ? $"theme-{themeId.Value}-{exportTab}" : "summary";
        return ReturnExcelFile(excel, $"thematic-report-{suffix}-{Timestamp()}.xlsx");
    }

    [HttpGet("thematic/export/all")]
    public async Task<IActionResult> ExportThematicReportAll(CancellationToken cancellationToken = default)
    {
        var summaryRows = await BuildThematicSummaryAsync(cancellationToken);
        var excel = await TryBuildThematicExcelAsync(
            summaryRows,
            themeId: null,
            exportTab: "all",
            includeAllTaggedWork: true,
            cancellationToken);
        if (excel == null)
            return Unauthorized();

        return ReturnExcelFile(excel, $"thematic-report-all-{Timestamp()}.xlsx");
    }

    /// <summary>Full service assessments export: summary, assessments, actions, and actions by standard.</summary>
    [HttpGet("assessments/export")]
    public async Task<IActionResult> ExportAssessmentsReport(CancellationToken cancellationToken = default)
    {
        try
        {
            var summaryTask = _serviceAssessmentApi.GetPublishedSummaryAsync(cancellationToken);
            var byStandardTask = _serviceAssessmentApi.GetPublishedActionsByStandardAsync(cancellationToken);
            var allWithActionsTask = _serviceAssessmentApi.GetActionsByStandardAsync();
            await Task.WhenAll(summaryTask, byStandardTask, allWithActionsTask);

            var summary = await summaryTask;
            var byStandard = await byStandardTask;
            var allWithActions = await allWithActionsTask;

            var (stdRows, outcomeDetail) = ServiceAssessmentStandardActionOutcomeBuilder.Build(
                byStandard,
                allWithActions,
                summary?.Assessments);

            var bytes = ServiceAssessmentsReportExcelExport.BuildWorkbook(
                summary,
                allWithActions,
                stdRows,
                outcomeDetail);

            return ReturnExcelFile(bytes, $"service-assessments-{Timestamp()}.xlsx");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error exporting service assessments report");
            TempData["ErrorMessage"] = "Could not export service assessments. Please try again.";
            return RedirectToAction(nameof(Assessments));
        }
    }

    [HttpGet("resourcing/export")]
    public async Task<IActionResult> ExportResourcingReport(
        int? year,
        int? month,
        int? businessAreaId,
        int? directorateId,
        string? dimension,
        int? groupId,
        CancellationToken cancellationToken = default)
    {
        var model = await _monthlyReportService.BuildResourcingReportAsync(
            year,
            month,
            businessAreaId,
            directorateId,
            dimension,
            groupId,
            cancellationToken);

        var bytes = ResourcingReportExcelExport.BuildWorkbook(model);
        var period = SanitizeFilePart(model.MonthName.Replace(" ", "-"));
        return ReturnExcelFile(bytes, $"resourcing-report-{period}-{Timestamp()}.xlsx");
    }

    [HttpGet("raid/export")]
    public async Task<IActionResult> ExportRaidReport(
        string? tab,
        int? businessAreaId,
        int? directorateId,
        CancellationToken cancellationToken = default)
    {
        var model = await _raidReportService.BuildAsync(tab, businessAreaId, directorateId, cancellationToken);
        var panel = model.ActivePanel;
        var tabKey = model.ActiveTab;

        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add(SanitizeSheetName(tabKey));
        WriteRaidReportWorksheet(worksheet, panel);

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return ReturnExcelFile(stream.ToArray(), $"raid-report-{tabKey}-{Timestamp()}.xlsx");
    }

    private async Task<byte[]?> TryBuildThematicExcelAsync(
        List<ThematicReportTagSummaryRow> summaryRows,
        int? themeId,
        string exportTab,
        bool includeAllTaggedWork,
        CancellationToken cancellationToken)
    {
        using var workbook = new XLWorkbook();
        WriteThematicSummaryWorksheet(workbook.Worksheets.Add("Themes"), summaryRows);

        int[]? tagIds = null;
        if (includeAllTaggedWork)
        {
            tagIds = summaryRows.Select(r => r.TagId).ToArray();
        }
        else if (themeId.HasValue && themeId.Value > 0 && summaryRows.Any(r => r.TagId == themeId.Value))
        {
            tagIds = new[] { themeId.Value };
        }

        if (tagIds is { Length: > 0 })
        {
            var user = await GetCurrentUserAsync(cancellationToken);
            if (user == null)
                return null;

            var rows = await _modernWork.BuildWorkRegisterExportRowsAsync(
                isMyWork: false,
                search: null,
                portfolioId: null,
                directorateId: null,
                phaseId: null,
                ragId: null,
                priorityId: null,
                monthlyUpdate: null,
                user.Value.CurrentUser,
                user.Value.Email,
                Url,
                exportTab,
                tagIds: tagIds,
                cancellationToken: cancellationToken);

            var periodColumns = await WorkRegisterMonthlySubmissionExportHelper.EnrichRegisterRowsWithMonthlyPeriodsAsync(
                _context, _monthlyUpdateService, rows, cancellationToken);
            WorkRegisterExcelExport.WriteWorkListSheet(workbook.Worksheets.Add("Work items"), rows, periodColumns);
        }

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private async Task<(User CurrentUser, string Email)?> GetCurrentUserAsync(CancellationToken cancellationToken)
    {
        var userEmail = User.Identity?.Name;
        if (string.IsNullOrEmpty(userEmail))
            return null;

        var currentUser = await _context.Users
            .FirstOrDefaultAsync(u => u.Email.ToLower() == userEmail.ToLower(), cancellationToken);
        if (currentUser == null)
            return null;

        return (currentUser, userEmail);
    }

    private static string NormalizeThematicExportTab(string? tab)
    {
        var key = (tab ?? "active").Trim().ToLowerInvariant();
        return key is "completed" ? "completed" : "active";
    }

    private static string Timestamp() => DateTime.UtcNow.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);

    private IActionResult ReturnExcelFile(byte[] bytes, string fileName) =>
        File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);

    private static string SanitizeSheetName(string name)
    {
        var invalid = new[] { '\\', '/', '*', '?', ':', '[', ']' };
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '-' : c).ToArray());
        return cleaned.Length > 31 ? cleaned[..31] : cleaned;
    }

    private static void WriteThematicSummaryWorksheet(IXLWorksheet worksheet, IEnumerable<ThematicReportTagSummaryRow> rows)
    {
        worksheet.Cell(1, 1).Value = "Theme";
        worksheet.Cell(1, 2).Value = "Description";
        worksheet.Cell(1, 3).Value = "Active work items";
        worksheet.Cell(1, 4).Value = "Completed work items";

        var rowNumber = 2;
        foreach (var row in rows)
        {
            worksheet.Cell(rowNumber, 1).Value = row.Name ?? "";
            worksheet.Cell(rowNumber, 2).Value = row.Description ?? "";
            worksheet.Cell(rowNumber, 3).Value = row.ActiveCount;
            worksheet.Cell(rowNumber, 4).Value = row.CompletedCount;
            rowNumber++;
        }

        worksheet.Range(1, 1, 1, 4).Style.Font.Bold = true;
        worksheet.Columns().AdjustToContents();
    }

    private static void WriteRaidReportWorksheet(IXLWorksheet worksheet, RaidReportTabPanel panel)
    {
        worksheet.Cell(1, 1).Value = "Reference";
        worksheet.Cell(1, 2).Value = "Title";
        var headerCount = 2 + panel.TableHeaders.Count;
        for (var i = 0; i < panel.TableHeaders.Count; i++)
            worksheet.Cell(1, 3 + i).Value = panel.TableHeaders[i];

        var rowNumber = 2;
        foreach (var row in panel.Rows)
        {
            worksheet.Cell(rowNumber, 1).Value = row.Reference;
            worksheet.Cell(rowNumber, 2).Value = row.Title ?? "";
            for (var i = 0; i < row.Cells.Count; i++)
                worksheet.Cell(rowNumber, 3 + i).Value = row.Cells[i];
            rowNumber++;
        }

        worksheet.Range(1, 1, 1, headerCount).Style.Font.Bold = true;
        worksheet.Columns().AdjustToContents();
    }

}
