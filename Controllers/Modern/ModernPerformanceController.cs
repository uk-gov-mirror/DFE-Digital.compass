using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using Compass.Data;
using Compass.Models;
using Compass.Services;
using Compass.ViewModels.Modern;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Compass.Controllers.Modern;

/// <summary>Modern UI for product commission performance reporting at <c>/modern/performance</c>.</summary>
[Authorize]
[Route("modern/performance")]
public class ModernPerformanceController : Controller
{
    private const string BulkCacheKeyPrefix = "modern-perf-bulk:";

    private readonly CompassDbContext _context;
    private readonly IProductsApiService _productsApi;
    private readonly IMemoryCache _cache;
    private readonly ILogger<ModernPerformanceController> _logger;
    private readonly IPermissionService _permissions;
    private readonly IBusinessAreaAdminService _businessAreaAdmins;
    private readonly IBusinessAreaLeadershipService _businessAreaLeadership;
    private readonly IPerformanceReportingEligibilityService _eligibilityService;

    public ModernPerformanceController(
        CompassDbContext context,
        IProductsApiService productsApi,
        IMemoryCache cache,
        ILogger<ModernPerformanceController> logger,
        IPermissionService permissions,
        IBusinessAreaAdminService businessAreaAdmins,
        IBusinessAreaLeadershipService businessAreaLeadership,
        IPerformanceReportingEligibilityService eligibilityService)
    {
        _context = context;
        _productsApi = productsApi;
        _cache = cache;
        _logger = logger;
        _permissions = permissions;
        _businessAreaAdmins = businessAreaAdmins;
        _businessAreaLeadership = businessAreaLeadership;
        _eligibilityService = eligibilityService;
    }

    private void SetChrome(string subNavItem)
    {
        ViewBag.MainNavSection = "performance";
        ViewBag.SubNavItem = subNavItem;
    }

    private string GetUserEmail() =>
        User.FindFirstValue(ClaimTypes.Email)
        ?? User.FindFirstValue("preferred_username")
        ?? User.Identity?.Name
        ?? "";

