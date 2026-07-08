using Compass.Models;
using Compass.ViewModels.Modern;
using Microsoft.EntityFrameworkCore;

namespace Compass.Services;

public partial class ModernMonthlyReportService
{
    public async Task<ModernPriorityResourcingReportViewModel> BuildPriorityResourcingReportAsync(
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

        var query = _db.Projects
            .AsNoTracking()
            .Include(p => p.BusinessAreaLookup)
            .Include(p => p.DeliveryPriority)
            .Include(p => p.RagStatusLookup)
            .Include(p => p.MonthlyUpdates)
            .Include(p => p.Directorates)
                .ThenInclude(d => d.Division)
            .Where(p => !p.IsDeleted && p.Status != "Cancelled" && p.Status != "Completed");

        if (businessAreaId.HasValue)
            query = query.Where(p => p.BusinessAreaId == businessAreaId.Value);
        if (directorateId.HasValue)
            query = query.Where(p => p.Directorates.Any(d => d.DivisionId == directorateId.Value));

        var scopedProjects = await query.ToListAsync(cancellationToken);

        var workItemRows = new List<ResourcingWorkItemRow>();
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
            if (perm == 0m && msp == 0m)
                continue;

            var directorateNames = project.Directorates
                .Select(d => d.Division?.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n)
                .ToList();

            workItemRows.Add(new ResourcingWorkItemRow
            {
                WorkItemId = project.Id,
                Title = project.Title,
                BusinessArea = project.BusinessAreaLookup?.Name ?? "Not set",
                Directorates = directorateNames.Count == 0 ? "Not set" : string.Join(", ", directorateNames),
                Rag = RagBucket(project),
                Priority = project.DeliveryPriority?.Name,
                PermFte = perm,
                MspFte = msp,
                ResourcingFte = perm + msp
            });
        }

        workItemRows = workItemRows
            .OrderBy(r => PrioritySortKey(r.Priority ?? "Not set"))
            .ThenByDescending(r => r.ResourcingFte)
            .ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var workItemsById = workItemRows.ToDictionary(r => r.WorkItemId);

        var sections = new List<PriorityResourcingChartSection>
        {
            BuildPriorityResourcingSection("all", null, "All directorates", workItemRows)
        };

        if (!directorateId.HasValue)
        {
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

            var directorateSections = directorateLinks
                .GroupBy(x => (x.DirectorateId, x.DirectorateName))
                .Select(g => BuildPriorityResourcingSection(
                    $"dir-{g.Key.DirectorateId ?? 0}",
                    g.Key.DirectorateId,
                    g.Key.DirectorateName,
                    g.Select(x => x.Item).DistinctBy(i => i.WorkItemId).ToList()))
                .OrderBy(s => s.Title == "Not set" ? "zzzzzz" : s.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();

            sections.AddRange(directorateSections);
        }

        var nextMonthDate = new DateTime(reportYear, reportMonth, 1).AddMonths(1);
        var nextMonthAllowed =
            (nextMonthDate.Year < defaultReportYear ||
             (nextMonthDate.Year == defaultReportYear && nextMonthDate.Month <= defaultReportMonth)) &&
            nextMonthDate.Year <= calendarYearUtc;
        var prevMonthDate = new DateTime(reportYear, reportMonth, 1).AddMonths(-1);
        var earliestReportPeriod = new DateTime(minReportYear, 1, 1);
        var hasPreviousMonthNav = prevMonthDate >= earliestReportPeriod;

        return new ModernPriorityResourcingReportViewModel
        {
            ReportYear = reportYear,
            ReportMonth = reportMonth,
            MonthName = new DateTime(reportYear, reportMonth, 1).ToString("MMMM yyyy"),
            MinReportYear = minReportYear,
            MaxReportYear = maxSelectableYear,
            FilterBusinessAreaId = businessAreaId,
            FilterDirectorateId = directorateId,
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
            SubmittedWorkItemCount = workItemRows.Count,
            Sections = sections,
            WorkItemsById = workItemsById
        };
    }

    private static PriorityResourcingChartSection BuildPriorityResourcingSection(
        string key,
        int? directorateId,
        string title,
        IReadOnlyList<ResourcingWorkItemRow> items)
    {
        var byPriority = items
            .GroupBy(i => NormalizePriorityLabel(i.Priority))
            .ToDictionary(g => g.Key, g => g.ToList());

        var bars = ModernPriorityResourcingReportViewModel.PriorityOrder
            .Select(priority =>
            {
                var rows = byPriority.TryGetValue(priority, out var list) ? list : [];
                return new PriorityResourcingBarPoint
                {
                    Priority = priority,
                    PermFte = rows.Sum(r => r.PermFte),
                    MspFte = rows.Sum(r => r.MspFte),
                    PermWorkItemIds = rows.Where(r => r.PermFte > 0m).Select(r => r.WorkItemId).Distinct().ToList(),
                    MspWorkItemIds = rows.Where(r => r.MspFte > 0m).Select(r => r.WorkItemId).Distinct().ToList(),
                    AllWorkItemIds = rows.Select(r => r.WorkItemId).Distinct().ToList()
                };
            })
            .ToList();

        return new PriorityResourcingChartSection
        {
            Key = key,
            DirectorateId = directorateId,
            Title = title,
            Bars = bars
        };
    }

    private static string NormalizePriorityLabel(string? priority) =>
        string.IsNullOrWhiteSpace(priority) ? "Not set" : priority.Trim();
}
