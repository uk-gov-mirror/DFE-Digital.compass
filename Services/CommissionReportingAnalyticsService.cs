using System;
using System.Collections.Generic;
using System.Linq;
using Compass.Data;
using Compass.Models;
using Compass.ViewModels;
using Compass.ViewModels.Modern;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Compass.Services;

/// <summary>Org-wide commission completion analytics for the reporting hub (catalogue scope).</summary>
public sealed class CommissionReportingAnalyticsService
{
    private readonly CompassDbContext _context;
    private readonly IProductsApiService _productsApi;
    private readonly IPerformanceReportingEligibilityService _eligibilityService;
    private readonly ILogger<CommissionReportingAnalyticsService> _logger;

    public CommissionReportingAnalyticsService(
        CompassDbContext context,
        IProductsApiService productsApi,
        IPerformanceReportingEligibilityService eligibilityService,
        ILogger<CommissionReportingAnalyticsService> logger)
    {
        _context = context;
        _productsApi = productsApi;
        _eligibilityService = eligibilityService;
        _logger = logger;
    }

    private static bool IsOpenForSubmissionWindow(Commission c, DateTime now) =>
        c.IsActive && now >= c.OpenDate && now <= c.DueDate.AddDays(1);

    /// <summary>
    /// Closed for the performance "completed" style list: not in the active submission window,
    /// excluding active commissions that are not yet open.
    /// </summary>
    public static bool IsEligibleForCompletedPerformanceReporting(Commission c, DateTime now)
    {
        if (IsOpenForSubmissionWindow(c, now))
            return false;
        if (c.IsActive && now < c.OpenDate)
            return false;
        return true;
    }

    public async Task<ModernReportingPerformanceIndexViewModel> BuildIndexAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var commissions = await _context.Commissions.AsNoTracking()
            .OrderByDescending(c => c.DueDate)
            .ToListAsync(cancellationToken);

        var eligible = commissions.Where(c => IsEligibleForCompletedPerformanceReporting(c, now)).ToList();
        if (eligible.Count == 0)
            return new ModernReportingPerformanceIndexViewModel();