    private async Task<int?> ResolveCurrentUserIdAsync(string userEmail, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userEmail))
            return null;
        var el = userEmail.Trim().ToLowerInvariant();
        return await _context.Users.AsNoTracking()
            .Where(u => u.Email.ToLower() == el)
            .Select(u => (int?)u.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>Central Ops / Super admin, or delegated admin for the product&apos;s FIPS &quot;Business area&quot; (matched to Compass lookup).</summary>
    private async Task<bool> CurrentUserMayOverridePerformanceCommissionLocksAsync(ProductDto? product,
        CancellationToken cancellationToken)
    {
        if (product == null)
            return false;

        var email = GetUserEmail();
        if (string.IsNullOrWhiteSpace(email))
            return false;

        if (await _permissions.IsCentralOperationsAdminOrSuperAdminAsync(email.Trim()))
            return true;

        var uid = await ResolveCurrentUserIdAsync(email, cancellationToken);
        if (!uid.HasValue)
            return false;

        var baName = CommissionReportingProductScope.GetBusinessArea(product);
        var lookupId = await _businessAreaAdmins.GetLookupIdForBusinessAreaDisplayNameAsync(baName, cancellationToken);
        if (!lookupId.HasValue)
            return false;

        return await _businessAreaAdmins.IsUserAdminForAnyBusinessAreaAsync(uid.Value, new[] { lookupId.Value },
            cancellationToken)
            || await _businessAreaLeadership.IsUserLeaderForAnyBusinessAreaAsync(
                uid.Value, new[] { lookupId.Value }, cancellationToken);
    }

    private static bool IsOpenForSubmissionWindow(Commission c, DateTime now) =>
        c.IsActive && now >= c.OpenDate && now <= c.DueDate.AddDays(1);

    private static string NormalizeCommissionTab(string? tab)
    {
        tab = (tab ?? "mine").Trim().ToLowerInvariant();
        return tab is ("mine" or "businessarea" or "directorate" or "all") ? tab : "mine";
    }

    /// <summary>
    /// Loads products for the commission scope tab. &quot;Mine&quot; uses four parallel user-role API calls only;
    /// other tabs use the full published catalogue (<see cref="IProductsApiService.GetAllProductsAsync"/> with no user filter).
    /// </summary>
    /// <remarks>
    /// Passing a user email to <c>GetAllProductsAsync</c> filters to products where that user appears in CMS
    /// <c>product_contacts</c> only — not the full catalogue — so &quot;all&quot; / business area / directorate tabs would often show no rows.
    /// </remarks>
    private async Task<List<ProductDto>> GetProductsForCommissionTabAsync(string userEmail, string? tab, CancellationToken cancellationToken = default)
    {
        tab = NormalizeCommissionTab(tab);
        if (tab == "mine")
            return await CommissionReportingProductScope.GetUserProductsForReportingAsync(userEmail, _productsApi);

        var allCatalog = await _productsApi.GetAllProductsAsync(null);
        return CommissionReportingProductScope.GetAllActivePublishedEligible(allCatalog);
    }

    private async Task<List<ProductDto>> GetCommissionScopeProductsAsync(
        string userEmail,
        string tab,
        Commission commission,
        CancellationToken cancellationToken)
    {
        var scopeProducts = (await GetProductsForCommissionTabAsync(userEmail, tab, cancellationToken))
            .Where(p => CommissionReportingProductScope.ProductMatchesCommissionInScopeRules(commission, p))
            .ToList();

        var eligibilityCache = await _eligibilityService.LoadEligibilityCacheAsync();
        return scopeProducts
            .Where(p => !_eligibilityService.IsProductExcludedForCommission(p, commission, eligibilityCache))
            .ToList();
    }

    private async Task<bool> IsProductExcludedFromCommissionAsync(
        ProductDto product,
        Commission commission,
        CancellationToken cancellationToken)
    {
        var eligibilityCache = await _eligibilityService.LoadEligibilityCacheAsync();
        return _eligibilityService.IsProductExcludedForCommission(product, commission, eligibilityCache);
    }

    /// <summary>Commission periods (open vs closed / other).</summary>
    [HttpGet("")]
    [HttpGet("commissions")]
    public async Task<IActionResult> Index(string tab = "active", CancellationToken cancellationToken = default)
    {
        SetChrome("perf-dashboard");

        tab = (tab ?? "active").Trim().ToLowerInvariant();
        if (tab != "completed")
            tab = "active";

        var now = DateTime.UtcNow;
        var all = await _context.Commissions.AsNoTracking()
            .OrderByDescending(c => c.DueDate)
            .ToListAsync(cancellationToken);

        var open = new List<ModernPerformanceCommissionListRow>();
        var closed = new List<ModernPerformanceCommissionListRow>();

        foreach (var c in all)
        {
            var isOpen = IsOpenForSubmissionWindow(c, now);
            var isPastDue = now > c.DueDate;
            var isNotYetOpen = c.IsActive && now < c.OpenDate;

            var row = new ModernPerformanceCommissionListRow
            {
                Commission = c,
                IsOpenForSubmission = isOpen,
                IsPastDue = isPastDue,
                IsNotYetOpen = isNotYetOpen
            };

            if (isOpen)
                open.Add(row);
            else
                closed.Add(row);
        }

        var vm = new ModernPerformanceCommissionsViewModel
        {
            ActiveTab = tab,
            OpenForSubmission = open,
            ClosedOrOther = closed
        };

        return View(vm);
    }

    /// <summary>Guidance page showing all performance metrics with descriptions, rules and criteria.</summary>
    [HttpGet("guidance")]
    public async Task<IActionResult> Guidance(CancellationToken cancellationToken = default)
    {
        SetChrome("perf-guidance");

        var metrics = await _context.PerformanceMetrics.AsNoTracking()
            .Where(m => !m.IsDisabled)
            .OrderBy(m => m.Identifier)
            .ToListAsync(cancellationToken);

        return View(metrics);
    }

    /// <summary>Products in scope for a commission (tabs: mine, business area, directorate, all).</summary>
    [HttpGet("commission/{commissionId:int}")]
    public async Task<IActionResult> Commission(
        int commissionId,
        string tab = "mine",
        string? businessArea = null,
        string? directorate = null,
        string? search = null,
        CancellationToken cancellationToken = default)
    {
        SetChrome("perf-dashboard");

        tab = NormalizeCommissionTab(tab);

        var commission = await _context.Commissions.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == commissionId, cancellationToken);
        if (commission == null)
        {
            TempData["ErrorMessage"] = "Commission not found.";
            return RedirectToAction(nameof(Index));
        }

        var now = DateTime.UtcNow;
        var isOpen = IsOpenForSubmissionWindow(commission, now);
        var isPastDue = now > commission.DueDate;

        var userEmail = GetUserEmail();
        if (string.IsNullOrEmpty(userEmail))
        {
            TempData["ErrorMessage"] = "Unable to identify user.";
            return RedirectToAction(nameof(Index));
        }

        var scopeProductsFiltered = await GetCommissionScopeProductsAsync(userEmail, tab, commission, cancellationToken);

        var submissionList = await _context.CommissionSubmissions.AsNoTracking()
            .Include(cs => cs.MetricValues)
            .Where(cs => cs.CommissionId == commissionId)
            .ToListAsync(cancellationToken);
        var submissionsDict = submissionList.ToDictionary(cs => cs.ProductDocumentId, cs => cs);

        var metricsForPeriod =
            await CommissionReportingMetricsHelper.LoadEnabledMetricsForCommissionPeriodAsync(_context, commission,
                cancellationToken);

        var allScopeRows = scopeProductsFiltered.Select(p =>
        {
            var docId = p.DocumentId ?? "";
            submissionsDict.TryGetValue(docId, out var sub);
            var existing = sub?.MetricValues?.ToList() ?? new List<CommissionMetricValue>();
            var applicable = CommissionReportingMetricsHelper.FilterApplicableMetricsForProduct(commission, p,
                metricsForPeriod, existing);
            var total = applicable.Count;
            var completed = applicable.Count(m =>
                existing.Any(mv => mv.PerformanceMetricId == m.Id && mv.IsComplete));
            return new CommissionSubmissionStatusViewModel
            {
                Product = p,
                Commission = commission,
                Submission = sub,
                Status = sub?.Status ?? CommissionSubmissionStatus.NotStarted,
                CompletedMetrics = completed,
                TotalMetrics = total,
                IsOpen = isOpen,
                IsPastDue = isPastDue
            };
        }).ToList();

        var businessAreaOptions = allScopeRows
            .Select(r => CommissionReportingProductScope.GetBusinessArea(r.Product) ?? "Unassigned")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var directorateOptions = allScopeRows
            .Select(r => CommissionReportingProductScope.GetDirectorate(r.Product) ?? "Unassigned")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        IEnumerable<CommissionSubmissionStatusViewModel> query = allScopeRows;
        if (!string.IsNullOrWhiteSpace(businessArea))
        {
            var ba = businessArea.Trim();
            query = query.Where(r =>
                string.Equals(
                    CommissionReportingProductScope.GetBusinessArea(r.Product) ?? "Unassigned",
                    ba,
                    StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(directorate))
        {
            var dir = directorate.Trim();
            query = query.Where(r =>
                string.Equals(
                    CommissionReportingProductScope.GetDirectorate(r.Product) ?? "Unassigned",
                    dir,
                    StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var q = search.Trim();
            query = query.Where(r =>
                (r.Product.Title ?? "").Contains(q, StringComparison.OrdinalIgnoreCase) ||
                (r.Product.FipsId ?? "").Contains(q, StringComparison.OrdinalIgnoreCase) ||
                (r.Product.DocumentId ?? "").Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        var filteredRows = query
            .OrderBy(r => r.Product.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        List<CommissionProductGroupSection>? grouped = null;
        if (tab == "businessarea")
        {
            grouped = filteredRows
                .GroupBy(r => CommissionReportingProductScope.GetBusinessArea(r.Product) ?? "Unassigned",
                    StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => new CommissionProductGroupSection
                {
                    GroupHeading = g.Key,
                    Rows = g.OrderBy(r => r.Product.Title, StringComparer.OrdinalIgnoreCase).ToList()
                })
                .ToList();
        }
        else if (tab == "directorate")
        {
            grouped = filteredRows
                .GroupBy(r => CommissionReportingProductScope.GetDirectorate(r.Product) ?? "Unassigned",
                    StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => new CommissionProductGroupSection
                {
                    GroupHeading = g.Key,
                    Rows = g.OrderBy(r => r.Product.Title, StringComparer.OrdinalIgnoreCase).ToList()
                })
                .ToList();
        }

        var vm = new ModernPerformanceCommissionDetailViewModel
        {
            Commission = commission,
            Tab = tab,
            Rows = filteredRows,
            GroupedProductRows = grouped,
            ScopeProductCount = allScopeRows.Count,
            BusinessAreaFilter = businessArea,
            DirectorateFilter = directorate,
            Search = search,
            BusinessAreaOptions = businessAreaOptions,
            DirectorateOptions = directorateOptions,
            IsOpenForSubmission = isOpen,
            IsPastDue = isPastDue
        };

        return View(vm);
    }

    /// <summary>Read-only view of a submitted commission return for one product.</summary>
    [HttpGet("commission/{commissionId:int}/submission")]
    public async Task<IActionResult> Submission(
        int commissionId,
        [FromQuery] string? documentId,
        [FromQuery] string? returnTab = null,
        CancellationToken cancellationToken = default)
    {
        SetChrome("perf-dashboard");

        if (string.IsNullOrWhiteSpace(documentId))
        {
            TempData["ErrorMessage"] = "Product is required.";
            return RedirectToAction(nameof(Commission), new { commissionId, tab = NormalizeCommissionTab(returnTab) });
        }

        documentId = documentId.Trim();
        var returnTabNorm = NormalizeCommissionTab(returnTab);

        var commission = await _context.Commissions.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == commissionId, cancellationToken);
        if (commission == null)
        {
            TempData["ErrorMessage"] = "Commission not found.";
            return RedirectToAction(nameof(Index));
        }

        var product = await _productsApi.GetProductByDocumentIdAsync(documentId)
                      ?? await _productsApi.GetProductByFipsIdAsync(documentId);
        if (product == null)
        {
            TempData["ErrorMessage"] = "Product not found.";
            return RedirectToAction(nameof(Commission), new { commissionId, tab = returnTabNorm });
        }

        var productDocumentId = product.DocumentId ?? documentId;

        var submission = await _context.CommissionSubmissions
            .AsNoTracking()
            .Include(s => s.MetricValues)
            .ThenInclude(mv => mv.PerformanceMetric)
            .FirstOrDefaultAsync(
                s => s.CommissionId == commissionId && s.ProductDocumentId == productDocumentId,
                cancellationToken);

        if (submission == null)
        {
            TempData["ErrorMessage"] = "No submission found for this product.";
            return RedirectToAction(nameof(Commission), new { commissionId, tab = returnTabNorm });
        }

        if (submission.Status is not (CommissionSubmissionStatus.Submitted or CommissionSubmissionStatus.Late))
        {
            return RedirectToAction(nameof(CommissionSubmit), new { commissionId, documentId = productDocumentId, returnTab = returnTabNorm });
        }

        var applicable = await CommissionReportingMetricsHelper.GetApplicableMetricsForProductAsync(
            _context, product, commissionId, cancellationToken);
        var applicableIds = applicable.Select(m => m.Id).ToHashSet();

        var metricRows = submission.MetricValues
            .Where(mv => mv.PerformanceMetric != null && applicableIds.Contains(mv.PerformanceMetricId))
            .OrderBy(mv => mv.PerformanceMetric!.Identifier, StringComparer.OrdinalIgnoreCase)
            .Select(mv => new ModernPerformanceSubmissionMetricRow
            {
                Title = mv.PerformanceMetric!.Title,
                Description = mv.PerformanceMetric.Description,
                Identifier = mv.PerformanceMetric.Identifier,
                ValueDisplay = FormatCommissionMetricValueDisplay(mv),
                IsComplete = mv.IsComplete,
                IsNotCaptured = mv.IsNotCaptured,
                NotCapturedReason = mv.NotCapturedReason,
                ReasonForDifference = mv.ReasonForDifference
            })
            .ToList();

        var now = DateTime.UtcNow;
        var isPastDue = now > commission.DueDate;
        var delegatedUnlock =
            await CurrentUserMayOverridePerformanceCommissionLocksAsync(product, cancellationToken);

        var vm = new ModernPerformanceSubmissionViewModel
        {
            Commission = commission,
            Product = product,
            Submission = submission,
            ReturnTab = returnTabNorm,
            MetricRows = metricRows,
            IsPastDue = isPastDue,
            CanReopen = !isPastDue || delegatedUnlock
        };

        return View(vm);
    }

    /// <summary>Enter or edit commission metrics for one product (modern UI).</summary>
    [HttpGet("commission/{commissionId:int}/submit")]
    public async Task<IActionResult> CommissionSubmit(
        int commissionId,
        [FromQuery] string? documentId,
        [FromQuery] string? returnTab = null,
        CancellationToken cancellationToken = default)
    {
        SetChrome("perf-dashboard");

        if (string.IsNullOrWhiteSpace(documentId))
        {
            TempData["ErrorMessage"] = "Product is required.";
            return RedirectToAction(nameof(Commission), new { commissionId, tab = NormalizeCommissionTab(returnTab) });
        }

        documentId = documentId.Trim();
        var returnTabNorm = NormalizeCommissionTab(returnTab);

        var commission = await _context.Commissions
            .FirstOrDefaultAsync(c => c.Id == commissionId, cancellationToken);
        if (commission == null || !commission.IsActive)
        {
            TempData["ErrorMessage"] = "Commission not found or is not active.";
            return RedirectToAction(nameof(Index));
        }

        var now = DateTime.UtcNow;
        if (now < commission.OpenDate)
        {
            TempData["WarningMessage"] =
                $"This commission is not yet open. It will be available from {commission.OpenDate:dd MMM yyyy}.";
            return RedirectToAction(nameof(Commission), new { commissionId, tab = returnTabNorm });
        }

        var product = await _productsApi.GetProductByDocumentIdAsync(documentId)
                      ?? await _productsApi.GetProductByFipsIdAsync(documentId);
        if (product == null)
            return NotFound();

        if (!CommissionReportingProductScope.ProductMatchesCommissionInScopeRules(commission, product))
        {
            TempData["ErrorMessage"] =
                "This product is not in scope for this commission (phase and type rules are set on the commission in Admin).";
            return RedirectToAction(nameof(Commission), new { commissionId, tab = returnTabNorm });
        }

        if (await IsProductExcludedFromCommissionAsync(product, commission, cancellationToken))
        {
            TempData["ErrorMessage"] = "This product is excluded from performance reporting for this commission period.";
            return RedirectToAction(nameof(Commission), new { commissionId, tab = returnTabNorm });
        }

        var (submission, metricValues) =
            await CommissionReportingSubmissionHelper.EnsureSubmissionAndMetricRowsAsync(_context, product, commissionId,
                cancellationToken);

        var isPastDue = now > commission.DueDate;
        var delegatedUnlock = await CurrentUserMayOverridePerformanceCommissionLocksAsync(product, cancellationToken);
        var isReadOnly = isPastDue && !delegatedUnlock;

        var productTypes = product.CategoryValues?
            .Where(cv => cv.CategoryType?.Name?.Equals("Type", StringComparison.OrdinalIgnoreCase) == true)
            .ToList() ?? new List<CategoryValueDto>();
        var hasNoProductTypes = productTypes.Count == 0;

        var vm = new ModernPerformanceCommissionSubmitViewModel
        {
            Commission = commission,
            Product = product,
            Submission = submission,
            MetricValues = metricValues,
            ReturnTab = returnTabNorm,
            IsReadOnly = isReadOnly,
            IsPastDue = isPastDue,
            HasNoProductTypes = hasNoProductTypes
        };

        return View(vm);
    }

    /// <summary>AJAX save for a single metric cell (aligned with legacy product reporting save rules).</summary>
    [HttpPost("commission/metric-value")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveCommissionMetricValue(
        int id,
        string? value,
        bool isNotCaptured = false,
        string? notCapturedReason = null,
        string? reasonForDifference = null)
    {
        var metricValue = await _context.CommissionMetricValues
            .Include(cmv => cmv.PerformanceMetric)
            .Include(cmv => cmv.CommissionSubmission)
            .ThenInclude(cs => cs!.Commission)
            .FirstOrDefaultAsync(cmv => cmv.Id == id);

        if (metricValue == null)
            return Json(new { success = false, message = "Metric value not found" });

        if (metricValue.CommissionSubmission?.Status is CommissionSubmissionStatus.Submitted
            or CommissionSubmissionStatus.Late)
            return Json(new { success = false, message = "This submission has been submitted and cannot be edited" });

        var now = DateTime.UtcNow;
        var submissionCommission = metricValue.CommissionSubmission?.Commission;
        if (submissionCommission != null && now > submissionCommission.DueDate)
        {
            var docId = metricValue.CommissionSubmission?.ProductDocumentId;
            ProductDto? productForPerm = null;
            if (!string.IsNullOrEmpty(docId))
            {
                productForPerm = await _productsApi.GetProductByDocumentIdAsync(docId)
                                 ?? await _productsApi.GetProductByFipsIdAsync(docId);
            }

            if (!await CurrentUserMayOverridePerformanceCommissionLocksAsync(productForPerm, CancellationToken.None))
                return Json(new { success = false, message = "This commission is past due and cannot be edited" });
        }

        try
        {
            if (isNotCaptured)
            {
                metricValue.Value = string.Empty;
                metricValue.IsComplete = true;
                metricValue.IsNotCaptured = true;
                metricValue.NotCapturedReason = notCapturedReason;
            }
            else
            {
                if (metricValue.PerformanceMetric != null && !string.IsNullOrWhiteSpace(value))
                {
                    var validationResult =
                        CommissionReportingMetricsHelper.ValidateMetricValue(value, metricValue.PerformanceMetric);
                    if (!validationResult.IsValid)
                        return Json(new { success = false, message = validationResult.ErrorMessage });
                }

                metricValue.Value = value;
                metricValue.IsComplete = !string.IsNullOrWhiteSpace(value);
                metricValue.IsNotCaptured = false;
                metricValue.NotCapturedReason = null;
            }

            metricValue.ReasonForDifference = reasonForDifference;
            metricValue.UpdatedAt = now;

            if (metricValue.CommissionSubmission != null)
            {
                var allMetricValues = await _context.CommissionMetricValues
                    .Where(cmv => cmv.CommissionSubmissionId == metricValue.CommissionSubmission.Id)
                    .ToListAsync();
                CommissionReportingSubmissionHelper.RecalculateSubmissionStatus(metricValue.CommissionSubmission,
                    allMetricValues);
            }

            await _context.SaveChangesAsync();

            return Json(new { success = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving commission metric value {Id}", id);
            return Json(new { success = false, message = "An error occurred while saving the value" });
        }
    }

    [HttpPost("commission/{commissionId:int}/submit/confirm")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CommissionSubmitConfirm(
        int commissionId,
        int id,
        string? comments,
        string? returnTab,
        CancellationToken cancellationToken = default)
    {
        var submission = await _context.CommissionSubmissions
            .Include(cs => cs.Commission)
            .Include(cs => cs.MetricValues)
            .ThenInclude(cmv => cmv.PerformanceMetric)
            .FirstOrDefaultAsync(cs => cs.Id == id, cancellationToken);

        if (submission == null)
            return NotFound();

        if (submission.CommissionId != commissionId)
        {
            TempData["ErrorMessage"] = "Submission does not match this commission.";
            return RedirectToAction(nameof(Commission), new { commissionId, tab = NormalizeCommissionTab(returnTab) });
        }

        if (submission.Status is CommissionSubmissionStatus.Submitted or CommissionSubmissionStatus.Late)
        {
            TempData["ErrorMessage"] = "This submission has already been submitted.";
            return RedirectToAction(nameof(CommissionSubmit),
                new
                {
                    commissionId = submission.CommissionId,
                    documentId = submission.ProductDocumentId,
                    returnTab = NormalizeCommissionTab(returnTab)
                });
        }

        var products = await _productsApi.GetProductsAsync();
        var product = products?.FirstOrDefault(p =>
            p.DocumentId == submission.ProductDocumentId || p.FipsId == submission.FipsId);
        if (product == null)
        {
            TempData["ErrorMessage"] = "Product not found. Please contact support.";
            return RedirectToAction(nameof(Commission), new { commissionId, tab = NormalizeCommissionTab(returnTab) });
        }

        var applicableMetrics =
            await CommissionReportingMetricsHelper.GetApplicableMetricsForProductAsync(_context, product,
                submission.CommissionId, cancellationToken);

        var existingMetricValues = submission.MetricValues
            .Where(mv => mv.PerformanceMetric != null &&
                        applicableMetrics.Any(m => m.Id == mv.PerformanceMetric!.Id))
            .ToList();

        var incompleteMetrics = new List<string>();
        foreach (var metric in applicableMetrics)
        {
            var metricValue = existingMetricValues.FirstOrDefault(mv => mv.PerformanceMetricId == metric.Id);
            var isRequired = false;
            try
            {
                if (!string.IsNullOrEmpty(metric.ValidationRules))
                {
                    var rules = JsonSerializer.Deserialize<ValidationRules>(metric.ValidationRules);
                    isRequired = rules?.Required == true && rules?.AllowNull != true;
                }
            }
            catch
            {
                // ignore parse errors — treat as optional
            }

            if (isRequired && (metricValue == null || !metricValue.IsComplete))
                incompleteMetrics.Add(metric.Title);
        }

        if (incompleteMetrics.Count > 0)
        {
            TempData["ErrorMessage"] =
                $"Please complete all required metrics before submitting. Missing: {string.Join(", ", incompleteMetrics)}";
            return RedirectToAction(nameof(CommissionSubmit),
                new
                {
                    commissionId = submission.CommissionId,
                    documentId = submission.ProductDocumentId,
                    returnTab = NormalizeCommissionTab(returnTab)
                });
        }

        var now = DateTime.UtcNow;
        if (submission.Commission != null && now > submission.Commission.DueDate)
            submission.Status = CommissionSubmissionStatus.Late;
        else
            submission.Status = CommissionSubmissionStatus.Submitted;

        submission.SubmittedDate = now;
        submission.SubmittedBy = User.Identity?.Name ?? "unknown";
        submission.UpdatedAt = now;
        submission.Comments = string.IsNullOrWhiteSpace(comments) ? null : comments.Trim();

        await _context.SaveChangesAsync(cancellationToken);

        TempData["SuccessMessage"] = "Commission submission completed successfully.";
        return RedirectToAction(nameof(Commission),
            new { commissionId = submission.CommissionId, tab = NormalizeCommissionTab(returnTab) });
    }

    [HttpPost("commission/unsubmit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CommissionUnsubmit(int id, string? returnTab)
    {
        var submission = await _context.CommissionSubmissions
            .Include(cs => cs.Commission)
            .FirstOrDefaultAsync(cs => cs.Id == id);

        if (submission == null)
        {
            TempData["ErrorMessage"] = "Submission not found.";
            return RedirectToAction(nameof(Index));
        }

        if (submission.Status is not (CommissionSubmissionStatus.Submitted or CommissionSubmissionStatus.Late))
        {
            TempData["ErrorMessage"] = "This submission has not been submitted yet.";
            return RedirectToAction(nameof(CommissionSubmit),
                new
                {
                    commissionId = submission.CommissionId,
                    documentId = submission.ProductDocumentId,
                    returnTab = NormalizeCommissionTab(returnTab)
                });
        }

        var now = DateTime.UtcNow;
        if (submission.Commission != null && now > submission.Commission.DueDate)
        {
            var docIdUnsub = submission.ProductDocumentId;
            var productForPermUnsub = !string.IsNullOrEmpty(docIdUnsub)
                ? await _productsApi.GetProductByDocumentIdAsync(docIdUnsub)
                  ?? await _productsApi.GetProductByFipsIdAsync(docIdUnsub)
                : null;
            if (!await CurrentUserMayOverridePerformanceCommissionLocksAsync(productForPermUnsub,
                    CancellationToken.None))
            {
                TempData["ErrorMessage"] =
                    "This submission cannot be unsubmitted because the commission due date has passed.";
                return RedirectToAction(nameof(CommissionSubmit),
                    new
                    {
                        commissionId = submission.CommissionId,
                        documentId = submission.ProductDocumentId,
                        returnTab = NormalizeCommissionTab(returnTab)
                    });
            }
        }

        try
        {
            var metricValues = await _context.CommissionMetricValues
                .Where(mv => mv.CommissionSubmissionId == submission.Id)
                .ToListAsync();
            var allComplete = metricValues.All(mv => mv.IsComplete);
            submission.Status = allComplete ? CommissionSubmissionStatus.InProgress : CommissionSubmissionStatus.NotStarted;
            submission.SubmittedDate = null;
            submission.SubmittedBy = null;
            submission.UpdatedAt = now;

            await _context.SaveChangesAsync();

            TempData["SuccessMessage"] =
                "Commission submission has been unsubmitted and is now available for editing.";
            return RedirectToAction(nameof(CommissionSubmit),
                new
                {
                    commissionId = submission.CommissionId,
                    documentId = submission.ProductDocumentId,
                    returnTab = NormalizeCommissionTab(returnTab)
                });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error unsubmitting commission submission");
            TempData["ErrorMessage"] = "An error occurred while unsubmitting the commission submission.";
            return RedirectToAction(nameof(CommissionSubmit),
                new
                {
                    commissionId = submission.CommissionId,
                    documentId = submission.ProductDocumentId,
                    returnTab = NormalizeCommissionTab(returnTab)
                });
        }
    }

    [HttpPost("commission/comments")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CommissionUpdateComments(int id, string? comments, string? returnTab)
    {
        var submission = await _context.CommissionSubmissions
            .Include(cs => cs.Commission)
            .FirstOrDefaultAsync(cs => cs.Id == id);

        if (submission == null)
        {
            TempData["ErrorMessage"] = "Submission not found.";
            return RedirectToAction(nameof(Index));
        }

        if (submission.Status is not (CommissionSubmissionStatus.Submitted or CommissionSubmissionStatus.Late))
        {
            TempData["ErrorMessage"] = "Comments can only be updated for submitted commissions.";
            return RedirectToAction(nameof(CommissionSubmit),
                new
                {
                    commissionId = submission.CommissionId,
                    documentId = submission.ProductDocumentId,
                    returnTab = NormalizeCommissionTab(returnTab)
                });
        }

        var now = DateTime.UtcNow;
        if (submission.Commission != null && now > submission.Commission.DueDate)
        {
            var docIdComments = submission.ProductDocumentId;
            var productForPermComments = !string.IsNullOrEmpty(docIdComments)
                ? await _productsApi.GetProductByDocumentIdAsync(docIdComments)
                  ?? await _productsApi.GetProductByFipsIdAsync(docIdComments)
                : null;
            if (!await CurrentUserMayOverridePerformanceCommissionLocksAsync(productForPermComments,
                    CancellationToken.None))
            {
                TempData["ErrorMessage"] =
                    "Comments cannot be updated because the commission due date has passed.";
                return RedirectToAction(nameof(CommissionSubmit),
                    new
                    {
                        commissionId = submission.CommissionId,
                        documentId = submission.ProductDocumentId,
                        returnTab = NormalizeCommissionTab(returnTab)
                    });
            }
        }

        try
        {
            submission.Comments = string.IsNullOrWhiteSpace(comments) ? null : comments.Trim();
            submission.UpdatedAt = now;
            await _context.SaveChangesAsync();

            TempData["SuccessMessage"] = "Comments have been updated successfully.";
            return RedirectToAction(nameof(CommissionSubmit),
                new
                {
                    commissionId = submission.CommissionId,
                    documentId = submission.ProductDocumentId,
                    returnTab = NormalizeCommissionTab(returnTab)
                });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating commission comments");
            TempData["ErrorMessage"] = "An error occurred while updating the comments.";
            return RedirectToAction(nameof(CommissionSubmit),
                new
                {
                    commissionId = submission.CommissionId,
                    documentId = submission.ProductDocumentId,
                    returnTab = NormalizeCommissionTab(returnTab)
                });
        }
    }

    private static string FormatCommissionMetricValueDisplay(CommissionMetricValue mv)
    {
        if (mv.IsNotCaptured)
            return "Not captured";
        if (!string.IsNullOrEmpty(mv.Value))
            return mv.Value;
        if (mv.IsComplete && string.IsNullOrEmpty(mv.Value))
            return "Unreported";
        return "Not entered";
    }

    [HttpGet("commission/{commissionId:int}/export")]
    public async Task<IActionResult> ExportExcel(
        int commissionId,
        string tab = "mine",
        CancellationToken cancellationToken = default)
    {
        var commission = await _context.Commissions.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == commissionId, cancellationToken);
        if (commission == null)
            return NotFound();

        var userEmail = GetUserEmail();
        if (string.IsNullOrEmpty(userEmail))
            return Unauthorized();

        tab = NormalizeCommissionTab(tab);

        var scopeProducts = await GetCommissionScopeProductsAsync(userEmail, tab, commission, cancellationToken);

        var templateMetrics =
            await CommissionReportingMetricsHelper.LoadEnabledMetricsForCommissionPeriodAsync(_context, commission,
                cancellationToken);
        var includedIds = CommissionReportingMetricsHelper.ParseIncludedMetricIds(commission.IncludedPerformanceMetricIds);
        if (includedIds != null)
            templateMetrics = templateMetrics.Where(m => includedIds.Contains(m.Id)).ToList();

        var submissions = await _context.CommissionSubmissions.AsNoTracking()
            .Include(cs => cs.MetricValues)
            .ThenInclude(mv => mv.PerformanceMetric)
            .Where(cs => cs.CommissionId == commissionId)
            .ToListAsync(cancellationToken);
        var subByDoc = submissions.ToDictionary(cs => cs.ProductDocumentId, cs => cs);

        using var workbook = new XLWorkbook();
        var ws = workbook.Worksheets.Add("Commission metrics");
        const string textFormat = "@";

        var col = 1;
        ws.Cell(1, col).Value = "DocumentId";
        ws.Cell(1, ++col).Value = "Title";
        ws.Cell(1, ++col).Value = "BusinessArea";
        foreach (var m in templateMetrics)
            ws.Cell(1, ++col).Value = m.Identifier;

        ws.Row(1).Style.Font.Bold = true;

        var row = 2;
        foreach (var p in scopeProducts.OrderBy(x => x.Title, StringComparer.OrdinalIgnoreCase))
        {
            var docId = p.DocumentId ?? "";
            subByDoc.TryGetValue(docId, out var sub);
            col = 1;

            var cDoc = ws.Cell(row, col++);
            cDoc.Style.NumberFormat.Format = textFormat;
            cDoc.Value = docId;

            ws.Cell(row, col++).Value = p.Title;

            ws.Cell(row, col++).Value = CommissionReportingProductScope.GetBusinessArea(p) ?? "";

            foreach (var m in templateMetrics)
            {
                var mv = sub?.MetricValues?.FirstOrDefault(x => x.PerformanceMetricId == m.Id);
                ws.Cell(row, col++).Value = mv?.Value ?? "";
            }

            row++;
        }

        ws.Columns().AdjustToContents();

        using var outMs = new MemoryStream();
        workbook.SaveAs(outMs);
        var fname = $"commission-{commissionId}-products-metrics.xlsx";
        return File(outMs.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fname);
    }

    [HttpGet("commission/{commissionId:int}/bulk")]
    public async Task<IActionResult> BulkUpload(
        int commissionId,
        string tab = "mine",
        CancellationToken cancellationToken = default)
    {
        SetChrome("perf-dashboard");

        var commission = await _context.Commissions.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == commissionId, cancellationToken);
        if (commission == null)
            return NotFound();

        var vm = new ModernPerformanceBulkUploadViewModel
        {
            CommissionId = commissionId,
            CommissionName = commission.Name,
            Tab = NormalizeCommissionTab(tab)
        };
        return View(vm);
    }

    public sealed class CommissionBulkVerifiedPayload
    {
        public string UserEmail { get; set; } = "";
        public int CommissionId { get; set; }
        public string Tab { get; set; } = "mine";
        public List<ParsedBulkRow> Rows { get; set; } = new();
    }

    public sealed class ParsedBulkRow
    {
        public int RowNumber { get; set; }
        public string DocumentId { get; set; } = "";
        /// <summary>From the FipsId column when present; used to resolve the product if DocumentId is missing or not found.</summary>
        public string? FipsIdFromFile { get; set; }
        public Dictionary<string, string?> MetricValuesByIdentifier { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    [HttpPost("commission/{commissionId:int}/bulk/verify")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> BulkVerify(
        int commissionId,
        IFormFile file,
        string tab = "mine",
        CancellationToken cancellationToken = default)
    {
        var userEmail = GetUserEmail();
        if (string.IsNullOrEmpty(userEmail))
        {
            TempData["ErrorMessage"] = "Unable to identify user.";
            return RedirectToAction(nameof(BulkUpload), new { commissionId, tab });
        }

        var commission = await _context.Commissions
            .FirstOrDefaultAsync(c => c.Id == commissionId, cancellationToken);
        if (commission == null)
            return NotFound();

        if (file == null || file.Length == 0)
        {
            TempData["ErrorMessage"] = "Choose an Excel (.xlsx) or CSV file to upload.";
            return RedirectToAction(nameof(BulkUpload), new { commissionId, tab });
        }

        tab = NormalizeCommissionTab(tab);

        List<ParsedBulkRow> parsed;
        try
        {
            await using var stream = file.OpenReadStream();
            var ext = Path.GetExtension(file.FileName ?? "").ToLowerInvariant();
            var isExcel = ext is ".xlsx" or ".xlsm" ||
                          string.Equals(file.ContentType, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", StringComparison.OrdinalIgnoreCase);
            parsed = isExcel
                ? await ParseBulkExcelAsync(stream, cancellationToken)
                : await ParseBulkCsvAsync(stream, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Bulk upload parse failed");
            TempData["ErrorMessage"] =
                "Could not read the file. Use the Download Excel template, or a UTF-8 CSV with the same columns.";
            return RedirectToAction(nameof(BulkUpload), new { commissionId, tab });
        }

        var catalog = await BuildProductLookupAsync(userEmail, cancellationToken);
        var allowedDocIds = await GetScopeDocumentIdsAsync(userEmail, tab, commission, cancellationToken);

        var verifyLines = new List<ModernPerformanceBulkVerifyLine>();
        var blocking = false;

        await using var tx = await _context.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            foreach (var row in parsed)
            {
                var line = new ModernPerformanceBulkVerifyLine
                {
                    RowNumber = row.RowNumber,
                    DocumentId = row.DocumentId
                };

                if (!TryResolveCatalogProduct(row, catalog, out var product) || product == null)
                {
                    line.Errors.Add(
                        "Product not found. Check the DocumentId (or FipsId) column matches the template and was not changed by Excel (IDs must stay text).");
                    verifyLines.Add(line);
                    blocking = true;
                    continue;
                }

                line.ProductTitle = product.Title;

                var canonicalDoc = product.DocumentId ?? NormalizeBulkKey(row.DocumentId);
                if (string.IsNullOrEmpty(canonicalDoc))
                    canonicalDoc = NormalizeBulkKey(row.FipsIdFromFile ?? "");
                if (!allowedDocIds.Contains(canonicalDoc))
                {
                    line.Errors.Add("Product is not in scope for the selected tab.");
                    verifyLines.Add(line);
                    blocking = true;
                    continue;
                }

                var rowErr = await ApplyParsedRowAsync(commission, product, row, cancellationToken);
                if (rowErr.Count > 0)
                {
                    line.Errors.AddRange(rowErr);
                    blocking = true;
                }

                verifyLines.Add(line);
            }

            await tx.RollbackAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(cancellationToken);
            _logger.LogError(ex, "Bulk verify transaction failed");
            TempData["ErrorMessage"] = "Verification failed unexpectedly. Try again or use single-product entry.";
            return RedirectToAction(nameof(BulkUpload), new { commissionId, tab });
        }

        var token = "";
        if (!blocking)
        {
            token = Guid.NewGuid().ToString("N");
            var cacheKey = BulkCacheKeyPrefix + userEmail + ":" + token;
            _cache.Set(
                cacheKey,
                new CommissionBulkVerifiedPayload
                {
                    UserEmail = userEmail,
                    CommissionId = commissionId,
                    Tab = tab,
                    Rows = parsed
                },
                new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1) });
        }

        var vm = new ModernPerformanceBulkVerifyViewModel
        {
            CommissionId = commissionId,
            CommissionName = commission.Name,
            Tab = tab,
            Token = token,
            Lines = verifyLines,
            CanApply = !blocking,
            StagedRowCount = parsed.Count
        };

        return View("BulkVerify", vm);
    }

    [HttpPost("commission/{commissionId:int}/bulk/apply")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> BulkApply(int commissionId, string token, CancellationToken cancellationToken = default)
    {
        var userEmail = GetUserEmail();
        if (string.IsNullOrEmpty(userEmail))
        {
            TempData["ErrorMessage"] = "Unable to identify user.";
            return RedirectToAction(nameof(Index));
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            TempData["ErrorMessage"] = "Upload session expired. Verify the file again.";
            return RedirectToAction(nameof(BulkUpload), new { commissionId });
        }

        var cacheKey = BulkCacheKeyPrefix + userEmail + ":" + token.Trim();
        if (!_cache.TryGetValue(cacheKey, out CommissionBulkVerifiedPayload? payload) ||
            payload == null ||
            payload.CommissionId != commissionId ||
            !string.Equals(payload.UserEmail, userEmail, StringComparison.OrdinalIgnoreCase))
        {
            TempData["ErrorMessage"] = "Upload session expired or is invalid. Verify the file again.";
            return RedirectToAction(nameof(BulkUpload), new { commissionId });
        }

        var commission = await _context.Commissions
            .FirstOrDefaultAsync(c => c.Id == commissionId, cancellationToken);
        if (commission == null)
            return NotFound();

        var catalog = await BuildProductLookupAsync(userEmail, cancellationToken);
        var allowedDocIds = await GetScopeDocumentIdsAsync(userEmail, payload.Tab, commission, cancellationToken);

        await using var tx = await _context.Database.BeginTransactionAsync(cancellationToken);
        foreach (var row in payload.Rows)
        {
            if (!TryResolveCatalogProduct(row, catalog, out var product) || product == null)
                continue;
            var canonicalDoc = product.DocumentId ?? NormalizeBulkKey(row.DocumentId);
            if (string.IsNullOrEmpty(canonicalDoc))
                canonicalDoc = NormalizeBulkKey(row.FipsIdFromFile ?? "");
            if (!allowedDocIds.Contains(canonicalDoc))
            {
                TempData["ErrorMessage"] =
                    "Apply rejected: product scope no longer matches the verified upload. Verify again.";
                return RedirectToAction(nameof(BulkUpload), new { commissionId });
            }

            var err = await ApplyParsedRowAsync(commission, product, row, cancellationToken);
            if (err.Count > 0)
            {
                TempData["ErrorMessage"] =
                    "Apply failed part-way (data may have changed since verification). Verify again.";
                return RedirectToAction(nameof(BulkUpload), new { commissionId });
            }
        }

        try
        {
            await tx.CommitAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Bulk apply commit failed");
            TempData["ErrorMessage"] = "Bulk apply failed. Verify the file again.";
            return RedirectToAction(nameof(BulkUpload), new { commissionId });
        }

        _cache.Remove(cacheKey);
        TempData["SuccessMessage"] = $"Imported metrics for {payload.Rows.Count} row(s).";
        return RedirectToAction(nameof(Commission), new { commissionId, tab = payload.Tab });
    }

    private async Task<Dictionary<string, ProductDto>> BuildProductLookupAsync(string userEmail, CancellationToken cancellationToken)
    {
        var mineTask = CommissionReportingProductScope.GetUserProductsForReportingAsync(userEmail, _productsApi);
        var allTask = _productsApi.GetAllProductsAsync(null);
        await Task.WhenAll(mineTask, allTask);

        var map = new Dictionary<string, ProductDto>(StringComparer.OrdinalIgnoreCase);
        void Add(ProductDto p)
        {
            if (!string.IsNullOrEmpty(p.DocumentId))
                map[p.DocumentId] = p;
            if (!string.IsNullOrEmpty(p.FipsId))
                map[p.FipsId] = p;
        }

        foreach (var p in await mineTask)
            Add(p);
        foreach (var p in await allTask)
            Add(p);

        return map;
    }

    private async Task<HashSet<string>> GetScopeDocumentIdsAsync(
        string userEmail,
        string tab,
        Commission commission,
        CancellationToken cancellationToken)
    {
        var scopeProducts = await GetCommissionScopeProductsAsync(userEmail, tab, commission, cancellationToken);
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in scopeProducts)
        {
            if (!string.IsNullOrEmpty(p.DocumentId))
                set.Add(p.DocumentId);
        }

        return set;
    }

    private static Task<List<ParsedBulkRow>> ParseBulkCsvAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var cfg = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            MissingFieldFound = null,
            BadDataFound = null
        };
        using var csv = new CsvReader(reader, cfg);
        csv.Read();
        csv.ReadHeader();
        var headers = csv.HeaderRecord ?? Array.Empty<string>();
        var headerList = headers.Select(h => NormalizeBulkHeader(h ?? "")).ToList();

        var docIdx = headerList.FindIndex(h => h.Equals("DocumentId", StringComparison.OrdinalIgnoreCase));
        if (docIdx < 0)
            throw new InvalidOperationException("Missing DocumentId column.");

        var fipsIdx = headerList.FindIndex(h => h.Equals("FipsId", StringComparison.OrdinalIgnoreCase));

        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "DocumentId", "Title", "FipsId", "BusinessArea", "Directorate"
        };

        var metricHeaders = headerList
            .Where(h => !string.IsNullOrEmpty(h) && !reserved.Contains(h))
            .ToList();

        var rows = new List<ParsedBulkRow>();
        var rowNumber = 1;
        while (csv.Read())
        {
            rowNumber++;
            var docId = NormalizeBulkKey(csv.GetField(docIdx));
            var fipsFromFile = fipsIdx >= 0 ? NormalizeBulkKey(csv.GetField(fipsIdx)) : null;
            if (string.IsNullOrEmpty(docId) && string.IsNullOrEmpty(fipsFromFile))
                continue;

            var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var mh in metricHeaders)
            {
                var i = headerList.FindIndex(h => h.Equals(mh, StringComparison.OrdinalIgnoreCase));
                if (i < 0)
                    continue;
                var v = csv.GetField(i)?.Trim();
                if (!string.IsNullOrEmpty(v))
                    dict[mh] = v;
            }

            rows.Add(new ParsedBulkRow
            {
                RowNumber = rowNumber,
                DocumentId = docId,
                FipsIdFromFile = string.IsNullOrEmpty(fipsFromFile) ? null : fipsFromFile,
                MetricValuesByIdentifier = dict
            });
        }

        return Task.FromResult(rows);
    }

    private static async Task<List<ParsedBulkRow>> ParseBulkExcelAsync(Stream stream, CancellationToken cancellationToken)
    {
        await using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, cancellationToken);
        if (ms.Length == 0)
            return new List<ParsedBulkRow>();
        ms.Position = 0;

        using var workbook = new XLWorkbook(ms);
        if (workbook.Worksheets.Count == 0)
            throw new InvalidOperationException("Workbook has no worksheets.");
        var ws = workbook.Worksheet(1);
        var firstRow = ws.FirstRowUsed()?.RowNumber() ?? 1;
        var lastRow = ws.LastRowUsed()?.RowNumber() ?? firstRow;
        var lastCol = ws.Row(firstRow).LastCellUsed()?.Address.ColumnNumber ?? 1;

        var headerList = new List<string>();
        for (var c = 1; c <= lastCol; c++)
            headerList.Add(NormalizeBulkHeader(GetBulkUploadCellText(ws.Cell(firstRow, c))));

        var docIdx = headerList.FindIndex(h => h.Equals("DocumentId", StringComparison.OrdinalIgnoreCase));
        if (docIdx < 0)
            throw new InvalidOperationException("Missing DocumentId column.");

        var fipsIdx = headerList.FindIndex(h => h.Equals("FipsId", StringComparison.OrdinalIgnoreCase));

        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "DocumentId", "Title", "FipsId", "BusinessArea", "Directorate"
        };

        var metricHeaders = headerList
            .Where(h => !string.IsNullOrEmpty(h) && !reserved.Contains(h))
            .ToList();

        var rows = new List<ParsedBulkRow>();
        for (var r = firstRow + 1; r <= lastRow; r++)
        {
            var docId = NormalizeBulkKey(GetBulkUploadCellText(ws.Cell(r, docIdx + 1)));
            var fipsFromFile = fipsIdx >= 0 ? NormalizeBulkKey(GetBulkUploadCellText(ws.Cell(r, fipsIdx + 1))) : null;
            if (string.IsNullOrEmpty(docId) && string.IsNullOrEmpty(fipsFromFile))
                continue;

            var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var mh in metricHeaders)
            {
                var i = headerList.FindIndex(h => h.Equals(mh, StringComparison.OrdinalIgnoreCase));
                if (i < 0)
                    continue;
                var v = GetBulkUploadCellText(ws.Cell(r, i + 1));
                if (!string.IsNullOrEmpty(v))
                    dict[mh] = v;
            }

            rows.Add(new ParsedBulkRow
            {
                RowNumber = r,
                DocumentId = docId,
                FipsIdFromFile = string.IsNullOrEmpty(fipsFromFile) ? null : fipsFromFile,
                MetricValuesByIdentifier = dict
            });
        }

        return rows;
    }

    private static string NormalizeBulkHeader(string? h)
    {
        if (string.IsNullOrEmpty(h))
            return "";
        return h.Trim().TrimStart('\uFEFF');
    }

    private static string NormalizeBulkKey(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return "";
        var t = s.Trim().TrimStart('\uFEFF');
        if (t.Length >= 2 && t[0] == '{' && t[^1] == '}')
            t = t[1..^1].Trim();
        return t;
    }

    private static bool TryResolveCatalogProduct(
        ParsedBulkRow row,
        Dictionary<string, ProductDto> catalog,
        out ProductDto? product)
    {
        product = null;
        var docKey = NormalizeBulkKey(row.DocumentId);
        var fipsKey = NormalizeBulkKey(row.FipsIdFromFile);
        if (!string.IsNullOrEmpty(docKey) && catalog.TryGetValue(docKey, out product))
            return true;
        if (!string.IsNullOrEmpty(fipsKey) && catalog.TryGetValue(fipsKey, out product))
            return true;
        return false;
    }

    private static string GetBulkUploadCellText(IXLCell cell)
    {
        if (cell.IsEmpty())
            return "";

        try
        {
            if (cell.DataType == XLDataType.Text)
                return cell.GetString().Trim();

            if (cell.DataType == XLDataType.Number)
            {
                var d = cell.GetDouble();
                if (d is >= 0 && d <= long.MaxValue && Math.Abs(d - Math.Truncate(d)) < 1e-9)
                    return ((long)d).ToString(CultureInfo.InvariantCulture);
            }

            var s = cell.GetString().Trim();
            return string.IsNullOrEmpty(s) ? cell.Value.ToString()?.Trim() ?? "" : s;
        }
        catch
        {
            return cell.Value.ToString()?.Trim() ?? "";
        }
    }

    private async Task<List<string>> ApplyParsedRowAsync(
        Commission commission,
        ProductDto product,
        ParsedBulkRow row,
        CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        var now = DateTime.UtcNow;

        if (!commission.IsActive)
        {
            errors.Add("Commission is not active.");
            return errors;
        }

        if (now > commission.DueDate)
        {
            errors.Add("Commission is past due; values cannot be updated.");
            return errors;
        }

        if (now < commission.OpenDate)
        {
            errors.Add("Commission is not yet open for submission.");
            return errors;
        }

        if (!CommissionReportingProductScope.ProductMatchesCommissionInScopeRules(commission, product))
        {
            errors.Add("Product is not in scope for this commission (phase/type rules).");
            return errors;
        }

        if (_eligibilityService.IsProductExcludedForCommission(
                product,
                commission,
                await _eligibilityService.LoadEligibilityCacheAsync()))
        {
            errors.Add("Product is excluded from performance reporting for this commission period.");
            return errors;
        }

        var (submission, _) =
            await CommissionReportingSubmissionHelper.EnsureSubmissionAndMetricRowsAsync(_context, product, commission.Id,
                cancellationToken);

        if (submission.Status == CommissionSubmissionStatus.Submitted)
        {
            errors.Add("Submission is already submitted and cannot be edited.");
            return errors;
        }

        for (var pass = 0; pass < 30; pass++)
        {
            var applicable = await CommissionReportingMetricsHelper.GetApplicableMetricsForProductAsync(
                _context, product, commission.Id, cancellationToken);

            var anyUpdate = false;

            foreach (var metric in applicable.OrderBy(m => m.Identifier, StringComparer.OrdinalIgnoreCase))
            {
                if (!row.MetricValuesByIdentifier.TryGetValue(metric.Identifier, out var raw) ||
                    string.IsNullOrWhiteSpace(raw))
                    continue;

                var cmv = await _context.CommissionMetricValues
                    .Include(x => x.PerformanceMetric)
                    .FirstOrDefaultAsync(
                        x => x.CommissionSubmissionId == submission.Id && x.PerformanceMetricId == metric.Id,
                        cancellationToken);

                if (cmv?.PerformanceMetric == null)
                    continue;

                var vr = CommissionReportingMetricsHelper.ValidateMetricValue(raw, cmv.PerformanceMetric);
                if (!vr.IsValid)
                {
                    errors.Add($"{metric.Identifier}: {vr.ErrorMessage}");
                    return errors;
                }

                cmv.Value = raw;
                cmv.IsComplete = !string.IsNullOrWhiteSpace(raw);
                cmv.IsNotCaptured = false;
                cmv.NotCapturedReason = null;
                cmv.UpdatedAt = DateTime.UtcNow;
                anyUpdate = true;
            }

            if (anyUpdate)
            {
                var allMv = await _context.CommissionMetricValues
                    .Where(mv => mv.CommissionSubmissionId == submission.Id)
                    .ToListAsync(cancellationToken);
                CommissionReportingSubmissionHelper.RecalculateSubmissionStatus(submission, allMv);
                await _context.SaveChangesAsync(cancellationToken);
            }
            else
            {
                break;
            }
        }

        return errors;
    }
}