        List<ProductDto> catalogue;
        try
        {
            catalogue = await _productsApi.GetAllProductsAsync(null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Commission reporting analytics: failed to load product catalogue");
            return new ModernReportingPerformanceIndexViewModel
            {
                LoadError = "Could not load the product catalogue. Try again shortly."
            };
        }

        catalogue = CommissionReportingProductScope.GetAllActivePublishedEligible(catalogue);

        var commissionIds = eligible.Select(c => c.Id).ToList();
        var submissions = await _context.CommissionSubmissions.AsNoTracking()
            .Include(cs => cs.MetricValues)
            .Where(cs => commissionIds.Contains(cs.CommissionId))
            .ToListAsync(cancellationToken);

        var subsByCommission = submissions.GroupBy(s => s.CommissionId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var rows = new List<ModernReportingPerformanceCommissionSummary>();
        foreach (var commission in eligible)
        {
            subsByCommission.TryGetValue(commission.Id, out var subs);
            var summary = await BuildSummaryAsync(commission, catalogue, subs ?? new List<CommissionSubmission>(), cancellationToken);
            rows.Add(summary);
        }

        return new ModernReportingPerformanceIndexViewModel { Commissions = rows };
    }

    public async Task<ModernReportingPerformanceCommissionDetailViewModel?> BuildDetailAsync(
        int commissionId,
        string? filterBusinessAreaName,
        string? filterDirectorateName,
        CancellationToken cancellationToken = default)
    {
        var commission = await _context.Commissions.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == commissionId, cancellationToken);
        if (commission == null)
            return null;

        var now = DateTime.UtcNow;
        if (!IsEligibleForCompletedPerformanceReporting(commission, now))
            return null;

        List<ProductDto> catalogue;
        try
        {
            catalogue = await _productsApi.GetAllProductsAsync(null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Commission reporting analytics: failed to load product catalogue for commission {Id}", commissionId);
            return new ModernReportingPerformanceCommissionDetailViewModel
            {
                Commission = commission,
                LoadError = "Could not load the product catalogue. Try again shortly."
            };
        }

        catalogue = CommissionReportingProductScope.GetAllActivePublishedEligible(catalogue);
        catalogue = ApplyPerformanceCatalogueFilters(catalogue, filterBusinessAreaName, filterDirectorateName);

        var submissions = await _context.CommissionSubmissions.AsNoTracking()
            .Include(cs => cs.MetricValues)
            .Where(cs => cs.CommissionId == commissionId)
            .ToListAsync(cancellationToken);

        var summary = await BuildSummaryAsync(commission, catalogue, submissions, cancellationToken);
        var perProduct = await BuildPerProductStatsAsync(commission, catalogue, submissions, cancellationToken);

        var baGroups = perProduct
            .GroupBy(p => p.BusinessArea ?? "Not assigned", StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var businessAreas = new List<ModernReportingPerformanceBusinessAreaRow>();
        foreach (var g in baGroups)
        {
            var total = g.Count();
            var ns = g.Count(x => x.Status == CommissionSubmissionStatus.NotStarted);
            var ip = g.Count(x => x.Status == CommissionSubmissionStatus.InProgress);
            var sub = g.Count(x => x.Status == CommissionSubmissionStatus.Submitted);
            var late = g.Count(x => x.Status == CommissionSubmissionStatus.Late);
            var returned = sub + late;
            var rate = total == 0 ? 0 : Math.Round(100m * returned / total, 1);

            var metNum = 0;
            var metDen = 0;
            foreach (var x in g)
            {
                if (x.TotalMetrics > 0)
                {
                    metDen += x.TotalMetrics;
                    metNum += x.CompletedMetrics;
                }
            }

            var metPct = metDen == 0 ? 0 : Math.Round(100m * metNum / metDen, 1);
            businessAreas.Add(new ModernReportingPerformanceBusinessAreaRow
            {
                BusinessArea = g.Key,
                Total = total,
                NotStarted = ns,
                InProgress = ip,
                Submitted = sub,
                Late = late,
                ReturnRatePercent = rate,
                MetricCompletionPercent = metPct
            });
        }

        return new ModernReportingPerformanceCommissionDetailViewModel
        {
            Commission = commission,
            ProductsInScope = summary.ProductsInScope,
            NotStarted = summary.NotStarted,
            InProgress = summary.InProgress,
            Submitted = summary.Submitted,
            Late = summary.Late,
            ReturnRatePercent = summary.ReturnRatePercent,
            MetricCompletionPercent = summary.MetricCompletionPercent,
            CompletedMetricCells = summary.CompletedMetricCells,
            ApplicableMetricCells = summary.ApplicableMetricCells,
            BusinessAreas = businessAreas
        };
    }

    /// <summary>
    /// Single reporting page: commission picker, optional business area / directorate filters (same lookups as monthly report), summary + breakdown.
    /// </summary>
    public async Task<ModernReportingPerformancePageViewModel> BuildPerformancePageAsync(
        int? commissionId,
        int? businessAreaId,
        int? directorateId,
        CancellationToken cancellationToken = default)
    {
        var index = await BuildIndexAsync(cancellationToken);
        var businessAreaOptions = await _context.BusinessAreaLookups.AsNoTracking()
            .Where(b => b.IsActive)
            .OrderBy(b => b.SortOrder)
            .ThenBy(b => b.Name)
            .ToListAsync(cancellationToken);
        var directorateOptions = await _context.Divisions.AsNoTracking()
            .Where(d => d.IsActive)
            .OrderBy(d => d.SortOrder)
            .ThenBy(d => d.Name)
            .ToListAsync(cancellationToken);

        var page = new ModernReportingPerformancePageViewModel
        {
            LoadError = index.LoadError,
            Commissions = index.Commissions,
            BusinessAreaOptions = businessAreaOptions,
            DirectorateOptions = directorateOptions
        };

        if (!string.IsNullOrEmpty(index.LoadError))
            return page;

        if (index.Commissions.Count == 0)
            return page;

        var selectedId = commissionId;
        if (selectedId == null || index.Commissions.All(c => c.CommissionId != selectedId))
            selectedId = index.Commissions[0].CommissionId;

        page.SelectedCommissionId = selectedId;

        string? baName = null;
        if (businessAreaId is > 0)
        {
            baName = businessAreaOptions.FirstOrDefault(x => x.Id == businessAreaId)?.Name;
            page.FilterBusinessAreaId = baName != null ? businessAreaId : null;
        }

        string? dirName = null;
        if (directorateId is > 0)
        {
            dirName = directorateOptions.FirstOrDefault(x => x.Id == directorateId)?.Name;
            page.FilterDirectorateId = dirName != null ? directorateId : null;
        }

        var detail = await BuildDetailAsync(selectedId.Value, baName, dirName, cancellationToken);
        page.Detail = detail;

        return page;
    }

    /// <summary>
    /// Reporting hub — live submission progress for active commission rounds (same product scope as commission workspace All products).
    /// </summary>
    public async Task<ModernReportingPerformanceSubmissionProgressViewModel> BuildPerformanceSubmissionProgressPageAsync(
        int? commissionId,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;

        var activeCommissions = await _context.Commissions.AsNoTracking()
            .Where(c => c.IsActive && now >= c.OpenDate)
            .OrderByDescending(c => c.DueDate)
            .ToListAsync(cancellationToken);

        var commissionOptions = activeCommissions.Select(c => new CommissionPickerOption
        {
            Id = c.Id,
            Name = c.Name,
            DueDate = c.DueDate,
            IsActive = c.IsActive
        }).ToList();

        if (commissionOptions.Count == 0)
        {
            return new ModernReportingPerformanceSubmissionProgressViewModel
            {
                CommissionOptions = commissionOptions
            };
        }

        var defaultId = activeCommissions.FirstOrDefault(c => IsOpenForSubmissionWindow(c, now))?.Id
            ?? activeCommissions[0].Id;

        var selectedId = commissionId ?? defaultId;
        if (activeCommissions.All(c => c.Id != selectedId))
            selectedId = defaultId;

        var commission = activeCommissions.First(c => c.Id == selectedId);

        List<ProductDto> scopeProducts;
        try
        {
            scopeProducts = await GetCommissionScopeProductsForReportingAsync(commission, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Performance submission progress: failed to load product catalogue for commission {Id}", selectedId);
            return new ModernReportingPerformanceSubmissionProgressViewModel
            {
                CommissionOptions = commissionOptions,
                SelectedCommissionId = selectedId,
                Commission = MapCommissionSummary(commission),
                HasCommission = true,
                SubmissionWindowPhase = ResolveSubmissionWindowPhase(commission, now),
                LoadError = "Could not load the product catalogue. Try again shortly."
            };
        }

        var submissions = await _context.CommissionSubmissions.AsNoTracking()
            .Include(cs => cs.MetricValues)
            .Where(cs => cs.CommissionId == commission.Id)
            .ToListAsync(cancellationToken);

        var submissionsDict = submissions.ToDictionary(cs => cs.ProductDocumentId, cs => cs, StringComparer.OrdinalIgnoreCase);
        var metricsForPeriod =
            await CommissionReportingMetricsHelper.LoadEnabledMetricsForCommissionPeriodAsync(
                _context, commission, cancellationToken);

        var perProduct = await BuildPerProductStatsForScopeAsync(commission, scopeProducts, submissions, cancellationToken);
        var submitted = perProduct.Count(x => x.Status == CommissionSubmissionStatus.Submitted);
        var late = perProduct.Count(x => x.Status == CommissionSubmissionStatus.Late);
        var inProgress = perProduct.Count(x => x.Status == CommissionSubmissionStatus.InProgress);
        var notStarted = perProduct.Count(x => x.Status == CommissionSubmissionStatus.NotStarted);
        var total = perProduct.Count;
        var returned = submitted + late;
        var returnRate = total == 0 ? 0 : Math.Round(100m * returned / total, 1);

        var metNum = perProduct.Sum(x => x.CompletedMetrics);
        var metDen = perProduct.Sum(x => x.TotalMetrics);
        var metPct = metDen == 0 ? 0 : Math.Round(100m * metNum / metDen, 1);

        var businessAreas = BuildBusinessAreaRows(perProduct);
        var metricRows = BuildMetricRows(commission, scopeProducts, submissionsDict, metricsForPeriod);
        var windowPhase = ResolveSubmissionWindowPhase(commission, now);
        var daysUntilDue = (int)(commission.DueDate.Date - now.Date).TotalDays;

        return new ModernReportingPerformanceSubmissionProgressViewModel
        {
            CommissionOptions = commissionOptions,
            SelectedCommissionId = commission.Id,
            Commission = MapCommissionSummary(commission),
            ProductsInScope = total,
            NotStarted = notStarted,
            InProgress = inProgress,
            Submitted = submitted,
            Late = late,
            ReturnRatePercent = returnRate,
            MetricCompletionPercent = metPct,
            CompletedMetricCells = metNum,
            ApplicableMetricCells = metDen,
            SubmissionWindowPhase = windowPhase,
            DaysUntilDue = daysUntilDue,
            BusinessAreas = businessAreas,
            MetricRows = metricRows,
            HasCommission = true
        };
    }

    private async Task<List<ProductDto>> GetCommissionScopeProductsForReportingAsync(
        Commission commission,
        CancellationToken cancellationToken)
    {
        var allCatalog = await _productsApi.GetAllProductsAsync(null);
        var catalogue = CommissionReportingProductScope.GetAllActivePublishedEligible(allCatalog);
        var scope = catalogue
            .Where(p => CommissionReportingProductScope.ProductMatchesCommissionInScopeRules(commission, p))
            .ToList();

        var eligibilityCache = await _eligibilityService.LoadEligibilityCacheAsync();
        return scope
            .Where(p => !_eligibilityService.IsProductExcludedForCommission(p, commission, eligibilityCache))
            .ToList();
    }

    private static List<ModernReportingPerformanceBusinessAreaRow> BuildBusinessAreaRows(
        List<ProductCommissionStats> perProduct)
    {
        return perProduct
            .GroupBy(p => p.BusinessArea ?? "Not assigned", StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var rowTotal = g.Count();
                var ns = g.Count(x => x.Status == CommissionSubmissionStatus.NotStarted);
                var ip = g.Count(x => x.Status == CommissionSubmissionStatus.InProgress);
                var sub = g.Count(x => x.Status == CommissionSubmissionStatus.Submitted);
                var late = g.Count(x => x.Status == CommissionSubmissionStatus.Late);
                var returned = sub + late;
                var rate = rowTotal == 0 ? 0 : Math.Round(100m * returned / rowTotal, 1);

                var metNum = 0;
                var metDen = 0;
                foreach (var x in g)
                {
                    if (x.TotalMetrics > 0)
                    {
                        metDen += x.TotalMetrics;
                        metNum += x.CompletedMetrics;
                    }
                }

                var metPct = metDen == 0 ? 0 : Math.Round(100m * metNum / metDen, 1);
                return new ModernReportingPerformanceBusinessAreaRow
                {
                    BusinessArea = g.Key,
                    Total = rowTotal,
                    NotStarted = ns,
                    InProgress = ip,
                    Submitted = sub,
                    Late = late,
                    ReturnRatePercent = rate,
                    MetricCompletionPercent = metPct
                };
            })
            .ToList();
    }

    /// <summary>Operations console — live commission dashboard (any commission, includes metric completion).</summary>
    public async Task<OperationsManagePerformanceViewModel> BuildOperationsManagePerformanceAsync(
        int? commissionId,
        CancellationToken cancellationToken = default)
    {
        var jsonOpts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var now = DateTime.UtcNow;

        var commissionOptions = await _context.Commissions.AsNoTracking()
            .OrderByDescending(c => c.DueDate)
            .Select(c => new CommissionPickerOption
            {
                Id = c.Id,
                Name = c.Name,
                DueDate = c.DueDate,
                IsActive = c.IsActive
            })
            .ToListAsync(cancellationToken);

        if (commissionOptions.Count == 0)
            return new OperationsManagePerformanceViewModel { CommissionOptions = commissionOptions };

        var selectedId = commissionId ?? commissionOptions[0].Id;
        if (commissionOptions.All(x => x.Id != selectedId))
            selectedId = commissionOptions[0].Id;

        var commissionEntity = await _context.Commissions.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == selectedId, cancellationToken);

        if (commissionEntity == null)
        {
            return new OperationsManagePerformanceViewModel
            {
                CommissionOptions = commissionOptions,
                SelectedCommissionId = selectedId
            };
        }

        List<ProductDto> eligibleProducts;
        try
        {
            eligibleProducts = await GetCommissionScopeProductsForReportingAsync(commissionEntity, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Operations manage performance: failed to load product catalogue");
            return new OperationsManagePerformanceViewModel
            {
                CommissionOptions = commissionOptions,
                SelectedCommissionId = commissionEntity.Id,
                Commission = MapCommissionSummary(commissionEntity),
                HasCommission = true,
                SubmissionWindowPhase = ResolveSubmissionWindowPhase(commissionEntity, now)
            };
        }

        var submissions = await _context.CommissionSubmissions.AsNoTracking()
            .Include(cs => cs.MetricValues)
            .Where(cs => cs.CommissionId == commissionEntity.Id)
            .ToListAsync(cancellationToken);

        var submissionsDict = submissions.ToDictionary(cs => cs.ProductDocumentId, cs => cs, StringComparer.OrdinalIgnoreCase);
        var metricsForPeriod =
            await CommissionReportingMetricsHelper.LoadEnabledMetricsForCommissionPeriodAsync(
                _context, commissionEntity, cancellationToken);

        var perProduct = await BuildPerProductStatsForScopeAsync(commissionEntity, eligibleProducts, submissions, cancellationToken);
        var submitted = perProduct.Count(x => x.Status == CommissionSubmissionStatus.Submitted);
        var late = perProduct.Count(x => x.Status == CommissionSubmissionStatus.Late);
        var inProgress = perProduct.Count(x => x.Status == CommissionSubmissionStatus.InProgress);
        var notStarted = perProduct.Count(x => x.Status == CommissionSubmissionStatus.NotStarted);
        var total = perProduct.Count;
        var returned = submitted + late;
        var returnRate = total == 0 ? 0 : Math.Round(100m * returned / total, 1);

        var metNum = perProduct.Sum(x => x.CompletedMetrics);
        var metDen = perProduct.Sum(x => x.TotalMetrics);
        var metPct = metDen == 0 ? 0 : Math.Round(100m * metNum / metDen, 1);

        var baRows = BuildOrgRows(perProduct, p => p.BusinessArea ?? "Unassigned");

        var dirRows = eligibleProducts
            .Select(p =>
            {
                var doc = p.DocumentId ?? "";
                submissionsDict.TryGetValue(doc, out var sub);
                var existing = sub?.MetricValues?.ToList() ?? new List<CommissionMetricValue>();
                var applicable = CommissionReportingMetricsHelper.FilterApplicableMetricsForProduct(
                    commissionEntity, p, metricsForPeriod, existing);
                var completed = applicable.Count(m =>
                    existing.Any(mv => mv.PerformanceMetricId == m.Id && mv.IsComplete));
                var status = sub?.Status ?? CommissionSubmissionStatus.NotStarted;
                return new
                {
                    Directorate = CommissionReportingProductScope.GetDirectorate(p) ?? "Unassigned",
                    Status = status,
                    Completed = completed,
                    Total = applicable.Count
                };
            })
            .GroupBy(x => x.Directorate)
            .Select(g => AggregateOrgFromParts(g.Key, g.Select(x => (x.Status, x.Completed, x.Total))))
            .OrderByDescending(r => r.PotentialSubmissions)
            .ThenBy(r => r.Name)
            .ToList();

        var metricRows = BuildMetricRows(commissionEntity, eligibleProducts, submissionsDict, metricsForPeriod);

        var doughnut = new
        {
            labels = new[] { "Submitted", "Late", "In progress", "Not started" },
            values = new[] { submitted, late, inProgress, notStarted },
            colors = new[] { "#00703c", "#d4351c", "#f47738", "#b1b4b6" }
        };

        var topBa = baRows.Take(12).ToList();
        var baBar = new
        {
            labels = topBa.Select(r => r.Name).ToArray(),
            potential = topBa.Select(r => r.PotentialSubmissions).ToArray(),
            actual = topBa.Select(r => r.ActualSubmitted + r.ActualLate).ToArray()
        };

        var topMetrics = metricRows.OrderByDescending(m => m.ApplicableProducts).Take(12).ToList();
        var metricBar = new
        {
            labels = topMetrics.Select(m => m.Name).ToArray(),
            completed = topMetrics.Select(m => m.CompletedCount).ToArray(),
            applicable = topMetrics.Select(m => m.ApplicableProducts).ToArray()
        };

        var timelinePoints = submissions
            .Where(s => s.SubmittedDate.HasValue &&
                        (s.Status == CommissionSubmissionStatus.Submitted || s.Status == CommissionSubmissionStatus.Late))
            .Select(s => s.SubmittedDate!.Value.Date)
            .OrderBy(d => d)
            .ToList();

        object timeline;
        if (timelinePoints.Count == 0)
        {
            timeline = new { labels = Array.Empty<string>(), cumulative = Array.Empty<int>() };
        }
        else
        {
            var byDay = timelinePoints.GroupBy(d => d).OrderBy(g => g.Key).ToList();
            var labels = new List<string>();
            var cumulative = new List<int>();
            var running = 0;
            foreach (var day in byDay)
            {
                running += day.Count();
                labels.Add(day.Key.ToString("d MMM yyyy", System.Globalization.CultureInfo.GetCultureInfo("en-GB")));
                cumulative.Add(running);
            }

            timeline = new { labels, cumulative };
        }

        var windowPhase = ResolveSubmissionWindowPhase(commissionEntity, now);
        var daysUntilDue = (int)(commissionEntity.DueDate.Date - now.Date).TotalDays;
        var daysUntilOpen = (int)(commissionEntity.OpenDate.Date - now.Date).TotalDays;
        var overview = BuildOverviewLines(
            commissionEntity,
            total,
            returned,
            returnRate,
            submitted,
            late,
            metPct,
            metNum,
            metDen,
            windowPhase,
            daysUntilDue,
            daysUntilOpen);

        return new OperationsManagePerformanceViewModel
        {
            CommissionOptions = commissionOptions,
            SelectedCommissionId = commissionEntity.Id,
            Commission = MapCommissionSummary(commissionEntity),
            EligibleProductCount = total,
            SubmittedCount = submitted,
            LateCount = late,
            InProgressCount = inProgress,
            NotStartedCount = notStarted,
            ReturnRatePercent = returnRate,
            MetricCompletionPercent = metPct,
            CompletedMetricCells = metNum,
            ApplicableMetricCells = metDen,
            SubmissionWindowPhase = windowPhase,
            DaysUntilDue = daysUntilDue,
            OverviewLines = overview,
            BusinessAreaRows = baRows,
            DirectorateRows = dirRows,
            MetricRows = metricRows,
            StatusDoughnutJson = JsonSerializer.Serialize(doughnut, jsonOpts),
            BusinessAreaBarJson = JsonSerializer.Serialize(baBar, jsonOpts),
            MetricCompletionBarJson = JsonSerializer.Serialize(metricBar, jsonOpts),
            SubmissionTimelineJson = JsonSerializer.Serialize(timeline, jsonOpts),
            HasCommission = true,
            HasEligibleProducts = total > 0
        };
    }

    private static CommissionSummaryVm MapCommissionSummary(Commission c) => new()
    {
        Id = c.Id,
        Name = c.Name,
        Quarter = c.Quarter,
        StartDate = c.StartDate,
        EndDate = c.EndDate,
        OpenDate = c.OpenDate,
        DueDate = c.DueDate,
        IsActive = c.IsActive
    };

    private static string ResolveSubmissionWindowPhase(Commission c, DateTime now)
    {
        if (IsOpenForSubmissionWindow(c, now))
            return "Open";
        if (c.IsActive && now < c.OpenDate)
            return "Upcoming";
        return "Closed";
    }

    private static List<string> BuildOverviewLines(
        Commission commission,
        int productsInScope,
        int returned,
        decimal returnRate,
        int submitted,
        int late,
        decimal metricPct,
        int metCompleted,
        int metApplicable,
        string windowPhase,
        int daysUntilDue,
        int daysUntilOpen)
    {
        var ci = System.Globalization.CultureInfo.GetCultureInfo("en-GB");
        var lines = new List<string>
        {
            $"{commission.Name} covers {productsInScope:N0} in-scope products from the service register.",
            $"{returned:N0} products have returned metrics ({returnRate:0.#}% return rate): {submitted:N0} on time and {late:N0} late.",
            $"Metric completion is {metricPct:0.#}% across the reporting grid ({metCompleted:N0} of {metApplicable:N0} applicable cells completed)."
        };

        lines.Add(windowPhase switch
        {
            "Open" => daysUntilDue >= 0
                ? $"Submissions are open — due in {daysUntilDue} day{(daysUntilDue == 1 ? "" : "s")} ({commission.DueDate.ToString("d MMM yyyy", ci)})."
                : $"Submissions are open — due date was {Math.Abs(daysUntilDue)} day{(Math.Abs(daysUntilDue) == 1 ? "" : "s")} ago.",
            "Upcoming" => daysUntilOpen > 0
                ? $"Submissions open in {daysUntilOpen} day{(daysUntilOpen == 1 ? "" : "s")} ({commission.OpenDate.ToString("d MMM yyyy", ci)})."
                : "Submissions window is not yet open.",
            _ => "This commission round is closed for submissions."
        });

        return lines;
    }

    private static List<OpsPerfOrgRow> BuildOrgRows(
        List<ProductCommissionStats> perProduct,
        Func<ProductCommissionStats, string> keySelector) =>
        perProduct
            .GroupBy(keySelector)
            .Select(g => AggregateOrgFromParts(
                g.Key,
                g.Select(x => (x.Status, x.CompletedMetrics, x.TotalMetrics))))
            .OrderByDescending(r => r.PotentialSubmissions)
            .ThenBy(r => r.Name)
            .ToList();

    private static OpsPerfOrgRow AggregateOrgFromParts(
        string name,
        IEnumerable<(CommissionSubmissionStatus Status, int Completed, int Total)> items)
    {
        var list = items.ToList();
        var potential = list.Count;
        var ac = list.Count(x => x.Status == CommissionSubmissionStatus.Submitted);
        var al = list.Count(x => x.Status == CommissionSubmissionStatus.Late);
        var ip = list.Count(x => x.Status == CommissionSubmissionStatus.InProgress);
        var ns = list.Count(x => x.Status == CommissionSubmissionStatus.NotStarted);
        return new OpsPerfOrgRow
        {
            Name = name,
            PotentialSubmissions = potential,
            ActualSubmitted = ac,
            ActualLate = al,
            InProgress = ip,
            NotStarted = ns,
            CompletedMetricCells = list.Sum(x => x.Completed),
            ApplicableMetricCells = list.Sum(x => x.Total)
        };
    }

    private static List<OpsPerfMetricRow> BuildMetricRows(
        Commission commission,
        List<ProductDto> eligibleProducts,
        Dictionary<string, CommissionSubmission> submissionsDict,
        List<PerformanceMetric> metricsForPeriod)
    {
        var rows = new List<OpsPerfMetricRow>();
        foreach (var metric in metricsForPeriod.OrderBy(m => m.Title))
        {
            var applicable = 0;
            var completedProducts = new List<OpsPerfMetricProductRow>();
            foreach (var p in eligibleProducts)
            {
                var docId = p.DocumentId ?? "";
                submissionsDict.TryGetValue(docId, out var sub);
                var existing = sub?.MetricValues?.ToList() ?? new List<CommissionMetricValue>();
                var applicableForProduct = CommissionReportingMetricsHelper.FilterApplicableMetricsForProduct(
                    commission, p, metricsForPeriod, existing);
                if (applicableForProduct.All(m => m.Id != metric.Id))
                    continue;
                applicable++;
                var mv = existing.FirstOrDefault(x => x.PerformanceMetricId == metric.Id && x.IsComplete);
                if (mv == null)
                    continue;

                completedProducts.Add(new OpsPerfMetricProductRow
                {
                    ProductTitle = p.Title ?? "Untitled",
                    ProductDocumentId = docId,
                    BusinessArea = CommissionReportingProductScope.GetBusinessArea(p) ?? "Not assigned",
                    DisplayValue = FormatMetricValueDisplay(mv),
                    NotCapturedReason = mv.IsNotCaptured ? mv.NotCapturedReason : null,
                    ReasonForDifference = mv.ReasonForDifference
                });
            }

            if (applicable > 0)
            {
                rows.Add(new OpsPerfMetricRow
                {
                    MetricId = metric.Id,
                    Name = metric.Title,
                    ApplicableProducts = applicable,
                    CompletedCount = completedProducts.Count,
                    CompletedProducts = completedProducts
                        .OrderBy(x => x.ProductTitle, StringComparer.OrdinalIgnoreCase)
                        .ToList()
                });
            }
        }

        return rows;
    }

    private static string FormatMetricValueDisplay(CommissionMetricValue mv)
    {
        if (mv.IsNotCaptured)
            return "Not captured";

        return string.IsNullOrWhiteSpace(mv.Value) ? "–" : mv.Value.Trim();
    }

    private static List<ProductDto> ApplyPerformanceCatalogueFilters(
        List<ProductDto> catalogue,
        string? businessAreaName,
        string? directorateName)
    {
        IEnumerable<ProductDto> q = catalogue;
        if (!string.IsNullOrWhiteSpace(businessAreaName))
        {
            var ba = businessAreaName.Trim();
            q = q.Where(p => string.Equals(
                CommissionReportingProductScope.GetBusinessArea(p) ?? "",
                ba,
                StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(directorateName))
        {
            var dn = directorateName.Trim();
            q = q.Where(p => string.Equals(
                CommissionReportingProductScope.GetDirectorate(p) ?? "",
                dn,
                StringComparison.OrdinalIgnoreCase));
        }

        return q.ToList();
    }

    private sealed class ProductCommissionStats
    {
        public string? BusinessArea { get; init; }
        public CommissionSubmissionStatus Status { get; init; }
        public int CompletedMetrics { get; init; }
        public int TotalMetrics { get; init; }
    }

    private async Task<List<ProductCommissionStats>> BuildPerProductStatsAsync(
        Commission commission,
        List<ProductDto> catalogue,
        List<CommissionSubmission> submissions,
        CancellationToken cancellationToken)
    {
        var scopeProducts = catalogue
            .Where(p => CommissionReportingProductScope.ProductMatchesCommissionInScopeRules(commission, p))
            .ToList();

        return await BuildPerProductStatsForScopeAsync(commission, scopeProducts, submissions, cancellationToken);
    }

    private async Task<List<ProductCommissionStats>> BuildPerProductStatsForScopeAsync(
        Commission commission,
        List<ProductDto> scopeProducts,
        List<CommissionSubmission> submissions,
        CancellationToken cancellationToken)
    {
        var submissionsDict = submissions.ToDictionary(cs => cs.ProductDocumentId, cs => cs, StringComparer.OrdinalIgnoreCase);

        var metricsForPeriod =
            await CommissionReportingMetricsHelper.LoadEnabledMetricsForCommissionPeriodAsync(_context, commission,
                cancellationToken);

        var rows = new List<ProductCommissionStats>();
        foreach (var p in scopeProducts)
        {
            var docId = p.DocumentId ?? "";
            submissionsDict.TryGetValue(docId, out var sub);
            var existing = sub?.MetricValues?.ToList() ?? new List<CommissionMetricValue>();
            var applicable = CommissionReportingMetricsHelper.FilterApplicableMetricsForProduct(commission, p,
                metricsForPeriod, existing);
            var total = applicable.Count;
            var completed = applicable.Count(m =>
                existing.Any(mv => mv.PerformanceMetricId == m.Id && mv.IsComplete));

            rows.Add(new ProductCommissionStats
            {
                BusinessArea = CommissionReportingProductScope.GetBusinessArea(p),
                Status = sub?.Status ?? CommissionSubmissionStatus.NotStarted,
                CompletedMetrics = completed,
                TotalMetrics = total
            });
        }

        return rows;
    }

    private async Task<ModernReportingPerformanceCommissionSummary> BuildSummaryAsync(
        Commission commission,
        List<ProductDto> catalogue,
        List<CommissionSubmission> submissions,
        CancellationToken cancellationToken)
    {
        var perProduct = await BuildPerProductStatsAsync(commission, catalogue, submissions, cancellationToken);
        var total = perProduct.Count;
        var ns = perProduct.Count(x => x.Status == CommissionSubmissionStatus.NotStarted);
        var ip = perProduct.Count(x => x.Status == CommissionSubmissionStatus.InProgress);
        var sub = perProduct.Count(x => x.Status == CommissionSubmissionStatus.Submitted);
        var late = perProduct.Count(x => x.Status == CommissionSubmissionStatus.Late);
        var returned = sub + late;
        var rate = total == 0 ? 0 : Math.Round(100m * returned / total, 1);

        var metNum = 0;
        var metDen = 0;
        foreach (var x in perProduct)
        {
            if (x.TotalMetrics > 0)
            {
                metDen += x.TotalMetrics;
                metNum += x.CompletedMetrics;
            }
        }

        var metPct = metDen == 0 ? 0 : Math.Round(100m * metNum / metDen, 1);

        return new ModernReportingPerformanceCommissionSummary
        {
            CommissionId = commission.Id,
            Name = commission.Name,
            StartDate = commission.StartDate,
            EndDate = commission.EndDate,
            DueDate = commission.DueDate,
            IsActive = commission.IsActive,
            ProductsInScope = total,
            NotStarted = ns,
            InProgress = ip,
            Submitted = sub,
            Late = late,
            ReturnRatePercent = rate,
            MetricCompletionPercent = metPct,
            CompletedMetricCells = metNum,
            ApplicableMetricCells = metDen
        };
    }
}
